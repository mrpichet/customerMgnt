using System.Globalization;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Integration;
using CustomerMgnt.Core.Security;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// สรุปข้อมูลจากระบบอื่นในมุมลูกค้า:
/// <list type="bullet">
/// <item>Issue ค้างที่ยังไม่แก้ แยก Aging × Severity พร้อมสถานะ SLA (จาก IssueTracking)</item>
/// <item>Project ที่ยัง Active และ Closed พร้อม Status/Progress (จาก ProjectMgnt)</item>
/// <item>SLA compliance ของลูกค้า</item>
/// </list>
/// </summary>
public sealed class InsightService
{
    private readonly ICustomerDAO _customers;
    private readonly IExternalLinkDAO _links;
    private readonly IIssueTrackingGateway _issues;
    private readonly IProjectMgntGateway _projects;
    private readonly SlaService _sla;
    private readonly CustomerMgntOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>สร้างบริการ</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="links">DAO ลิงก์</param>
    /// <param name="issues">IssueTracking</param>
    /// <param name="projects">ProjectMgnt</param>
    /// <param name="sla">บริการ SLA</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="clock">นาฬิกา</param>
    public InsightService(ICustomerDAO customers, IExternalLinkDAO links, IIssueTrackingGateway issues, IProjectMgntGateway projects, SlaService sla, IOptions<CustomerMgntOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _customers = customers;
        _links = links;
        _issues = issues;
        _projects = projects;
        _sla = sla;
        _options = options.Value;
        _clock = clock;
    }

