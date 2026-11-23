using System.Text.RegularExpressions;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// บริหาร SLA แยกตามลูกค้า:
/// <list type="bullet">
/// <item>ลูกค้าแต่ละรายกำหนดระดับ Severity ของตัวเองได้ (รหัส ชื่อ จำนวนระดับ เวลาตอบ/แก้ไข) — ถ้าไม่มีใช้นโยบายมาตรฐาน</item>
/// <item>คำนวณกำหนดเวลา (response / resolution) ตามเวลาทำการ เขตเวลา และวันหยุดของนโยบาย</item>
/// <item>ประเมินสถานะ OnTrack / AtRisk / Breached / Met และสรุป compliance — ใช้ร่วมกับ IssueTracking</item>
/// </list>
/// </summary>
public sealed partial class SlaService
{
    private static readonly string[] AllowedPriorities = ["Low", "Normal", "High", "Urgent", "Immediate"];
    private readonly ISlaDAO _sla;
    private readonly ICustomerDAO _customers;
    private readonly TimeProvider _clock;

    /// <summary>สร้างบริการ</summary>
    /// <param name="sla">DAO ของนโยบาย SLA</param>
    /// <param name="customers">DAO ของลูกค้า</param>
    /// <param name="clock">นาฬิกา (FakeTimeProvider ใน unit test)</param>
    public SlaService(ISlaDAO sla, ICustomerDAO customers, TimeProvider clock)
    {
        _sla = sla;
        _customers = customers;
        _clock = clock;
    }

    [GeneratedRegex("^[A-Z0-9_-]{1,20}$")]
    private static partial Regex SeverityCodeRegex();

    /// <summary>สร้างนโยบาย SLA (ของลูกค้า หรือมาตรฐาน)</summary>
    /// <param name="user">ผู้เรียก (ต้องมีสิทธิ์ sla.manage)</param>
    /// <param name="request">ข้อมูลนโยบายและระดับ severity</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>นโยบายที่บันทึกแล้ว</returns>
    /// <exception cref="ValidationException">ข้อมูลไม่ถูกต้อง</exception>
    /// <exception cref="NotFoundException">ไม่พบลูกค้า</exception>
    public async Task<SlaPolicyDTO> CreatePolicyAsync(UserContextDTO user, SlaPolicyRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ตรวจข้อมูล
        AccessGuard.Require(user, Permissions.SlaManage);
        ArgumentNullException.ThrowIfNull(request);
        SlaPolicyDTO policy = await BuildPolicyAsync(request, ct).ConfigureAwait(false);

        // Step 2: บันทึก (นโยบาย + ระดับใน transaction เดียว)
        policy.Id = await _sla.InsertAsync(policy, ct).ConfigureAwait(false);
        return (await _sla.GetAsync(policy.Id, ct).ConfigureAwait(false))!;
    }

    /// <summary>แก้ไขนโยบายและแทนที่ระดับทั้งหมด (ตรวจ version)</summary>
    /// <param name="user">ผู้เรียก</param>
    /// <param name="policyId">รหัสนโยบาย</param>
    /// <param name="request">ข้อมูลใหม่ (ต้องมี Version)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>นโยบายหลังแก้ไข</returns>
    /// <exception cref="ConflictException">version ไม่ตรง</exception>
    public async Task<SlaPolicyDTO> UpdatePolicyAsync(UserContextDTO user, long policyId, SlaPolicyRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + มีอยู่จริง
        AccessGuard.Require(user, Permissions.SlaManage);
        ArgumentNullException.ThrowIfNull(request);
        SlaPolicyDTO existing = await _sla.GetAsync(policyId, ct).ConfigureAwait(false) ?? throw new NotFoundException("SLA policy not found.");
        ValidationException.ThrowIf(request.Version is null, "version", "Version is required.");
        ValidationException.ThrowIf(request.CustomerId != existing.CustomerId, "customerId", "A policy cannot be moved to another customer.");

        // Step 2: สร้างข้อมูลใหม่แล้วบันทึกแบบ optimistic concurrency
        SlaPolicyDTO policy = await BuildPolicyAsync(request, ct).ConfigureAwait(false);
        policy.Id = policyId;
        policy.Version = request.Version!.Value;
        if (!await _sla.UpdateAsync(policy, ct).ConfigureAwait(false))
        {
            throw new ConflictException("The SLA policy was changed by someone else. Reload and try again.");
        }

        return (await _sla.GetAsync(policyId, ct).ConfigureAwait(false))!;
    }

