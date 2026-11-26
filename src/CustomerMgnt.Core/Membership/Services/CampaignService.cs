using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>แคมเปญ / โปรโมชัน (PRO-001..006): สร้าง แก้ ส่งอนุมัติ หยุด ยกเลิก และเปลี่ยนสถานะตามเวลา</summary>
public sealed class CampaignService : IApprovalHandler
{
    private static readonly CampaignStatus[] ScheduleStatuses = [CampaignStatus.Approved, CampaignStatus.Scheduled, CampaignStatus.Active, CampaignStatus.Paused];
    private readonly ICampaignDAO _campaigns;
    private readonly ICouponDAO _coupons;
    private readonly IMembershipAuditDAO _audit;
    private readonly IOutboxDAO _outbox;
    private readonly ApprovalService _approvals;
    private readonly MembershipRulesProvider _rules;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public CampaignService(ICampaignDAO campaigns, ICouponDAO coupons, IMembershipAuditDAO audit, IOutboxDAO outbox, ApprovalService approvals, MembershipRulesProvider rules, TimeProvider clock)
    {
        _campaigns = campaigns;
        _coupons = coupons;
        _audit = audit;
        _outbox = outbox;
        _approvals = approvals;
        _rules = rules;
        _clock = clock;
    }

    /// <inheritdoc />
    public ApprovalType Type => ApprovalType.Campaign;