    /// <summary>สรุป issue ค้างของลูกค้า แยกช่วงอายุ × severity พร้อมจำนวนที่ผิด/ใกล้ผิด SLA</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="customerId">ลูกค้า (null = ทุก issue ที่ผู้เรียกเห็นใน IssueTracking)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป aging</returns>
    public async Task<IssueAgingSummaryDTO> GetIssueAgingAsync(UserContextDTO user, long? customerId, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้าและ project ที่ผูกไว้
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        CustomerDTO? customer = null;
        long[] projectIds = [];
        if (customerId is { } id)
        {
            customer = await _customers.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
            projectIds = (await _links.ListAsync(id, ExternalSystem.IssueTracking, ct).ConfigureAwait(false))
                .Where(l => l.EntityType == "project").Select(l => long.Parse(l.ExternalId, CultureInfo.InvariantCulture)).ToArray();
        }

        // Step 2: issue ที่ยังไม่ปิด (กรองด้วยรหัสลูกค้าที่ IssueTracking เก็บ + project ที่ผูกไว้)
        IReadOnlyList<ExternalIssueDTO> issues = await _issues.SearchIssuesAsync(customer?.Code, projectIds, openOnly: true, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();

        // Step 3: คำนวณสรุปแบบ pure
        SlaPolicyDTO? policy = customer is null ? null : (await _sla.ResolvePolicyAsync(customer.Code, now, ct).ConfigureAwait(false)).Policy;
        return BuildAging(customer?.Code, issues, _options.AgingBucketDays, policy, now);
    }

    /// <summary>สร้างตาราง aging × severity (Pure function)</summary>
    /// <param name="customerCode">รหัสลูกค้า</param>
    /// <param name="issues">issue ที่ยังเปิด</param>
    /// <param name="bucketDays">ขอบช่วงอายุ เช่น [7, 30, 90]</param>
    /// <param name="policy">นโยบาย SLA (ใช้เกณฑ์เตือน)</param>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <returns>สรุป</returns>
    public static IssueAgingSummaryDTO BuildAging(string? customerCode, IReadOnlyList<ExternalIssueDTO> issues, IReadOnlyList<int> bucketDays, SlaPolicyDTO? policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(bucketDays);

        // Step 1: สร้างช่วง [0,b1), [b1,b2), ..., [bn, ∞)
        int[] edges = bucketDays.Where(d => d > 0).Distinct().Order().ToArray();
        var ranges = new List<(string Label, int Min, int? Max)>();
        int prev = 0;
        foreach (int e in edges)
        {
            ranges.Add(($"{prev}-{e} วัน", prev, e));
            prev = e;
        }

        ranges.Add(($"{prev}+ วัน", prev, null));

        // Step 2: นับ issue เปิดลงช่วงอายุ × severity (severity ใช้รหัสของลูกค้า; ไม่มี → ใช้ priority)
        var open = issues.Where(i => !i.IsClosed).ToList();
        var counts = ranges.Select(_ => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (ExternalIssueDTO i in open)
        {
            double age = (now - i.CreatedAt).TotalDays;
            int idx = ranges.FindIndex(r => age >= r.Min && (r.Max is null || age < r.Max));
            string sev = i.SeverityCode ?? (i.Priority is null ? "(none)" : "P:" + i.Priority);
            counts[idx][sev] = counts[idx].GetValueOrDefault(sev) + 1;
        }

        // Step 3: SLA ของ issue ที่เปิดอยู่
        SlaComplianceDTO sla = SlaService.SummarizeCompliance(customerCode ?? "*", open, policy, now);
        var buckets = ranges.Select((r, i) => new AgingBucketDTO(r.Label, r.Min, r.Max, counts[i], counts[i].Values.Sum())).ToList();
        double avgAge = open.Count == 0 ? 0 : Math.Round(open.Average(i => (now - i.CreatedAt).TotalDays), 1);

        // Step 4: 10 รายการที่ค้างนานที่สุด (รุนแรงกว่าขึ้นก่อนเมื่ออายุเท่ากัน)
        var oldest = open.OrderBy(i => i.CreatedAt).ThenBy(i => i.SeverityRank ?? 9).Take(10).ToList();
        return new IssueAgingSummaryDTO(customerCode, open.Count, buckets, sla.Breached, sla.AtRisk, avgAge, oldest);
    }

    /// <summary>สรุปการปฏิบัติตาม SLA ของลูกค้า (issue ทั้งเปิดและปิด)</summary>
    /// <param name="user">ผู้เรียก (sla.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป compliance</returns>
    public async Task<SlaComplianceDTO> GetSlaComplianceAsync(UserContextDTO user, long customerId, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้า
        AccessGuard.Require(user, Permissions.SlaRead);
        CustomerDTO customer = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");

        // Step 2: issue ทั้งหมดของลูกค้า + นโยบาย
        IReadOnlyList<ExternalIssueDTO> issues = await _issues.SearchIssuesAsync(customer.Code, [], openOnly: false, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        SlaPolicyDTO? policy = (await _sla.ResolvePolicyAsync(customer.Code, now, ct).ConfigureAwait(false)).Policy;
        return SlaService.SummarizeCompliance(customer.Code, issues, policy, now);
    }

    /// <summary>สรุปโครงการของลูกค้า: Active / Closed พร้อม progress และ health</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุปโครงการ</returns>
    public async Task<ProjectPortfolioDTO> GetProjectPortfolioAsync(UserContextDTO user, long customerId, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + project ที่ผูกไว้
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        CustomerDTO customer = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
        long[] ids = (await _links.ListAsync(customerId, ExternalSystem.ProjectMgnt, ct).ConfigureAwait(false))
            .Where(l => l.EntityType == "project").Select(l => long.Parse(l.ExternalId, CultureInfo.InvariantCulture)).Distinct().ToArray();

        // Step 2: ดึง project + KPI จาก ProjectMgnt
        IReadOnlyList<ExternalProjectDTO> projects = ids.Length == 0 ? [] : await _projects.GetProjectsAsync(ids, ct).ConfigureAwait(false);

        // Step 3: แยก Active / Closed
        var active = projects.Where(p => !p.IsClosed).OrderBy(p => p.ForecastEndDate ?? DateOnly.MaxValue).ToList();
        var closed = projects.Where(p => p.IsClosed).ToList();
        double avg = active.Count == 0 ? 0 : Math.Round(active.Average(p => p.ActualProgress ?? 0), 1);
        int atRisk = active.Count(p => p.Health is "Red" or "Amber");
        return new ProjectPortfolioDTO(customer.Code, active, closed, avg, atRisk);
    }

    /// <summary>ผูกลูกค้ากับ project/entity ในระบบอื่นด้วยมือ (เช่น project ที่มีอยู่แล้วก่อนใช้ CustomerMgnt)</summary>
    /// <param name="user">ผู้เรียก (customer.write)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="system">ระบบ</param>
    /// <param name="entityType">ชนิด (project/issue/meeting/artifact)</param>
    /// <param name="externalId">รหัสในระบบนั้น</param>
    /// <param name="title">ชื่อ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลิงก์</returns>
    public async Task<ExternalLinkDTO> LinkAsync(UserContextDTO user, long customerId, ExternalSystem system, string entityType, string externalId, string? title, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ข้อมูล (allow-list ของชนิด, รหัสเป็นตัวเลข/GUID เท่านั้น)
        AccessGuard.Require(user, Permissions.CustomerWrite);
        _ = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
        ValidationException.ThrowIf(entityType is not ("project" or "issue" or "meeting" or "artifact"), "entityType", "EntityType must be project, issue, meeting or artifact.");
        ValidationException.ThrowIf(!long.TryParse(externalId, NumberStyles.None, CultureInfo.InvariantCulture, out _) && !Guid.TryParse(externalId, out _), "externalId", "ExternalId must be a number or GUID.");

        // Step 2: บันทึก (ซ้ำ = คืนของเดิม)
        var link = new ExternalLinkDTO { CustomerId = customerId, System = system, EntityType = entityType, ExternalId = externalId, Title = Common.Core.Security.InputGuard.StripControlCharacters(title), CreatedAt = _clock.GetUtcNow() };
        link.Id = await _links.UpsertAsync(link, ct).ConfigureAwait(false);
        return link;
    }
}