    /// <summary>รายการนโยบายของลูกค้า (customerId = null → นโยบายมาตรฐาน)</summary>
    /// <param name="user">ผู้เรียก (sla.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>นโยบายพร้อมระดับ</returns>
    public Task<IReadOnlyList<SlaPolicyDTO>> ListPoliciesAsync(UserContextDTO user, long? customerId, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.SlaRead);
        return _sla.ListByCustomerAsync(customerId, ct);
    }

    /// <summary>
    /// หานโยบายที่มีผลกับลูกค้า ณ วันที่: นโยบายเฉพาะลูกค้าก่อน (ล่าสุดที่มีผล) ไม่มีจึงใช้นโยบายมาตรฐาน
    /// </summary>
    /// <param name="customerCode">รหัสลูกค้า</param>
    /// <param name="at">เวลาอ้างอิง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>(ลูกค้า, นโยบาย) — นโยบาย null ถ้าไม่มีทั้งสองแบบ</returns>
    /// <exception cref="NotFoundException">ไม่พบลูกค้า</exception>
    public async Task<(CustomerDTO Customer, SlaPolicyDTO? Policy)> ResolvePolicyAsync(string customerCode, DateTimeOffset at, CancellationToken ct = default)
    {
        // Step 1: ลูกค้า
        CustomerDTO customer = await _customers.GetByCodeAsync(customerCode.Trim(), ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Customer '{customerCode}' not found.");
        var date = DateOnly.FromDateTime(at.UtcDateTime);

        // Step 2: นโยบายเฉพาะลูกค้าที่มีผล (ValidFrom ล่าสุดชนะ)
        SlaPolicyDTO? own = (await _sla.ListByCustomerAsync(customer.Id, ct).ConfigureAwait(false))
            .Where(p => p.IsEffectiveOn(date)).OrderByDescending(p => p.ValidFrom).FirstOrDefault();
        if (own is not null)
        {
            return (customer, own);
        }

        // Step 3: นโยบายมาตรฐาน
        SlaPolicyDTO? fallback = (await _sla.ListByCustomerAsync(null, ct).ConfigureAwait(false))
            .Where(p => p.IsEffectiveOn(date)).OrderByDescending(p => p.ValidFrom).FirstOrDefault();
        return (customer, fallback);
    }

    /// <summary>ระดับ severity ที่ลูกค้าใช้ได้ ณ ตอนนี้ (IssueTracking ใช้แสดง dropdown และตรวจค่าที่ผู้ใช้เลือก)</summary>
    /// <param name="user">ผู้เรียก (sla.read)</param>
    /// <param name="customerCode">รหัสลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>นโยบายที่มีผล (null ถ้าไม่มี SLA)</returns>
    public async Task<SlaPolicyDTO?> GetEffectivePolicyAsync(UserContextDTO user, string customerCode, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.SlaRead);
        return (await ResolvePolicyAsync(customerCode, _clock.GetUtcNow(), ct).ConfigureAwait(false)).Policy;
    }

    /// <summary>คำนวณกำหนดเวลา SLA ของงานใหม่ (IssueTracking เรียกตอนสร้าง/เปลี่ยน severity ของ issue)</summary>
    /// <param name="user">ผู้เรียก (sla.read)</param>
    /// <param name="request">รหัสลูกค้า, severity, เวลาเริ่ม</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กำหนดเวลา</returns>
    /// <exception cref="ValidationException">ลูกค้าไม่มี SLA หรือ severity ไม่อยู่ในนโยบายของลูกค้า</exception>
    public async Task<SlaTargetDTO> CalculateTargetsAsync(UserContextDTO user, SlaCalculateRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + หานโยบาย
        AccessGuard.Require(user, Permissions.SlaRead);
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset start = request.StartedAt ?? _clock.GetUtcNow();
        (CustomerDTO customer, SlaPolicyDTO? policy) = await ResolvePolicyAsync(request.CustomerCode, start, ct).ConfigureAwait(false);
        ValidationException.ThrowIf(policy is null, "customerCode", $"Customer '{customer.Code}' has no effective SLA policy.");

        // Step 2: คำนวณจากนโยบาย
        return CalculateTargets(customer.Code, policy!, request.SeverityCode, start);
    }

    /// <summary>คำนวณกำหนดเวลาจากนโยบาย (Pure function — ทดสอบได้โดยไม่ต้องมี DB)</summary>
    /// <param name="customerCode">รหัสลูกค้า</param>
    /// <param name="policy">นโยบาย</param>
    /// <param name="severityCode">รหัส severity (ไม่สนตัวพิมพ์)</param>
    /// <param name="start">เวลาเริ่ม</param>
    /// <returns>กำหนดเวลา</returns>
    /// <exception cref="ValidationException">severity ไม่อยู่ในนโยบาย</exception>
    public static SlaTargetDTO CalculateTargets(string customerCode, SlaPolicyDTO policy, string severityCode, DateTimeOffset start)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Step 1: หา severity ในนโยบายของลูกค้า (แต่ละลูกค้ามีชุดของตัวเอง)
        SeverityLevelDTO level = policy.Levels.FirstOrDefault(l => string.Equals(l.Code, severityCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ValidationException(
                $"Severity '{severityCode}' is not defined for customer '{customerCode}'. Allowed: {string.Join(", ", policy.Levels.OrderBy(l => l.Rank).Select(l => l.Code))}.",
                new Dictionary<string, string[]> { ["severityCode"] = policy.Levels.OrderBy(l => l.Rank).Select(l => l.Code).ToArray() });

        // Step 2: บวกเวลาตามปฏิทิน (เวลาทำการ หรือ 24x7)
        var calendar = new BusinessCalendar(policy);
        DateTimeOffset responseDue = calendar.AddMinutes(start, level.ResponseMinutes, level.BusinessHoursOnly);
        DateTimeOffset resolutionDue = calendar.AddMinutes(start, level.ResolutionMinutes, level.BusinessHoursOnly);

        // Step 3: ประกอบผล
        return new SlaTargetDTO(customerCode, policy.Id, policy.Name, level.Code, level.Name, level.Rank, level.IssuePriority,
            start.ToUniversalTime(), responseDue, resolutionDue, level.BusinessHoursOnly);
    }

    /// <summary>ประเมินสถานะ SLA ของงาน ณ ตอนนี้</summary>
    /// <param name="user">ผู้เรียก (sla.read)</param>
    /// <param name="request">ข้อมูลงาน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลประเมิน</returns>
    public async Task<SlaEvaluationDTO> EvaluateAsync(UserContextDTO user, SlaEvaluateRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + นโยบาย ณ เวลาเปิดงาน (นโยบายที่เปลี่ยนภายหลังไม่ย้อนมีผล)
        AccessGuard.Require(user, Permissions.SlaRead);
        ArgumentNullException.ThrowIfNull(request);
        (CustomerDTO customer, SlaPolicyDTO? policy) = await ResolvePolicyAsync(request.CustomerCode, request.StartedAt, ct).ConfigureAwait(false);
        ValidationException.ThrowIf(policy is null, "customerCode", $"Customer '{customer.Code}' has no effective SLA policy.");

        // Step 2: คำนวณกำหนดเวลาแล้วประเมิน
        SlaTargetDTO target = CalculateTargets(customer.Code, policy!, request.SeverityCode, request.StartedAt);
        return Evaluate(target, policy!, request.FirstRespondedAt, request.ResolvedAt, _clock.GetUtcNow());
    }

    /// <summary>ประเมินสถานะจากกำหนดเวลา (Pure function)</summary>
    /// <param name="target">กำหนดเวลา</param>
    /// <param name="policy">นโยบาย (ใช้นับเวลาทำการและเกณฑ์เตือน)</param>
    /// <param name="firstRespondedAt">ตอบครั้งแรกเมื่อ</param>
    /// <param name="resolvedAt">แก้เสร็จเมื่อ</param>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <returns>ผลประเมิน</returns>
    public static SlaEvaluationDTO Evaluate(SlaTargetDTO target, SlaPolicyDTO policy, DateTimeOffset? firstRespondedAt, DateTimeOffset? resolvedAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policy);
        var calendar = new BusinessCalendar(policy);
        SeverityLevelDTO? level = policy.Levels.FirstOrDefault(l => l.Code == target.SeverityCode);

        // Step 1: สัดส่วนเวลาที่ใช้ไป (นับเวลาทำการถ้านโยบายกำหนด)
        double Ratio(DateTimeOffset due, int budgetMinutes, DateTimeOffset until) => budgetMinutes <= 0 ? 1
            : calendar.MinutesBetween(target.StartedAt, until, target.BusinessHoursOnly) / budgetMinutes;

        int responseBudget = level?.ResponseMinutes ?? (int)Math.Max(1, (target.ResponseDueAt - target.StartedAt).TotalMinutes);
        int resolutionBudget = level?.ResolutionMinutes ?? (int)Math.Max(1, (target.ResolutionDueAt - target.StartedAt).TotalMinutes);

        // Step 2: สถานะ response และ resolution
        SlaState responseState = StateOf(target.ResponseDueAt, firstRespondedAt, now, Ratio(target.ResponseDueAt, responseBudget, firstRespondedAt ?? now), policy.AtRiskThreshold);
        double resolutionRatio = Ratio(target.ResolutionDueAt, resolutionBudget, resolvedAt ?? now);
        SlaState resolutionState = StateOf(target.ResolutionDueAt, resolvedAt, now, resolutionRatio, policy.AtRiskThreshold);

        // Step 3: เวลาที่เหลือ (ติดลบ = เลยกำหนด)
        double minutesLeft = ((resolvedAt ?? now) <= target.ResolutionDueAt ? 1 : -1)
            * calendar.MinutesBetween(Min(resolvedAt ?? now, target.ResolutionDueAt), Max(resolvedAt ?? now, target.ResolutionDueAt), target.BusinessHoursOnly);
        return new SlaEvaluationDTO(target, responseState, resolutionState, Math.Round(resolutionRatio, 4), Math.Round(minutesLeft, 1));
    }

    /// <summary>
    /// สรุปการปฏิบัติตาม SLA จากรายการ issue ของ IssueTracking (ใช้กำหนดเวลาที่ IssueTracking บันทึกไว้)
    /// </summary>
    /// <param name="customerCode">รหัสลูกค้า</param>
    /// <param name="issues">issue ของลูกค้า (ทั้งเปิดและปิด)</param>
    /// <param name="policy">นโยบาย (null = ใช้เวลานาฬิกาในการคำนวณเกณฑ์เตือน)</param>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <returns>สรุป compliance</returns>
    public static SlaComplianceDTO SummarizeCompliance(string customerCode, IReadOnlyList<ExternalIssueDTO> issues, SlaPolicyDTO? policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issues);
        double threshold = policy?.AtRiskThreshold ?? 0.8;
        int met = 0, breached = 0, atRisk = 0, onTrack = 0, total = 0;
        var bySeverity = new Dictionary<string, (int Total, int Breached)>(StringComparer.OrdinalIgnoreCase);

        foreach (ExternalIssueDTO issue in issues)
        {
            // Step 1: ข้าม issue ที่ไม่มี SLA
            if (issue.ResolutionDueAt is not { } due)
            {
                continue;
            }

            // Step 2: สถานะ resolution จากเวลาปิด (ปิดแล้ว) หรือเวลาปัจจุบัน (ยังเปิด)
            double budget = Math.Max(1, (due - issue.CreatedAt).TotalMinutes);
            double ratio = ((issue.IsClosed ? issue.ClosedAt ?? now : now) - issue.CreatedAt).TotalMinutes / budget;
            SlaState state = StateOf(due, issue.IsClosed ? issue.ClosedAt ?? now : null, now, ratio, threshold);
            total++;
            switch (state)
            {
                case SlaState.Met: met++; break;
                case SlaState.Breached: breached++; break;
                case SlaState.AtRisk: atRisk++; break;
                default: onTrack++; break;
            }

            // Step 3: นับแยก severity
            string key = issue.SeverityCode ?? "(none)";
            (int t, int b) = bySeverity.GetValueOrDefault(key);
            bySeverity[key] = (t + 1, b + (state == SlaState.Breached ? 1 : 0));
        }

        double compliance = total == 0 ? 100 : Math.Round(100.0 * (total - breached) / total, 2);
        return new SlaComplianceDTO(customerCode, total, met, breached, atRisk, onTrack, compliance,
            bySeverity.ToDictionary(kv => kv.Key, kv => new SlaBucketDTO(kv.Value.Total, kv.Value.Breached), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Priority มาตรฐานของ IssueTracking ตามอันดับ severity</summary>
    /// <param name="rank">อันดับ 1..5</param>
    /// <returns>Immediate / Urgent / High / Normal / Low</returns>
    public static string DefaultPriorityForRank(int rank) => rank switch
    {
        1 => "Immediate",
        2 => "Urgent",
        3 => "High",
        4 => "Normal",
        _ => "Low",
    };

    /// <summary>สถานะของเส้นตาย 1 เส้น</summary>
    /// <param name="due">กำหนด</param>
    /// <param name="doneAt">ทำเสร็จเมื่อ (null = ยังไม่เสร็จ)</param>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <param name="ratio">สัดส่วนเวลาที่ใช้ไป</param>
    /// <param name="atRiskThreshold">เกณฑ์เตือน</param>
    /// <returns>สถานะ</returns>
    private static SlaState StateOf(DateTimeOffset due, DateTimeOffset? doneAt, DateTimeOffset now, double ratio, double atRiskThreshold)
    {
        if (doneAt is { } done)
        {
            return done <= due ? SlaState.Met : SlaState.Breached;
        }

        if (now > due)
        {
            return SlaState.Breached;
        }

        return ratio >= atRiskThreshold ? SlaState.AtRisk : SlaState.OnTrack;
    }

    /// <summary>ตรวจคำขอแล้วสร้าง DTO ของนโยบาย</summary>
    /// <param name="request">คำขอ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>นโยบาย (ยังไม่บันทึก)</returns>
    private async Task<SlaPolicyDTO> BuildPolicyAsync(SlaPolicyRequest request, CancellationToken ct)
    {
        // Step 1: ลูกค้าต้องมีอยู่จริง (ถ้าระบุ)
        if (request.CustomerId is { } customerId)
        {
            _ = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
        }

        // Step 2: ข้อมูลหลัก
        string name = Common.Core.Security.InputGuard.Required(request.Name, "name", 150);
        ValidationException.ThrowIf(request.ValidTo is { } to && to < request.ValidFrom, "validTo", "ValidTo must be on or after ValidFrom.");
        ValidationException.ThrowIf(request.Levels.Count == 0, "levels", "At least one severity level is required.");
        ValidationException.ThrowIf(request.AtRiskThreshold is < 0.5 or > 0.99, "atRiskThreshold", "AtRiskThreshold must be between 0.5 and 0.99.");

        // Step 3: ระดับ severity — รหัสไม่ซ้ำ, resolution ≥ response, priority อยู่ใน allow-list
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var levels = new List<SeverityLevelDTO>(request.Levels.Count);
        foreach (SeverityLevelRequest l in request.Levels)
        {
            string code = (l.Code ?? string.Empty).Trim().ToUpperInvariant();
            ValidationException.ThrowIf(!SeverityCodeRegex().IsMatch(code), "levels.code", "Severity code must be 1-20 characters A-Z, 0-9, '_' or '-'.");
            ValidationException.ThrowIf(!codes.Add(code), "levels.code", $"Duplicate severity code '{code}'.");
            ValidationException.ThrowIf(l.Rank is < 1 or > 5, "levels.rank", "Rank must be 1-5.");
            ValidationException.ThrowIf(l.ResolutionMinutes < l.ResponseMinutes, "levels.resolutionMinutes", $"Resolution time of {code} must not be shorter than its response time.");
            string priority = string.IsNullOrWhiteSpace(l.IssuePriority) ? DefaultPriorityForRank(l.Rank)
                : AllowedPriorities.FirstOrDefault(p => p.Equals(l.IssuePriority.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw new ValidationException($"IssuePriority must be one of {string.Join(", ", AllowedPriorities)}.");
            levels.Add(new SeverityLevelDTO
            {
                Code = code,
                Name = Common.Core.Security.InputGuard.Required(l.Name, "levels.name", 100),
                Description = Common.Core.Security.InputGuard.StripControlCharacters(l.Description),
                Rank = l.Rank,
                ResponseMinutes = l.ResponseMinutes,
                ResolutionMinutes = l.ResolutionMinutes,
                BusinessHoursOnly = l.BusinessHoursOnly,
                IssuePriority = priority,
            });
        }

        // Step 4: ประกอบนโยบาย แล้วตรวจปฏิทิน (เขตเวลา/เวลาทำการ) ด้วยการสร้าง BusinessCalendar
        var policy = new SlaPolicyDTO
        {
            CustomerId = request.CustomerId,
            Name = name,
            Description = Common.Core.Security.InputGuard.StripControlCharacters(request.Description),
            ValidFrom = request.ValidFrom == default ? DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime) : request.ValidFrom,
            ValidTo = request.ValidTo,
            TimeZoneId = request.TimeZoneId,
            BusinessStart = request.BusinessStart,
            BusinessEnd = request.BusinessEnd,
            WorkingDaysMask = request.WorkingDaysMask,
            Holidays = BusinessCalendar.FormatHolidays(request.Holidays),
            AtRiskThreshold = request.AtRiskThreshold,
            IsActive = true,
            Levels = levels.OrderBy(l => l.Rank).ThenBy(l => l.Code, StringComparer.Ordinal).ToList(),
        };
        _ = new BusinessCalendar(policy);
        return policy;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
