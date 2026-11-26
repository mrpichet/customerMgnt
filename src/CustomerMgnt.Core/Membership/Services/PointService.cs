using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>คะแนน (PTS-001..005) สำหรับ Back Office: ยอด ledger lot กฎการได้คะแนน การปรับคะแนน</summary>
public sealed class PointService
{
    private readonly IPointDAO _points;
    private readonly IMemberDAO _members;
    private readonly IMembershipAuditDAO _audit;
    private readonly IMembershipUnitOfWork _uow;
    private readonly PointLedgerService _ledger;
    private readonly ApprovalService _approvals;
    private readonly MembershipRulesProvider _rules;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public PointService(IPointDAO points, IMemberDAO members, IMembershipAuditDAO audit, IMembershipUnitOfWork uow, PointLedgerService ledger,
        ApprovalService approvals, MembershipRulesProvider rules, TimeProvider clock)
    {
        _points = points;
        _members = members;
        _audit = audit;
        _uow = uow;
        _ledger = ledger;
        _approvals = approvals;
        _rules = rules;
        _clock = clock;
    }

    /// <summary>ยอดคะแนน</summary>
    public async Task<PointBalanceDTO> GetBalanceAsync(MembershipCaller caller, long memberId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PointsRead);
        await RequireMemberAsync(memberId, ct).ConfigureAwait(false);
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        return await _ledger.GetBalanceAsync(memberId, _clock.GetUtcNow(), rules.ExpiryNotifyDays, ct).ConfigureAwait(false);
    }

    /// <summary>ค้นหา ledger</summary>
    public Task<PagedResultDTO<PointLedgerDTO>> SearchLedgerAsync(MembershipCaller caller, LedgerCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PointsRead);
        return _points.SearchLedgerAsync(criteria, ct);
    }

    /// <summary>lot ของสมาชิก</summary>
    public async Task<IReadOnlyList<PointLotDTO>> ListLotsAsync(MembershipCaller caller, long memberId, bool activeOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PointsRead);
        await RequireMemberAsync(memberId, ct).ConfigureAwait(false);
        return await _points.ListLotsAsync(memberId, activeOnly, _clock.GetUtcNow(), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ กฎการได้คะแนน

    /// <summary>กฎทั้งหมด</summary>
    public async Task<IReadOnlyList<EarnRuleDTO>> ListRulesAsync(MembershipCaller caller, bool activeOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PointsRead);
        IReadOnlyList<EarnRuleDTO> all = await _points.ListRulesAsync(ct).ConfigureAwait(false);
        return activeOnly ? [.. all.Where(r => r.Status == EarnRuleStatus.Active)] : all;
    }

    /// <summary>สร้างกฎ (Submit = ส่งอนุมัติทันที)</summary>
    public async Task<EarnRuleDTO> CreateRuleAsync(MembershipCaller caller, EarnRuleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PointsRuleManage);
        IReadOnlyList<EarnRuleDTO> all = await _points.ListRulesAsync(ct).ConfigureAwait(false);
        if (all.Any(r => string.Equals(r.Code, request.Code, StringComparison.OrdinalIgnoreCase)))
        {
            throw MembershipException.Conflict("conflict", "Rule code already exists.");
        }

        DateTimeOffset now = _clock.GetUtcNow();
        EarnRuleDTO rule = Build(request);
        rule.Status = EarnRuleStatus.Draft;
        rule.CreatedBy = caller.User.Subject;
        rule.CreatedAt = now;
        rule.Version = 1;
        rule.Id = await _points.InsertRuleAsync(rule, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "earn_rule.create", $"earn_rule:{rule.Id}", null, rule, now)], ct).ConfigureAwait(false);
        return request.Submit ? await SubmitRuleAsync(caller, rule, null, ct).ConfigureAwait(false) : rule;
    }

    /// <summary>แก้กฎ — กฎที่ Active แล้วแก้ = กลับไปรออนุมัติ</summary>
    public async Task<EarnRuleDTO> UpdateRuleAsync(MembershipCaller caller, long id, EarnRuleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PointsRuleManage);
        MembershipEvents.RequireVersion(request.Version);
        EarnRuleDTO before = await _points.GetRuleAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Rule not found.");
        if (before.Version != request.Version)
        {
            throw MembershipException.VersionConflict();
        }

        if (before.Status == EarnRuleStatus.PendingApproval)
        {
            throw MembershipException.Conflict("invalid_transition", "The rule is waiting for approval.");
        }

        EarnRuleDTO rule = Build(request);
        rule.Id = id;
        rule.Code = before.Code;
        rule.CreatedBy = before.CreatedBy;
        rule.CreatedAt = before.CreatedAt;
        rule.Version = before.Version;
        rule.Status = before.Status == EarnRuleStatus.Active ? EarnRuleStatus.Draft : before.Status;
        if (before.Status == EarnRuleStatus.Active)
        {
            // กฎเดิมยังใช้ต่อจนกว่าจะอนุมัติ: เก็บค่าใหม่ไว้ในคำขออนุมัติ
            ApprovalRequestDTO ar = await _approvals.SubmitAsync(caller, ApprovalType.EarnRule, id, $"แก้กฎคะแนน {before.Code}", 1, before, rule, rule, null, ct).ConfigureAwait(false);
            before.ApprovalRequestId = ar.Id;
            if (!await _points.UpdateRuleAsync(before, ct).ConfigureAwait(false))
            {
                throw MembershipException.VersionConflict();
            }

            before.Version++;
            return before;
        }

        if (!await _points.UpdateRuleAsync(rule, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        rule.Version++;
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "earn_rule.update", $"earn_rule:{id}", before, rule, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        return request.Submit ? await SubmitRuleAsync(caller, rule, before, ct).ConfigureAwait(false) : rule;
    }

    private async Task<EarnRuleDTO> SubmitRuleAsync(MembershipCaller caller, EarnRuleDTO rule, EarnRuleDTO? before, CancellationToken ct)
    {
        ApprovalRequestDTO ar = await _approvals.SubmitAsync(caller, ApprovalType.EarnRule, rule.Id, $"กฎคะแนน {rule.Code}", 1, before, rule, null, null, ct).ConfigureAwait(false);
        rule.Status = EarnRuleStatus.PendingApproval;
        rule.ApprovalRequestId = ar.Id;
        if (!await _points.UpdateRuleAsync(rule, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        rule.Version++;
        return rule;
    }

    /// <summary>ผลอนุมัติกฎ</summary>
    internal async Task ApplyRuleDecisionAsync(MembershipCaller actor, ApprovalRequestDTO request, bool approved, CancellationToken ct)
    {
        EarnRuleDTO rule = await _points.GetRuleAsync(request.EntityId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Rule not found.");
        EarnRuleDTO? proposed = request.PayloadJson is null ? null : MembershipJson.Read<EarnRuleDTO>(request.PayloadJson);
        if (approved && proposed is not null)
        {
            // แก้กฎที่ Active: ใช้ค่าที่เสนอ
            proposed.Id = rule.Id;
            proposed.Version = rule.Version;
            proposed.Status = EarnRuleStatus.Active;
            proposed.ApprovalRequestId = request.Id;
            rule = proposed;
        }
        else
        {
            rule.Status = approved ? EarnRuleStatus.Active : proposed is not null ? rule.Status : EarnRuleStatus.Draft;
        }

        if (!await _points.UpdateRuleAsync(rule, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        await _audit.AppendAsync([MembershipEvents.Audit(actor, approved ? "earn_rule.activate" : "earn_rule.reject", $"earn_rule:{rule.Id}", null, new { rule.Status }, _clock.GetUtcNow())], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// กฎที่ใช้กับบิล: กฎ Active ที่ตรงสาขา/ช่องทาง/เวลา — หลายกฎเลือกที่เจาะจงที่สุด (ระบุสาขา/ช่องทาง) แล้วเริ่มล่าสุด
    /// </summary>
    public async Task<EarnRuleDTO?> ResolveRuleAsync(string? branch, SalesChannel channel, DateTimeOffset at, CancellationToken ct) =>
        (await _points.ListRulesAsync(ct).ConfigureAwait(false))
            .Where(r => r.AppliesTo(branch, channel, at))
            .OrderByDescending(r => (r.Branches.Count > 0 ? 2 : 0) + (r.Channels.Count > 0 ? 1 : 0))
            .ThenByDescending(r => r.StartAt).ThenByDescending(r => r.Id)
            .FirstOrDefault();

    // ------------------------------------------------------------------ ปรับคะแนน

    /// <summary>ขอปรับคะแนน (spec 2.9): ในวงเงิน = มีผลทันที, เกิน = รออนุมัติ</summary>
    public async Task<AdjustmentDTO> RequestAdjustmentAsync(MembershipCaller caller, AdjustmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PointsAdjust);
        ValidationException.ThrowIf(request.Points == 0, "points", "Points must not be zero.");
        MemberEntity m = await RequireMemberAsync(request.MemberId, ct).ConfigureAwait(false);
        if (m.Status == MemberStatus.Closed)
        {
            throw MembershipException.Conflict("invalid_transition", "A closed member cannot be adjusted.");
        }

        // Step 1: วงเงินต่อครั้งและต่อวัน
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        TimeZoneInfo zone = PointMath.Zone(rules.TimeZoneId);
        DateOnly today = PointMath.LocalDate(now, zone);
        var dayStart = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(today.ToDateTime(TimeOnly.MinValue)));
        int usedToday = await _points.SumAdjustmentsAsync(caller.User.Subject, dayStart, ct).ConfigureAwait(false);
        int abs = Math.Abs(request.Points);
        bool withinLimit = abs <= rules.AdjustMaxPerRequest && usedToday + abs <= rules.AdjustMaxPerUserPerDay;

        // Step 2: บันทึกคำขอ
        var adj = new AdjustmentDTO
        {
            MemberId = m.Id, Points = request.Points, Reason = request.Reason.Trim(), AttachmentRef = request.AttachmentRef,
            Status = withinLimit ? AdjustmentStatus.Applied : AdjustmentStatus.PendingApproval, RequestedBy = caller.User.Subject, CreatedAt = now,
        };
        if (withinLimit)
        {
            // ตรวจคะแนนพอก่อนเพิ่มคำขอ (ลดคะแนน)
            adj.Status = AdjustmentStatus.PendingApproval;
            adj.Id = await _points.InsertAdjustmentAsync(adj, ct).ConfigureAwait(false);
            try
            {
                await ApplyAdjustmentAsync(caller, adj, ct).ConfigureAwait(false);
            }
            catch
            {
                adj.Status = AdjustmentStatus.Rejected;
                adj.DecidedAt = now;
                await _points.UpdateAdjustmentAsync(adj, ct).ConfigureAwait(false);
                throw;
            }

            return adj;
        }

        adj.Id = await _points.InsertAdjustmentAsync(adj, ct).ConfigureAwait(false);
        ApprovalRequestDTO ar = await _approvals.SubmitAsync(caller, ApprovalType.Adjustment, adj.Id,
            $"ปรับคะแนน {m.MemberNo} {request.Points:+#;-#;0}", 1, null, new { m.MemberNo, request.Points, request.Reason }, null, request.Reason, ct).ConfigureAwait(false);
        adj.ApprovalRequestId = ar.Id;
        await _points.UpdateAdjustmentAsync(adj, ct).ConfigureAwait(false);
        return adj;
    }

    /// <summary>รายการคำขอปรับคะแนน</summary>
    public Task<IReadOnlyList<AdjustmentDTO>> ListAdjustmentsAsync(MembershipCaller caller, AdjustmentStatus? status, long? memberId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PointsRead);
        return _points.ListAdjustmentsAsync(status, memberId, ct);
    }

    /// <summary>ผลอนุมัติการปรับคะแนน</summary>
    internal async Task ApplyAdjustmentDecisionAsync(MembershipCaller actor, ApprovalRequestDTO request, bool approved, CancellationToken ct)
    {
        AdjustmentDTO adj = await _points.GetAdjustmentAsync(request.EntityId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Adjustment not found.");
        if (adj.Status != AdjustmentStatus.PendingApproval)
        {
            return;
        }

        if (!approved)
        {
            adj.Status = AdjustmentStatus.Rejected;
            adj.DecidedAt = _clock.GetUtcNow();
            await _points.UpdateAdjustmentAsync(adj, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await ApplyAdjustmentAsync(actor, adj, ct).ConfigureAwait(false);
        }
        catch (MembershipException)
        {
            adj.Status = AdjustmentStatus.Rejected;
            adj.DecidedAt = _clock.GetUtcNow();
            await _points.UpdateAdjustmentAsync(adj, ct).ConfigureAwait(false);
            throw;
        }
    }

    private Task ApplyAdjustmentAsync(MembershipCaller actor, AdjustmentDTO adj, CancellationToken ct) =>
        MembershipEvents.RetryOnConflictAsync(async () =>
        {
            MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
            DateTimeOffset now = _clock.GetUtcNow();
            var cs = new MembershipChangeSet();
            PointPosting p = await _ledger.OpenAsync(adj.MemberId, now, ct).ConfigureAwait(false);
            var ctx = new LedgerContext(LedgerType.Adjustment, $"adjustment:{adj.Id}", actor.Actor) { Reference = $"adjustment:{adj.Id}", Reason = adj.Reason, RequestId = actor.RequestId };
            if (adj.Points > 0)
            {
                PointLedgerService.Credit(p, cs, adj.Points, ctx, now, PointMath.ExpiryFor(now, rules.ExpiryMonths, PointMath.Zone(rules.TimeZoneId)), countLifetime: false);
            }
            else
            {
                PointLedgerService.Debit(p, cs, -adj.Points, ctx, now, allowDebt: false);
            }

            PointLedgerService.Finish(p, cs);
            adj.Status = AdjustmentStatus.Applied;
            adj.DecidedAt = now;
            cs.AdjustmentUpdate = adj;
            cs.Audit.Add(MembershipEvents.Audit(actor, "points.adjust", $"member:{adj.MemberId}", null, new { adjustmentId = adj.Id, adj.Points, adj.Reason }, now));
            await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
            return true;
        });

    private async Task<MemberEntity> RequireMemberAsync(long memberId, CancellationToken ct) =>
        await _members.GetAsync(memberId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Member not found.");

    private static EarnRuleDTO Build(EarnRuleRequest r)
    {
        ValidationException.ThrowIf(r.EndAt is DateTimeOffset end && end <= r.StartAt, "endAt", "End must be after start.");
        return new EarnRuleDTO
        {
            Code = r.Code, Name = r.Name.Trim(), SpendPerPoint = r.SpendPerPoint, MinSpend = r.MinSpend, MaxPointsPerTransaction = r.MaxPointsPerTransaction,
            IncludeCategories = [.. r.IncludeCategories], ExcludeItemCodes = [.. r.ExcludeItemCodes], Branches = [.. r.Branches], Channels = [.. r.Channels],
            StartAt = r.StartAt, EndAt = r.EndAt,
        };
    }
}

/// <summary>นำผลอนุมัติการปรับคะแนนไปใช้</summary>
public sealed class AdjustmentApprovalHandler(PointService points) : IApprovalHandler
{
    /// <inheritdoc />
    public ApprovalType Type => ApprovalType.Adjustment;

    /// <inheritdoc />
    public Task OnApprovedAsync(MembershipCaller approver, ApprovalRequestDTO request, CancellationToken ct) => points.ApplyAdjustmentDecisionAsync(approver, request, true, ct);

    /// <inheritdoc />
    public Task OnRejectedAsync(MembershipCaller actor, ApprovalRequestDTO request, CancellationToken ct) => points.ApplyAdjustmentDecisionAsync(actor, request, false, ct);
}

/// <summary>นำผลอนุมัติกฎคะแนนไปใช้</summary>
public sealed class EarnRuleApprovalHandler(PointService points) : IApprovalHandler
{
    /// <inheritdoc />
    public ApprovalType Type => ApprovalType.EarnRule;

    /// <inheritdoc />
    public Task OnApprovedAsync(MembershipCaller approver, ApprovalRequestDTO request, CancellationToken ct) => points.ApplyRuleDecisionAsync(approver, request, true, ct);

    /// <inheritdoc />
    public Task OnRejectedAsync(MembershipCaller actor, ApprovalRequestDTO request, CancellationToken ct) => points.ApplyRuleDecisionAsync(actor, request, false, ct);
}
