using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>Tier (MEM-005): จัดการระดับ, ขึ้น Tier ทันทีหลังซื้อ, ตรวจคง/ลดตอนครบรอบ (spec 2.8)</summary>
public sealed class TierService
{
    private readonly ITierDAO _tiers;
    private readonly IMemberDAO _members;
    private readonly ICheckoutDAO _checkout;
    private readonly IMembershipAuditDAO _audit;
    private readonly IMembershipUnitOfWork _uow;
    private readonly MembershipRulesProvider _rules;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public TierService(ITierDAO tiers, IMemberDAO members, ICheckoutDAO checkout, IMembershipAuditDAO audit, IMembershipUnitOfWork uow, MembershipRulesProvider rules, TimeProvider clock)
    {
        _tiers = tiers;
        _members = members;
        _checkout = checkout;
        _audit = audit;
        _uow = uow;
        _rules = rules;
        _clock = clock;
    }

    /// <summary>Tier ทั้งหมด (เรียงตาม rank)</summary>
    public async Task<IReadOnlyList<TierDTO>> ListAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.MemberRead);
        return [.. (await _tiers.ListAsync(ct).ConfigureAwait(false)).OrderBy(t => t.Rank)];
    }

    /// <summary>Tier หนึ่งรายการ</summary>
    public async Task<TierDTO> GetAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.MemberRead);
        return await _tiers.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Tier not found.");
    }

    /// <summary>สร้าง Tier</summary>
    public async Task<TierDTO> CreateAsync(MembershipCaller caller, TierRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.TierManage);
        TierDTO t = await BuildAsync(request, null, ct).ConfigureAwait(false);
        t.Version = 1;
        t.Id = await _tiers.InsertAsync(t, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "tier.create", $"tier:{t.Id}", null, t, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        return t;
    }

    /// <summary>แก้ Tier (ตรวจ version)</summary>
    public async Task<TierDTO> UpdateAsync(MembershipCaller caller, long id, TierRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.TierManage);
        MembershipEvents.RequireVersion(request.Version);
        TierDTO before = await _tiers.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Tier not found.");
        ValidationException.ThrowIf(!string.Equals(before.Code, request.Code, StringComparison.Ordinal), "code", "Tier code cannot be changed.");
        TierDTO t = await BuildAsync(request, id, ct).ConfigureAwait(false);
        t.Id = id;
        t.Version = request.Version!.Value;
        if (!await _tiers.UpdateAsync(t, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        t.Version++;
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "tier.update", $"tier:{id}", before, t, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        return t;
    }

    /// <summary>
    /// สั่งประเมิน Tier: ระบุสมาชิก = ประเมินทันที (ขึ้น/ครบรอบ), ไม่ระบุ = รันงานครบรอบของทุกคนที่ถึงกำหนด
    /// </summary>
    public async Task<JobDTO> EvaluateAsync(MembershipCaller caller, long? memberId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.TierManage);
        if (memberId is long id)
        {
            MemberEntity m = await _members.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Member not found.");
            string? changed = await ReviewMemberAsync(caller, m, force: true, ct).ConfigureAwait(false);
            return new JobDTO { JobId = $"tier:{id}", Kind = "tier.evaluate", Status = JobStatus.Done, Message = changed is null ? "No change" : $"Tier changed to {changed}" };
        }

        int count = await RunReviewsAsync(ct).ConfigureAwait(false);
        return new JobDTO { JobId = $"tier:all:{_clock.GetUtcNow():yyyyMMddHHmm}", Kind = "tier.evaluate", Status = JobStatus.Done, Message = $"{count} members reviewed" };
    }

    /// <summary>งานรายวัน: ตรวจคง/ลด Tier ของสมาชิกที่ครบรอบ</summary>
    public async Task<int> RunReviewsAsync(CancellationToken ct)
    {
        int total = 0;
        while (true)
        {
            IReadOnlyList<MemberEntity> due = await _members.ListTierReviewDueAsync(_clock.GetUtcNow(), 200, ct).ConfigureAwait(false);
            if (due.Count == 0)
            {
                return total;
            }

            foreach (MemberEntity m in due)
            {
                try
                {
                    await ReviewMemberAsync(MembershipCaller.System, m, force: true, ct).ConfigureAwait(false);
                }
                catch (MembershipConcurrencyException)
                {
                    // มีรายการอื่นแก้สมาชิกพร้อมกัน — รอบถัดไปค่อยตรวจ
                }

                total++;
            }

            if (due.Count < 200)
            {
                return total;
            }
        }
    }

    /// <summary>
    /// ขึ้น Tier หลังซื้อ (ใส่ใน change set ของ commit) — ยอดซื้อในรอบรวมบิลปัจจุบัน
    /// </summary>
    /// <returns>รหัส Tier ใหม่ หรือ null</returns>
    public async Task<string?> ApplyUpgradeAsync(MemberEntity member, decimal currentEligible, MembershipChangeSet cs, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(cs);
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        decimal spend = await _checkout.GetQualifyingSpendAsync(member.Id, now.AddMonths(-rules.TierWindowMonths), ct).ConfigureAwait(false) + currentEligible;
        TierDTO? up = TierRules.Upgrade(await _tiers.ListAsync(ct).ConfigureAwait(false), member.TierCode, spend);
        if (up is null)
        {
            return null;
        }

        ChangeTier(member, up.Code, "Upgrade", spend, now, rules, cs, actor);
        return up.Code;
    }

    private async Task<string?> ReviewMemberAsync(MembershipCaller caller, MemberEntity m, bool force, CancellationToken ct)
    {
        if (m.Status == MemberStatus.Closed)
        {
            return null;
        }

        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        decimal spend = await _checkout.GetQualifyingSpendAsync(m.Id, now.AddMonths(-rules.TierWindowMonths), ct).ConfigureAwait(false);
        IReadOnlyList<TierDTO> tiers = await _tiers.ListAsync(ct).ConfigureAwait(false);
        bool due = m.TierReviewAt is null || m.TierReviewAt <= now;
        (TierDTO? target, string reason) = due && force ? TierRules.Review(tiers, m.TierCode, spend) : (TierRules.Upgrade(tiers, m.TierCode, spend), "Upgrade");
        if (target is null)
        {
            return null;
        }

        var cs = new MembershipChangeSet();
        bool changed = !string.Equals(target.Code, m.TierCode, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            ChangeTier(m, target.Code, reason, spend, now, rules, cs, caller.Actor);
        }
        else if (due)
        {
            // คง Tier: เลื่อนวันตรวจครบรอบ
            m.TierReviewAt = now.AddMonths(rules.TierWindowMonths);
            m.UpdatedAt = now;
            cs.MemberUpdates.Add(m);
            cs.TierHistory.Add(new TierHistoryDTO { MemberId = m.Id, FromTier = m.TierCode, ToTier = target.Code, Reason = "Retain", QualifyingSpend = spend, ChangedAt = now });
        }
        else
        {
            return null;
        }

        await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
        return changed ? target.Code : null;
    }

    private static void ChangeTier(MemberEntity m, string code, string reason, decimal spend, DateTimeOffset now, MembershipRulesDTO rules, MembershipChangeSet cs, string actor)
    {
        string? from = m.TierCode;
        m.TierCode = code;
        m.TierSince = now;
        m.TierReviewAt = now.AddMonths(rules.TierWindowMonths);
        m.UpdatedAt = now;
        if (!cs.MemberUpdates.Contains(m))
        {
            cs.MemberUpdates.Add(m);
        }

        cs.TierHistory.Add(new TierHistoryDTO { MemberId = m.Id, FromTier = from, ToTier = code, Reason = reason, QualifyingSpend = spend, ChangedAt = now });
        cs.Outbox.Add(MembershipEvents.Outbox("member.tier_changed", new { memberId = m.Id, m.MemberNo, from, to = code, reason }, now));
        cs.Audit.Add(new MembershipAuditDTO { At = now, Actor = actor, ActorType = actor == "system" ? ActorType.System : ActorType.Client, Action = "member.tier_change", Target = $"member:{m.Id}", BeforeMasked = MembershipJson.Write(new { tier = from }), AfterMasked = MembershipJson.Write(new { tier = code, reason, spend }) });
    }

    private async Task<TierDTO> BuildAsync(TierRequest r, long? id, CancellationToken ct)
    {
        ValidationException.ThrowIf(r.RetainThreshold > r.UpgradeThreshold, "retainThreshold", "Retain threshold must not exceed the upgrade threshold.");
        IReadOnlyList<TierDTO> all = await _tiers.ListAsync(ct).ConfigureAwait(false);
        if (all.Any(t => t.Id != id && string.Equals(t.Code, r.Code, StringComparison.OrdinalIgnoreCase)))
        {
            throw MembershipException.Conflict("conflict", "Tier code already exists.");
        }

        if (all.Any(t => t.Id != id && t.Rank == r.Rank))
        {
            throw MembershipException.Conflict("conflict", "Another tier already has this rank.");
        }

        return new TierDTO
        {
            Code = r.Code, Name = r.Name.Trim(), Rank = r.Rank, UpgradeThreshold = r.UpgradeThreshold, RetainThreshold = r.RetainThreshold,
            PointMultiplier = r.PointMultiplier, Benefits = [.. r.Benefits.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim())], IsActive = r.IsActive,
        };
    }
}