    /// <summary>ค้นหาแคมเปญ</summary>
    public async Task<PagedResultDTO<CampaignSummaryDTO>> SearchAsync(MembershipCaller caller, CampaignSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PromotionRead);
        PagedResultDTO<CampaignDTO> page = await _campaigns.SearchAsync(criteria, ct).ConfigureAwait(false);
        await FillUsageAsync(page.Items, ct).ConfigureAwait(false);
        return new PagedResultDTO<CampaignSummaryDTO>([.. page.Items.Select(ToSummary)], page.Total, page.Page, page.PageSize);
    }

    /// <summary>แคมเปญหนึ่งรายการ (รวมการใช้)</summary>
    public async Task<CampaignDTO> GetAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PromotionRead);
        CampaignDTO c = await LoadAsync(id, ct).ConfigureAwait(false);
        await FillUsageAsync([c], ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>สร้างแคมเปญ (Draft)</summary>
    public async Task<CampaignDTO> CreateAsync(MembershipCaller caller, CampaignRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PromotionWrite);
        if (await _campaigns.GetByCodeAsync(request.Code, ct).ConfigureAwait(false) is not null)
        {
            throw MembershipException.Conflict("conflict", "Campaign code already exists.");
        }

        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        CampaignDTO c = Build(request, rules);
        await ValidateAsync(c, ct).ConfigureAwait(false);
        c.Status = CampaignStatus.Draft;
        c.CreatedBy = caller.User.Subject;
        c.CreatedAt = now;
        c.UpdatedAt = now;
        c.Version = 1;
        c.Id = await _campaigns.InsertAsync(c, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "campaign.create", $"campaign:{c.Id}", null, c, now)], ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>แก้แคมเปญ — แก้ field ที่ต้องอนุมัติตอนอนุมัติแล้ว/กำลังใช้ = กลับไปรออนุมัติ</summary>
    public async Task<CampaignDTO> UpdateAsync(MembershipCaller caller, long id, CampaignRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PromotionWrite);
        MembershipEvents.RequireVersion(request.Version);
        CampaignDTO before = await LoadAsync(id, ct).ConfigureAwait(false);
        if (before.Version != request.Version)
        {
            throw MembershipException.VersionConflict();
        }

        if (before.Status is CampaignStatus.PendingApproval or CampaignStatus.Ended or CampaignStatus.Cancelled)
        {
            throw MembershipException.Conflict("invalid_transition", $"A {before.Status} campaign cannot be edited.");
        }

        ValidationException.ThrowIf(!string.Equals(before.Code, request.Code, StringComparison.Ordinal), "code", "Campaign code cannot be changed.");
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        CampaignDTO c = Build(request, rules);
        await ValidateAsync(c, ct).ConfigureAwait(false);
        c.Id = id;
        c.CreatedBy = before.CreatedBy;
        c.CreatedAt = before.CreatedAt;
        c.Version = before.Version;
        c.ApprovalRequestId = before.ApprovalRequestId;
        c.UpdatedAt = _clock.GetUtcNow();
        bool reapprove = before.Status is CampaignStatus.Approved or CampaignStatus.Scheduled or CampaignStatus.Active or CampaignStatus.Paused
            && CampaignRules.RequiresReapproval(before, c);
        c.Status = before.Status == CampaignStatus.Rejected ? CampaignStatus.Draft : before.Status;
        if (reapprove)
        {
            ApprovalRequestDTO ar = await _approvals.SubmitAsync(caller, ApprovalType.Campaign, id, $"แก้แคมเปญ {c.Code}", CampaignRules.ApprovalLevels(c, rules), before, c, null, "edit", ct).ConfigureAwait(false);
            c.Status = CampaignStatus.PendingApproval;
            c.ApprovalRequestId = ar.Id;
        }

        await SaveAsync(caller, c, "campaign.update", before, ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>ส่งอนุมัติ</summary>
    public async Task<CampaignDTO> SubmitAsync(MembershipCaller caller, long id, CampaignActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PromotionWrite);
        CampaignDTO c = await LoadForActionAsync(id, request, ct).ConfigureAwait(false);
        if (c.Status == CampaignStatus.Rejected)
        {
            c.Status = CampaignStatus.Draft;
        }

        Transition(c, CampaignStatus.PendingApproval);
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        ApprovalRequestDTO ar = await _approvals.SubmitAsync(caller, ApprovalType.Campaign, id, $"แคมเปญ {c.Code} — {c.Name}", CampaignRules.ApprovalLevels(c, rules), null, c, null, request.Reason, ct).ConfigureAwait(false);
        c.ApprovalRequestId = ar.Id;
        await SaveAsync(caller, c, "campaign.submit", null, ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>หยุดชั่วคราว</summary>
    public Task<CampaignDTO> PauseAsync(MembershipCaller caller, long id, CampaignActionRequest request, CancellationToken ct) =>
        SimpleTransitionAsync(caller, id, request, CampaignStatus.Paused, "campaign.pause", ct);

    /// <summary>ใช้งานต่อ</summary>
    public Task<CampaignDTO> ResumeAsync(MembershipCaller caller, long id, CampaignActionRequest request, CancellationToken ct) =>
        SimpleTransitionAsync(caller, id, request, CampaignStatus.Active, "campaign.resume", ct);

    /// <summary>ยกเลิก</summary>
    public Task<CampaignDTO> CancelAsync(MembershipCaller caller, long id, CampaignActionRequest request, CancellationToken ct) =>
        SimpleTransitionAsync(caller, id, request, CampaignStatus.Cancelled, "campaign.cancel", ct);

    /// <inheritdoc />
    public async Task OnApprovedAsync(MembershipCaller approver, ApprovalRequestDTO request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        CampaignDTO c = await LoadAsync(request.EntityId, ct).ConfigureAwait(false);
        if (c.Status != CampaignStatus.PendingApproval)
        {
            return;
        }

        DateTimeOffset now = _clock.GetUtcNow();
        c.Status = CampaignStatus.Approved;
        c.Status = c.EndAt <= now ? CampaignStatus.Ended : c.StartAt <= now ? CampaignStatus.Active : CampaignStatus.Scheduled;
        await SaveAsync(approver, c, "campaign.approved", null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task OnRejectedAsync(MembershipCaller actor, ApprovalRequestDTO request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        CampaignDTO c = await LoadAsync(request.EntityId, ct).ConfigureAwait(false);
        if (c.Status != CampaignStatus.PendingApproval)
        {
            return;
        }

        c.Status = request.Status == ApprovalStatus.Cancelled ? CampaignStatus.Draft : CampaignStatus.Rejected;
        await SaveAsync(actor, c, "campaign.rejected", null, ct).ConfigureAwait(false);
    }

    /// <summary>งานทุกนาที: Scheduled → Active → Ended, งบหมด → Ended</summary>
    public async Task<int> RunScheduleAsync(CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        IReadOnlyList<CampaignDTO> list = await _campaigns.ListByStatusAsync(ScheduleStatuses, ct).ConfigureAwait(false);
        await FillUsageAsync(list, ct).ConfigureAwait(false);
        int changed = 0;
        foreach (CampaignDTO c in list)
        {
            CampaignStatus? next = c.Status switch
            {
                _ when c.EndAt <= now && c.Status is CampaignStatus.Active or CampaignStatus.Paused or CampaignStatus.Scheduled or CampaignStatus.Approved => CampaignStatus.Ended,
                CampaignStatus.Active when c.Limits.Budget is decimal b && c.Usage.BudgetUsed >= b => CampaignStatus.Ended,
                CampaignStatus.Approved or CampaignStatus.Scheduled when c.StartAt <= now => CampaignStatus.Active,
                CampaignStatus.Approved => CampaignStatus.Scheduled,
                _ => null,
            };
            if (next is null)
            {
                continue;
            }

            c.Status = next.Value;
            try
            {
                await SaveAsync(MembershipCaller.System, c, "campaign.schedule", null, ct).ConfigureAwait(false);
                changed++;
            }
            catch (MembershipException)
            {
                // มีคนแก้พร้อมกัน — รอบถัดไป
            }
        }

        return changed;
    }

    /// <summary>แคมเปญที่ใช้ได้ (Active)</summary>
    internal Task<IReadOnlyList<CampaignDTO>> ListActiveAsync(CancellationToken ct) => _campaigns.ListByStatusAsync([CampaignStatus.Active], ct);

    /// <summary>แปลงเป็นแถวสรุป</summary>
    internal static CampaignSummaryDTO ToSummary(CampaignDTO c) => new()
    {
        Id = c.Id, Code = c.Code, Name = c.Name, Description = c.Description, RewardType = c.RewardType, Status = c.Status, StartAt = c.StartAt, EndAt = c.EndAt,
        Budget = c.Limits.Budget, BudgetUsed = c.Usage.BudgetUsed, Uses = c.Usage.Uses,
    };

    private async Task<CampaignDTO> SimpleTransitionAsync(MembershipCaller caller, long id, CampaignActionRequest request, CampaignStatus to, string action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PromotionWrite);
        CampaignDTO c = await LoadForActionAsync(id, request, ct).ConfigureAwait(false);
        if (to == CampaignStatus.Active && c.Status != CampaignStatus.Paused)
        {
            throw MembershipException.InvalidTransition(c.Status, to);
        }

        if (to is CampaignStatus.Paused or CampaignStatus.Cancelled)
        {
            ValidationException.ThrowIf(string.IsNullOrWhiteSpace(request.Reason), "reason", "A reason is required.");
        }

        Transition(c, to);
        if (to == CampaignStatus.Active && c.EndAt <= _clock.GetUtcNow())
        {
            c.Status = CampaignStatus.Ended;
        }

        await SaveAsync(caller, c, action, new { reason = request.Reason }, ct).ConfigureAwait(false);
        return c;
    }

    private async Task<CampaignDTO> LoadForActionAsync(long id, CampaignActionRequest request, CancellationToken ct)
    {
        MembershipEvents.RequireVersion(request.Version);
        CampaignDTO c = await LoadAsync(id, ct).ConfigureAwait(false);
        return c.Version == request.Version ? c : throw MembershipException.VersionConflict();
    }

    private static void Transition(CampaignDTO c, CampaignStatus to)
    {
        if (!CampaignRules.CanTransition(c.Status, to))
        {
            throw MembershipException.InvalidTransition(c.Status, to);
        }

        c.Status = to;
    }

    private async Task SaveAsync(MembershipCaller caller, CampaignDTO c, string action, object? before, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        c.UpdatedAt = now;
        if (!await _campaigns.UpdateAsync(c, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        c.Version++;
        await _audit.AppendAsync([MembershipEvents.Audit(caller, action, $"campaign:{c.Id}", before, new { c.Status, c.Name, c.StartAt, c.EndAt }, now)], ct).ConfigureAwait(false);
        await _outbox.EnqueueAsync([MembershipEvents.Outbox("campaign.status_changed", new { campaignId = c.Id, c.Code, status = c.Status }, now)], ct).ConfigureAwait(false);
    }

    private async Task<CampaignDTO> LoadAsync(long id, CancellationToken ct) =>
        await _campaigns.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Campaign not found.");

    private async Task FillUsageAsync(IReadOnlyList<CampaignDTO> list, CancellationToken ct)
    {
        if (list.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<(long, string), CampaignUsageDTO> counters = await _campaigns.GetCountersAsync([.. list.Select(c => c.Id)], ["total"], ct).ConfigureAwait(false);
        foreach (CampaignDTO c in list)
        {
            c.Usage = counters.TryGetValue((c.Id, "total"), out CampaignUsageDTO? u) ? u : new CampaignUsageDTO();
        }
    }

    private async Task ValidateAsync(CampaignDTO c, CancellationToken ct)
    {
        CampaignRules.Validate(c);
        if (c.RewardType == RewardType.IssueCoupon && await _coupons.GetBatchAsync(c.Reward.CouponBatchId!.Value, ct).ConfigureAwait(false) is null)
        {
            throw new ValidationException("Coupon batch not found.", new Dictionary<string, string[]> { ["reward.couponBatchId"] = ["Coupon batch not found."] });
        }
    }

    private static CampaignDTO Build(CampaignRequest r, MembershipRulesDTO rules) => new()
    {
        Code = r.Code,
        Name = r.Name.Trim(),
        Description = r.Description,
        StartAt = r.StartAt,
        EndAt = r.EndAt,
        RewardType = r.RewardType,
        Reward = r.Reward.Clone(),
        Level = r.Level,
        RequiresCoupon = r.RequiresCoupon,
        Conditions = r.Conditions.Clone(),
        Targets = r.Targets.Clone(),
        StackMode = r.StackMode,
        ExclusionGroup = string.IsNullOrWhiteSpace(r.ExclusionGroup) ? null : r.ExclusionGroup.Trim(),
        Priority = r.Priority,
        Limits = r.Limits?.Clone() ?? new CampaignLimitsDTO { PerMemberPerDay = rules.DefaultPerMemberPerDay > 0 ? rules.DefaultPerMemberPerDay : null },
    };
}
