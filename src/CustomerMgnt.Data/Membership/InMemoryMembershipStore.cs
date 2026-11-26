using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Membership.Services;

namespace CustomerMgnt.Data.Membership;

/// <summary>
/// ฐานข้อมูลระบบสมาชิกในหน่วยความจำ (Development / test) — implement ทุก DAO ของระบบสมาชิกด้วย lock เดียว
/// พฤติกรรมเหมือน ADO: ตรวจ version, unique key, ตัวนับสิทธิ์ และคืนสำเนาเสมอ
/// </summary>
public sealed class InMemoryMembershipStore : IMemberDAO, ITierDAO, IPointDAO, ICampaignDAO, ICouponDAO, ISegmentDAO, ICheckoutDAO, IApprovalDAO,
    IMembershipAuditDAO, IOutboxDAO, IIdempotencyDAO, IMembershipSettingsDAO, IMemberPrivacyDAO, IMembershipJobDAO, IMembershipUnitOfWork
{
    private readonly Lock _sync = new();
    private long _id;

    private readonly Dictionary<long, MemberEntity> _members = [];
    private readonly List<MemberConsentDTO> _consents = [];
    private readonly List<TierHistoryDTO> _tierHistory = [];
    private readonly Dictionary<long, TierDTO> _tiers = [];
    private readonly Dictionary<long, PointAccountDTO> _accounts = [];
    private readonly Dictionary<long, PointLotDTO> _lots = [];
    private readonly List<PointLedgerDTO> _ledger = [];
    private readonly HashSet<string> _ledgerKeys = new(StringComparer.Ordinal);
    private readonly List<LotAllocationDTO> _allocations = [];
    private readonly Dictionary<long, EarnRuleDTO> _rules = [];
    private readonly Dictionary<long, AdjustmentDTO> _adjustments = [];
    private readonly Dictionary<long, CampaignDTO> _campaigns = [];
    private readonly Dictionary<(long, string), CampaignUsageDTO> _counters = [];
    private readonly Dictionary<long, CouponBatchDTO> _batches = [];
    private readonly Dictionary<string, CouponDTO> _coupons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CouponEventDTO> _couponEvents = [];
    private readonly Dictionary<long, SegmentDTO> _segments = [];
    private readonly Dictionary<long, HashSet<long>> _segmentMembers = [];
    private readonly Dictionary<string, ReservationEntity> _reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<long, SalesTransactionEntity> _transactions = [];
    private readonly List<SalesReturnDTO> _returns = [];
    private readonly Dictionary<long, PendingReturnEntity> _pendingReturns = [];
    private readonly Dictionary<long, ApprovalRequestDTO> _approvals = [];
    private readonly List<MembershipAuditDTO> _audit = [];
    private readonly Dictionary<long, OutboxMessageDTO> _outbox = [];
    private readonly Dictionary<(string, string), IdempotencyRecordDTO> _idempotency = [];
    private MembershipRulesDTO _settings = new();
    private readonly Dictionary<long, ConsentVersionDTO> _consentVersions = [];
    private readonly Dictionary<string, IntegrationClientDTO> _integrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OtpChallengeEntity> _otps = new(StringComparer.Ordinal);
    private readonly Dictionary<long, PrivacyRequestDTO> _privacy = [];
    private readonly Dictionary<string, (string Holder, DateTimeOffset Until)> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<long, ExportJobDTO> _exports = [];
    private readonly Dictionary<long, MessageJobDTO> _messageJobs = [];

    private long NextId() => ++_id;

    private static T C<T>(T value) where T : new() => MembershipJson.Read<T>(MembershipJson.Write(value));

    private static List<T> CList<T>(IEnumerable<T> values) where T : new() => [.. values.Select(C)];

    private static PagedResultDTO<T> Page<T>(IEnumerable<T> items, int page, int pageSize) where T : new()
    {
        List<T> all = [.. items];
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 5000);
        return new PagedResultDTO<T>(CList(all.Skip((page - 1) * pageSize).Take(pageSize)), all.Count, page, pageSize);
    }

    private Task<T> Run<T>(Func<T> f)
    {
        lock (_sync)
        {
            return Task.FromResult(f());
        }
    }

    private Task Run(Action f)
    {
        lock (_sync)
        {
            f();
            return Task.CompletedTask;
        }
    }

    // ================================================================== IMemberDAO

    public Task<MemberEntity?> GetAsync(long id, CancellationToken ct) => Run(() => _members.TryGetValue(id, out MemberEntity? m) ? WithPoints(C(m)) : null);

    public Task<MemberEntity?> GetByNoAsync(string memberNo, CancellationToken ct) =>
        Run(() => _members.Values.FirstOrDefault(m => string.Equals(m.MemberNo, memberNo, StringComparison.OrdinalIgnoreCase)) is MemberEntity m ? WithPoints(C(m)) : null);

    public Task<MemberEntity?> GetBySubjectAsync(string subject, CancellationToken ct) =>
        Run(() => _members.Values.FirstOrDefault(m => string.Equals(m.AuthSubject, subject, StringComparison.Ordinal)) is MemberEntity m ? WithPoints(C(m)) : null);

    public Task<IReadOnlyList<MemberEntity>> FindByPhoneHashAsync(string phoneHash, CancellationToken ct) =>
        Run<IReadOnlyList<MemberEntity>>(() => CList(_members.Values.Where(m => m.PhoneHash == phoneHash).OrderBy(m => m.Id)));

    public Task<IReadOnlyList<MemberEntity>> FindByEmailHashAsync(string emailHash, CancellationToken ct) =>
        Run<IReadOnlyList<MemberEntity>>(() => CList(_members.Values.Where(m => m.EmailHash == emailHash).OrderBy(m => m.Id)));

    public Task<PagedResultDTO<MemberEntity>> SearchAsync(MemberSearchCriteria c, CancellationToken ct) => Run(() =>
    {
        IEnumerable<MemberEntity> q = _members.Values;
        if (!string.IsNullOrWhiteSpace(c.Text))
        {
            string t = c.Text.Trim();
            q = q.Where(m => m.MemberNo.Contains(t, StringComparison.OrdinalIgnoreCase) || m.FirstName.Contains(t, StringComparison.OrdinalIgnoreCase) || m.LastName.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(c.MemberNo)) q = q.Where(m => string.Equals(m.MemberNo, c.MemberNo.Trim(), StringComparison.OrdinalIgnoreCase));
        if (c.PhoneHash is not null) q = q.Where(m => m.PhoneHash == c.PhoneHash);
        if (c.EmailHash is not null) q = q.Where(m => m.EmailHash == c.EmailHash);
        if (c.Status is MemberStatus s) q = q.Where(m => m.Status == s);
        if (!string.IsNullOrWhiteSpace(c.TierCode)) q = q.Where(m => string.Equals(m.TierCode, c.TierCode, StringComparison.OrdinalIgnoreCase));
        if (c.SegmentId is long sid) q = q.Where(m => _segmentMembers.TryGetValue(sid, out HashSet<long>? set) && set.Contains(m.Id));
        if (!string.IsNullOrWhiteSpace(c.CustomerCode)) q = q.Where(m => string.Equals(m.CustomerCode, c.CustomerCode, StringComparison.OrdinalIgnoreCase));
        PagedResultDTO<MemberEntity> p = Page(q.OrderByDescending(m => m.Id), c.Page, c.PageSize);
        foreach (MemberEntity m in p.Items) WithPoints(m);
        return p;
    });

    public Task<MemberEntity> InsertAsync(MemberEntity member, MemberConsentDTO? consent, IReadOnlyList<OutboxMessageDTO> outbox, CancellationToken ct) => Run(() =>
    {
        MemberEntity m = C(member);
        m.Id = NextId();
        m.MemberNo = $"M{m.CreatedAt.Year % 100:00}{m.Id:00000}";
        m.Version = Math.Max(m.Version, 1);
        _members[m.Id] = m;
        if (consent is not null)
        {
            MemberConsentDTO cc = C(consent);
            cc.Id = NextId();
            cc.MemberId = m.Id;
            _consents.Add(cc);
        }

        foreach (OutboxMessageDTO o in outbox)
        {
            OutboxMessageDTO x = C(o);
            x.Id = NextId();
            x.PayloadJson = MembershipJson.WithMember(x.PayloadJson, m.Id, m.MemberNo);
            _outbox[x.Id] = x;
        }

        return C(m);
    });

    public Task<bool> UpdateAsync(MemberEntity member, CancellationToken ct) => Run(() =>
    {
        if (!_members.TryGetValue(member.Id, out MemberEntity? cur) || cur.Version != member.Version) return false;
        MemberEntity m = C(member);
        m.Version++;
        _members[m.Id] = m;
        return true;
    });

    public Task<IReadOnlyList<MemberEntity>> ListTierReviewDueAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        Run<IReadOnlyList<MemberEntity>>(() => CList(_members.Values.Where(m => m.Status != MemberStatus.Closed && m.TierCode is not null && m.TierReviewAt <= now).OrderBy(m => m.TierReviewAt).Take(take)));

    public Task<IReadOnlyList<MemberEntity>> ListWithoutCustomerAsync(int take, CancellationToken ct) =>
        Run<IReadOnlyList<MemberEntity>>(() => CList(_members.Values.Where(m => m.Status == MemberStatus.Active && m.CustomerCode is null).OrderBy(m => m.Id).Take(take)));

    public Task<IReadOnlyList<MemberConsentDTO>> ListConsentsAsync(long memberId, CancellationToken ct) =>
        Run<IReadOnlyList<MemberConsentDTO>>(() => CList(_consents.Where(c => c.MemberId == memberId).OrderBy(c => c.At)));

    public Task AddConsentAsync(MemberConsentDTO consent, CancellationToken ct) => Run(() =>
    {
        MemberConsentDTO c = C(consent);
        c.Id = NextId();
        _consents.Add(c);
    });

    public Task<IReadOnlyList<TierHistoryDTO>> ListTierHistoryAsync(long memberId, CancellationToken ct) =>
        Run<IReadOnlyList<TierHistoryDTO>>(() => CList(_tierHistory.Where(h => h.MemberId == memberId).OrderByDescending(h => h.ChangedAt)));

    public Task<IReadOnlyList<MemberFactsDTO>> ListFactsAsync(long afterId, int take, DateTimeOffset now, CancellationToken ct) => Run<IReadOnlyList<MemberFactsDTO>>(() =>
    {
        DateTimeOffset since = now.AddMonths(-12);
        var result = new List<MemberFactsDTO>();
        foreach (MemberEntity m in _members.Values.Where(m => m.Id > afterId).OrderBy(m => m.Id).Take(take))
        {
            List<SalesTransactionEntity> txns = [.. _transactions.Values.Where(t => t.MemberId == m.Id && t.Status != TransactionStatus.Cancelled)];
            List<SalesTransactionEntity> recent = [.. txns.Where(t => t.OccurredAt >= since)];
            DateTimeOffset? last = txns.Count == 0 ? null : txns.Max(t => t.OccurredAt);
            result.Add(new MemberFactsDTO
            {
                MemberId = m.Id, TierCode = m.TierCode, Status = m.Status, AgeYears = m.BirthYear is int y ? now.Year - y : null, Gender = m.Gender, Province = m.Province,
                PointsAvailable = Account(m.Id).Available, Spend12m = recent.Sum(t => t.Eligible - t.ReturnedEligible), Frequency12m = recent.Count,
                RecencyDays = last is null ? null : (int)(now - last.Value).TotalDays, MarketingConsent = m.MarketingConsent,
            });
        }

        return result;
    });

    public Task<MemberStatsDTO> GetStatsAsync(DateTimeOffset since, CancellationToken ct) => Run(() => new MemberStatsDTO
    {
        Total = _members.Values.Count(m => m.Status != MemberStatus.Closed),
        Active = _members.Values.Count(m => m.Status == MemberStatus.Active),
        NewSince = _members.Values.Count(m => m.CreatedAt >= since),
        ByTier = _members.Values.Where(m => m.Status == MemberStatus.Active).GroupBy(m => m.TierCode ?? "-").ToDictionary(g => g.Key, g => g.Count()),
    });

    private MemberEntity WithPoints(MemberEntity m)
    {
        m.PointsAvailable = Account(m.Id).Available;
        return m;
    }

    private PointAccountDTO Account(long memberId) => _accounts.TryGetValue(memberId, out PointAccountDTO? a) ? a : new PointAccountDTO { MemberId = memberId };

    // ================================================================== ITierDAO

    Task<IReadOnlyList<TierDTO>> ITierDAO.ListAsync(CancellationToken ct) => Run<IReadOnlyList<TierDTO>>(() => CList(_tiers.Values.OrderBy(t => t.Rank)));

    Task<TierDTO?> ITierDAO.GetAsync(long id, CancellationToken ct) => Run(() => _tiers.TryGetValue(id, out TierDTO? t) ? C(t) : null);

    Task<long> ITierDAO.InsertAsync(TierDTO tier, CancellationToken ct) => Run(() =>
    {
        if (_tiers.Values.Any(t => string.Equals(t.Code, tier.Code, StringComparison.OrdinalIgnoreCase))) throw new ConflictException("Tier code already exists.");
        TierDTO t = C(tier);
        t.Id = NextId();
        t.Version = 1;
        _tiers[t.Id] = t;
        return t.Id;
    });

    Task<bool> ITierDAO.UpdateAsync(TierDTO tier, CancellationToken ct) => Run(() =>
    {
        if (!_tiers.TryGetValue(tier.Id, out TierDTO? cur) || cur.Version != tier.Version) return false;
        TierDTO t = C(tier);
        t.Version++;
        _tiers[t.Id] = t;
        return true;
    });

    // ================================================================== IPointDAO

    public Task<PointAccountDTO> GetAccountAsync(long memberId, CancellationToken ct) => Run(() => C(Account(memberId)));

    public Task<IReadOnlyList<PointLotDTO>> ListLotsAsync(long memberId, bool activeOnly, DateTimeOffset now, CancellationToken ct) =>
        Run<IReadOnlyList<PointLotDTO>>(() => CList(_lots.Values.Where(l => l.MemberId == memberId && (!activeOnly || (l.Remaining > 0 && l.ExpiresAt > now))).OrderBy(l => l.ExpiresAt).ThenBy(l => l.EarnedAt).ThenBy(l => l.Id)));

    public Task<IReadOnlyList<ExpiringPointsDTO>> ListExpiringAsync(long memberId, DateTimeOffset now, DateTimeOffset until, CancellationToken ct) =>
        Run<IReadOnlyList<ExpiringPointsDTO>>(() => [.. _lots.Values.Where(l => l.MemberId == memberId && l.Remaining > 0 && l.ExpiresAt > now && l.ExpiresAt <= until)
            .GroupBy(l => l.ExpiresAt).OrderBy(g => g.Key).Select(g => new ExpiringPointsDTO { ExpiresAt = g.Key, Points = g.Sum(l => l.Remaining) })]);

    public Task<PagedResultDTO<PointLedgerDTO>> SearchLedgerAsync(LedgerCriteria c, CancellationToken ct) => Run(() =>
    {
        IEnumerable<PointLedgerDTO> q = _ledger;
        if (c.MemberId is long mid) q = q.Where(l => l.MemberId == mid);
        if (c.Type is LedgerType t) q = q.Where(l => l.Type == t);
        if (c.From is DateTimeOffset f) q = q.Where(l => l.CreatedAt >= f);
        if (c.To is DateTimeOffset to) q = q.Where(l => l.CreatedAt <= to);
        if (!string.IsNullOrWhiteSpace(c.TransactionId)) q = q.Where(l => l.TransactionId == c.TransactionId);
        return Page(q.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id), c.Page, c.PageSize);
    });

    public Task<bool> LedgerKeyExistsAsync(string idempotencyKey, CancellationToken ct) => Run(() => _ledgerKeys.Contains(idempotencyKey));

    public Task<IReadOnlyList<PointLotDTO>> ListExpiredLotsAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        Run<IReadOnlyList<PointLotDTO>>(() => CList(_lots.Values.Where(l => l.Remaining > 0 && l.ExpiresAt <= now).OrderBy(l => l.ExpiresAt).Take(take)));

    public Task<IReadOnlyList<(long MemberId, int Points, DateTimeOffset ExpiresAt)>> ListExpiringForNoticeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Run<IReadOnlyList<(long, int, DateTimeOffset)>>(() => [.. _lots.Values.Where(l => l.Remaining > 0 && l.ExpiresAt > from && l.ExpiresAt <= to)
            .GroupBy(l => l.MemberId).Select(g => (g.Key, g.Sum(l => l.Remaining), g.Min(l => l.ExpiresAt)))]);

    public Task<int> SumAdjustmentsAsync(string requestedBy, DateTimeOffset since, CancellationToken ct) =>
        Run(() => _adjustments.Values.Where(a => a.RequestedBy == requestedBy && a.CreatedAt >= since && a.Status != AdjustmentStatus.Rejected).Sum(a => Math.Abs(a.Points)));

    Task<PointTotalsDTO> IPointDAO.GetTotalsAsync(DateTimeOffset since, CancellationToken ct) => Run(() => new PointTotalsDTO
    {
        Outstanding = _accounts.Values.Sum(a => (long)a.LotBalance - a.Debt),
        EarnedSince = _ledger.Where(l => l.Type == LedgerType.Earn && l.CreatedAt >= since).Sum(l => (long)l.Points),
        RedeemedSince = -_ledger.Where(l => l.Type == LedgerType.Redeem && l.CreatedAt >= since).Sum(l => (long)l.Points),
    });

    public Task<IReadOnlyList<EarnRuleDTO>> ListRulesAsync(CancellationToken ct) => Run<IReadOnlyList<EarnRuleDTO>>(() => CList(_rules.Values.OrderBy(r => r.Id)));

    public Task<EarnRuleDTO?> GetRuleAsync(long id, CancellationToken ct) => Run(() => _rules.TryGetValue(id, out EarnRuleDTO? r) ? C(r) : null);

    public Task<long> InsertRuleAsync(EarnRuleDTO rule, CancellationToken ct) => Run(() =>
    {
        if (_rules.Values.Any(r => string.Equals(r.Code, rule.Code, StringComparison.OrdinalIgnoreCase))) throw new ConflictException("Rule code already exists.");
        EarnRuleDTO r = C(rule);
        r.Id = NextId();
        r.Version = 1;
        _rules[r.Id] = r;
        return r.Id;
    });

    public Task<bool> UpdateRuleAsync(EarnRuleDTO rule, CancellationToken ct) => Run(() =>
    {
        if (!_rules.TryGetValue(rule.Id, out EarnRuleDTO? cur) || cur.Version != rule.Version) return false;
        EarnRuleDTO r = C(rule);
        r.Version++;
        _rules[r.Id] = r;
        return true;
    });

    public Task<AdjustmentDTO?> GetAdjustmentAsync(long id, CancellationToken ct) => Run(() => _adjustments.TryGetValue(id, out AdjustmentDTO? a) ? C(a) : null);

    public Task<IReadOnlyList<AdjustmentDTO>> ListAdjustmentsAsync(AdjustmentStatus? status, long? memberId, CancellationToken ct) =>
        Run<IReadOnlyList<AdjustmentDTO>>(() => CList(_adjustments.Values.Where(a => (status is null || a.Status == status) && (memberId is null || a.MemberId == memberId)).OrderByDescending(a => a.Id)));

    public Task<long> InsertAdjustmentAsync(AdjustmentDTO adjustment, CancellationToken ct) => Run(() =>
    {
        AdjustmentDTO a = C(adjustment);
        a.Id = NextId();
        _adjustments[a.Id] = a;
        return a.Id;
    });

    public Task UpdateAdjustmentAsync(AdjustmentDTO adjustment, CancellationToken ct) => Run(() => { _adjustments[adjustment.Id] = C(adjustment); });

    // ================================================================== ICampaignDAO

    public Task<PagedResultDTO<CampaignDTO>> SearchAsync(CampaignSearchCriteria c, CancellationToken ct) => Run(() =>
    {
        IEnumerable<CampaignDTO> q = _campaigns.Values;
        if (c.Status is CampaignStatus s) q = q.Where(x => x.Status == s);
        if (c.From is DateTimeOffset f) q = q.Where(x => x.EndAt >= f);
        if (c.To is DateTimeOffset t) q = q.Where(x => x.StartAt <= t);
        if (c.RewardType is RewardType rt) q = q.Where(x => x.RewardType == rt);
        if (!string.IsNullOrWhiteSpace(c.Text)) q = q.Where(x => x.Code.Contains(c.Text.Trim(), StringComparison.OrdinalIgnoreCase) || x.Name.Contains(c.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        return Page(q.OrderByDescending(x => x.Id), c.Page, c.PageSize);
    });

    Task<CampaignDTO?> ICampaignDAO.GetAsync(long id, CancellationToken ct) => Run(() => _campaigns.TryGetValue(id, out CampaignDTO? c) ? C(c) : null);

    Task<CampaignDTO?> ICampaignDAO.GetByCodeAsync(string code, CancellationToken ct) =>
        Run(() => _campaigns.Values.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)) is CampaignDTO c ? C(c) : null);

    public Task<IReadOnlyList<CampaignDTO>> ListByStatusAsync(IReadOnlyCollection<CampaignStatus> statuses, CancellationToken ct) =>
        Run<IReadOnlyList<CampaignDTO>>(() => CList(_campaigns.Values.Where(c => statuses.Contains(c.Status)).OrderBy(c => c.Priority).ThenBy(c => c.Id)));

    public Task<IReadOnlyList<CampaignDTO>> ListByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct) =>
        Run<IReadOnlyList<CampaignDTO>>(() => CList(_campaigns.Values.Where(c => ids.Contains(c.Id)).OrderBy(c => c.Id)));

    Task<long> ICampaignDAO.InsertAsync(CampaignDTO campaign, CancellationToken ct) => Run(() =>
    {
        if (_campaigns.Values.Any(c => string.Equals(c.Code, campaign.Code, StringComparison.OrdinalIgnoreCase))) throw new ConflictException("Campaign code already exists.");
        CampaignDTO c = C(campaign);
        c.Id = NextId();
        c.Version = 1;
        _campaigns[c.Id] = c;
        return c.Id;
    });

    Task<bool> ICampaignDAO.UpdateAsync(CampaignDTO campaign, CancellationToken ct) => Run(() =>
    {
        if (!_campaigns.TryGetValue(campaign.Id, out CampaignDTO? cur) || cur.Version != campaign.Version) return false;
        CampaignDTO c = C(campaign);
        c.Version++;
        _campaigns[c.Id] = c;
        return true;
    });

    public Task<IReadOnlyDictionary<(long CampaignId, string Scope), CampaignUsageDTO>> GetCountersAsync(IReadOnlyCollection<long> campaignIds, IReadOnlyCollection<string> scopes, CancellationToken ct) =>
        Run<IReadOnlyDictionary<(long, string), CampaignUsageDTO>>(() => _counters.Where(kv => campaignIds.Contains(kv.Key.Item1) && scopes.Contains(kv.Key.Item2)).ToDictionary(kv => kv.Key, kv => C(kv.Value)));

    // ================================================================== ICouponDAO

    public Task<IReadOnlyList<CouponBatchDTO>> ListBatchesAsync(long? campaignId, CancellationToken ct) =>
        Run<IReadOnlyList<CouponBatchDTO>>(() => CList(_batches.Values.Where(b => campaignId is null || b.CampaignId == campaignId).OrderByDescending(b => b.Id)));

    public Task<CouponBatchDTO?> GetBatchAsync(long id, CancellationToken ct) => Run(() => _batches.TryGetValue(id, out CouponBatchDTO? b) ? C(b) : null);

    public Task<long> InsertBatchAsync(CouponBatchDTO batch, CancellationToken ct) => Run(() =>
    {
        CouponBatchDTO b = C(batch);
        b.Id = NextId();
        _batches[b.Id] = b;
        return b.Id;
    });

    public Task UpdateBatchAsync(CouponBatchDTO batch, CancellationToken ct) => Run(() => { _batches[batch.Id] = C(batch); });

    Task<CouponDTO?> ICouponDAO.GetByCodeAsync(string code, CancellationToken ct) => Run(() => _coupons.TryGetValue(code, out CouponDTO? c) ? C(c) : null);

    public Task<IReadOnlyList<CouponDTO>> GetByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct) =>
        Run<IReadOnlyList<CouponDTO>>(() => CList(codes.Select(code => _coupons.GetValueOrDefault(code)).OfType<CouponDTO>()));

    public Task<PagedResultDTO<CouponDTO>> SearchAsync(CouponSearchCriteria c, CancellationToken ct) => Run(() =>
    {
        IEnumerable<CouponDTO> q = _coupons.Values;
        if (!string.IsNullOrWhiteSpace(c.Code)) q = q.Where(x => x.Code.Contains(c.Code.Trim(), StringComparison.OrdinalIgnoreCase));
        if (c.BatchId is long b) q = q.Where(x => x.BatchId == b);
        if (c.CampaignId is long cid) q = q.Where(x => x.CampaignId == cid);
        if (c.MemberId is long mid) q = q.Where(x => x.MemberId == mid);
        if (c.Status is CouponStatus s) q = q.Where(x => x.Status == s);
        return Page(q.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Code), c.Page, c.PageSize);
    });

    public Task<IReadOnlyList<CouponDTO>> ListByMemberAsync(long memberId, CouponStatus? status, CancellationToken ct) =>
        Run<IReadOnlyList<CouponDTO>>(() => CList(_coupons.Values.Where(c => c.MemberId == memberId && (status is null || c.Status == status)).OrderBy(c => c.ExpiresAt)));

    public Task<int> InsertManyAsync(IReadOnlyList<CouponDTO> coupons, CancellationToken ct) => Run(() =>
    {
        int n = 0;
        foreach (CouponDTO c in coupons)
        {
            if (_coupons.ContainsKey(c.Code)) continue;
            CouponDTO x = C(c);
            x.Id = NextId();
            x.Version = Math.Max(x.Version, 1);
            _coupons[x.Code] = x;
            n++;
        }

        return n;
    });

    public Task<IReadOnlyList<CouponDTO>> TakeUnassignedAsync(long batchId, int take, CancellationToken ct) =>
        Run<IReadOnlyList<CouponDTO>>(() => CList(_coupons.Values.Where(c => c.BatchId == batchId && c.MemberId is null && c.Status == CouponStatus.Available).OrderBy(c => c.Id).Take(take)));

    public Task<IReadOnlyList<CouponEventDTO>> ListEventsAsync(string code, CancellationToken ct) =>
        Run<IReadOnlyList<CouponEventDTO>>(() => CList(_couponEvents.Where(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.At)));

    public Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct) => Run(() =>
    {
        int n = 0;
        foreach (CouponDTO c in _coupons.Values.Where(c => c.Status is CouponStatus.Available or CouponStatus.Reversed && c.ExpiresAt <= now))
        {
            _couponEvents.Add(new CouponEventDTO { Id = NextId(), Code = c.Code, FromStatus = c.Status, ToStatus = CouponStatus.Expired, Actor = "system", At = now });
            c.Status = CouponStatus.Expired;
            c.Version++;
            n++;
        }

        return n;
    });

    // ================================================================== ISegmentDAO

    Task<IReadOnlyList<SegmentDTO>> ISegmentDAO.ListAsync(CancellationToken ct) => Run<IReadOnlyList<SegmentDTO>>(() => CList(_segments.Values.OrderBy(s => s.Id)));

    Task<SegmentDTO?> ISegmentDAO.GetAsync(long id, CancellationToken ct) => Run(() => _segments.TryGetValue(id, out SegmentDTO? s) ? C(s) : null);

    Task<long> ISegmentDAO.InsertAsync(SegmentDTO segment, CancellationToken ct) => Run(() =>
    {
        SegmentDTO s = C(segment);
        s.Id = NextId();
        s.Version = 1;
        _segments[s.Id] = s;
        return s.Id;
    });

    Task<bool> ISegmentDAO.UpdateAsync(SegmentDTO segment, CancellationToken ct) => Run(() =>
    {
        if (!_segments.TryGetValue(segment.Id, out SegmentDTO? cur) || cur.Version != segment.Version) return false;
        SegmentDTO s = C(segment);
        s.Version++;
        _segments[s.Id] = s;
        return true;
    });

    public Task ReplaceMembersAsync(long segmentId, IReadOnlyList<long> memberIds, DateTimeOffset refreshedAt, CancellationToken ct) => Run(() =>
    {
        _segmentMembers[segmentId] = [.. memberIds];
        if (_segments.TryGetValue(segmentId, out SegmentDTO? s))
        {
            s.MemberCount = memberIds.Count;
            s.RefreshedAt = refreshedAt;
        }
    });

    public Task<IReadOnlySet<long>> ListSegmentIdsOfMemberAsync(long memberId, CancellationToken ct) =>
        Run<IReadOnlySet<long>>(() => _segmentMembers.Where(kv => kv.Value.Contains(memberId)).Select(kv => kv.Key).ToHashSet());

    public Task<PagedResultDTO<long>> PageMemberIdsAsync(long segmentId, int page, int pageSize, CancellationToken ct) => Run(() =>
    {
        List<long> all = _segmentMembers.TryGetValue(segmentId, out HashSet<long>? set) ? [.. set.Order()] : [];
        page = Math.Max(page, 1);
        return new PagedResultDTO<long>([.. all.Skip((page - 1) * pageSize).Take(pageSize)], all.Count, page, pageSize);
    });

    // ================================================================== ICheckoutDAO

    public Task<ReservationEntity?> GetReservationAsync(string id, CancellationToken ct) => Run(() => _reservations.TryGetValue(id, out ReservationEntity? r) ? r.Clone() : null);

    public Task<IReadOnlyList<ReservationEntity>> ListExpiredReservationsAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        Run<IReadOnlyList<ReservationEntity>>(() => [.. _reservations.Values.Where(r => r.Status == ReservationStatus.Reserved && r.ExpiresAt <= now).OrderBy(r => r.ExpiresAt).Take(take).Select(r => r.Clone())]);

    public Task<SalesTransactionEntity?> GetTransactionAsync(string sourceSystem, string transactionId, CancellationToken ct) =>
        Run(() => _transactions.Values.FirstOrDefault(t => t.SourceSystem == sourceSystem && t.TransactionId == transactionId)?.Clone());

    public Task<SalesTransactionEntity?> GetTransactionByIdAsync(long id, CancellationToken ct) => Run(() => _transactions.TryGetValue(id, out SalesTransactionEntity? t) ? t.Clone() : null);

    public Task<PagedResultDTO<SalesTransactionEntity>> SearchTransactionsAsync(TransactionSearchCriteria c, CancellationToken ct) => Run(() =>
    {
        IEnumerable<SalesTransactionEntity> q = _transactions.Values;
        if (c.MemberId is long mid) q = q.Where(t => t.MemberId == mid);
        if (!string.IsNullOrWhiteSpace(c.TransactionId)) q = q.Where(t => t.TransactionId == c.TransactionId);
        if (!string.IsNullOrWhiteSpace(c.SourceSystem)) q = q.Where(t => t.SourceSystem == c.SourceSystem);
        if (!string.IsNullOrWhiteSpace(c.BranchCode)) q = q.Where(t => string.Equals(t.BranchCode, c.BranchCode, StringComparison.OrdinalIgnoreCase));
        if (c.From is DateTimeOffset f) q = q.Where(t => t.OccurredAt >= f);
        if (c.To is DateTimeOffset to) q = q.Where(t => t.OccurredAt <= to);
        if (c.Status is TransactionStatus s) q = q.Where(t => t.Status == s);
        List<SalesTransactionEntity> all = [.. q.OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.Id)];
        int page = Math.Max(c.Page, 1);
        return new PagedResultDTO<SalesTransactionEntity>([.. all.Skip((page - 1) * c.PageSize).Take(c.PageSize).Select(t => t.Clone())], all.Count, page, c.PageSize);
    });

    public Task<IReadOnlyList<SalesReturnDTO>> ListReturnsAsync(string sourceSystem, string transactionId, CancellationToken ct) =>
        Run<IReadOnlyList<SalesReturnDTO>>(() => CList(_returns.Where(r => r.SourceSystem == sourceSystem && r.TransactionId == transactionId).OrderBy(r => r.Id)));

    public Task<decimal> GetQualifyingSpendAsync(long memberId, DateTimeOffset since, CancellationToken ct) =>
        Run(() => _transactions.Values.Where(t => t.MemberId == memberId && t.Status != TransactionStatus.Cancelled && t.OccurredAt >= since).Sum(t => t.Eligible - t.ReturnedEligible));

    Task<SalesTotalsDTO> ICheckoutDAO.GetTotalsAsync(DateTimeOffset since, CancellationToken ct) => Run(() =>
    {
        List<SalesTransactionEntity> t = [.. _transactions.Values.Where(t => t.Status != TransactionStatus.Cancelled && t.OccurredAt >= since)];
        return new SalesTotalsDTO { Sales = t.Sum(x => x.NetPaid), Transactions = t.Count };
    });

    public Task<long> InsertPendingReturnAsync(PendingReturnEntity pending, CancellationToken ct) => Run(() =>
    {
        PendingReturnEntity? existing = _pendingReturns.Values.FirstOrDefault(p => p.SourceSystem == pending.SourceSystem && p.TransactionId == pending.TransactionId && p.ReturnId == pending.ReturnId);
        if (existing is not null) return existing.Id;
        PendingReturnEntity x = C(pending);
        x.Id = NextId();
        _pendingReturns[x.Id] = x;
        return x.Id;
    });

    public Task<IReadOnlyList<PendingReturnEntity>> ListPendingReturnsAsync(int take, CancellationToken ct) =>
        Run<IReadOnlyList<PendingReturnEntity>>(() => CList(_pendingReturns.Values.Where(p => p.ProcessedAt is null).OrderBy(p => p.Id).Take(take)));

    public Task UpdatePendingReturnAsync(PendingReturnEntity pending, CancellationToken ct) => Run(() => { _pendingReturns[pending.Id] = C(pending); });

    // ================================================================== IApprovalDAO

    Task<long> IApprovalDAO.InsertAsync(ApprovalRequestDTO request, CancellationToken ct) => Run(() =>
    {
        ApprovalRequestDTO a = C(request);
        a.Id = NextId();
        a.Version = 1;
        _approvals[a.Id] = a;
        return a.Id;
    });

    Task<ApprovalRequestDTO?> IApprovalDAO.GetAsync(long id, CancellationToken ct) => Run(() => _approvals.TryGetValue(id, out ApprovalRequestDTO? a) ? C(a) : null);

    Task<IReadOnlyList<ApprovalRequestDTO>> IApprovalDAO.SearchAsync(ApprovalSearchCriteria c, CancellationToken ct) =>
        Run<IReadOnlyList<ApprovalRequestDTO>>(() => CList(_approvals.Values.Where(a => (c.Status is null || a.Status == c.Status) && (c.Type is null || a.Type == c.Type)
            && (c.RequestedBy is null || a.RequestedBy == c.RequestedBy)).OrderByDescending(a => a.Id).Take(500)));

    Task<bool> IApprovalDAO.UpdateAsync(ApprovalRequestDTO request, CancellationToken ct) => Run(() =>
    {
        if (!_approvals.TryGetValue(request.Id, out ApprovalRequestDTO? cur) || cur.Version != request.Version) return false;
        ApprovalRequestDTO a = C(request);
        a.Version++;
        _approvals[a.Id] = a;
        return true;
    });

    // ================================================================== IMembershipAuditDAO

    public Task AppendAsync(IReadOnlyList<MembershipAuditDTO> entries, CancellationToken ct) => Run(() => AppendAudit(entries));

    private void AppendAudit(IEnumerable<MembershipAuditDTO> entries)
    {
        foreach (MembershipAuditDTO e in entries)
        {
            MembershipAuditDTO x = C(e);
            x.Id = NextId();
            MembershipAuditChain.Seal(x, _audit.Count == 0 ? null : _audit[^1].Hash);
            _audit.Add(x);
        }
    }

    Task<PagedResultDTO<MembershipAuditDTO>> IMembershipAuditDAO.SearchAsync(AuditSearchCriteria c, CancellationToken ct) => Run(() =>
    {
        IEnumerable<MembershipAuditDTO> q = _audit;
        if (!string.IsNullOrWhiteSpace(c.Actor)) q = q.Where(a => a.Actor.Contains(c.Actor.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(c.Action)) q = q.Where(a => a.Action.StartsWith(c.Action.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(c.Target)) q = q.Where(a => a.Target == c.Target.Trim());
        if (c.From is DateTimeOffset f) q = q.Where(a => a.At >= f);
        if (c.To is DateTimeOffset t) q = q.Where(a => a.At <= t);
        return Page(q.OrderByDescending(a => a.Id), c.Page, c.PageSize);
    });

    // ================================================================== IOutboxDAO

    public Task<IReadOnlyList<OutboxMessageDTO>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        Run<IReadOnlyList<OutboxMessageDTO>>(() => CList(_outbox.Values.Where(o => o.SentAt is null && !o.DeadLettered && o.NextAttemptAt <= now).OrderBy(o => o.Id).Take(take)));

    public Task MarkSentAsync(long id, DateTimeOffset sentAt, CancellationToken ct) => Run(() =>
    {
        if (_outbox.TryGetValue(id, out OutboxMessageDTO? o)) o.SentAt = sentAt;
    });

    public Task MarkFailedAsync(long id, int attempts, DateTimeOffset nextAttemptAt, string error, bool deadLetter, CancellationToken ct) => Run(() =>
    {
        if (!_outbox.TryGetValue(id, out OutboxMessageDTO? o)) return;
        o.Attempts = attempts;
        o.NextAttemptAt = nextAttemptAt;
        o.LastError = error;
        o.DeadLettered = deadLetter;
    });

    public Task EnqueueAsync(IReadOnlyList<OutboxMessageDTO> messages, CancellationToken ct) => Run(() => AddOutbox(messages));

    private void AddOutbox(IEnumerable<OutboxMessageDTO> messages)
    {
        foreach (OutboxMessageDTO m in messages)
        {
            OutboxMessageDTO x = C(m);
            x.Id = NextId();
            _outbox[x.Id] = x;
        }
    }

    Task<(int Backlog, int DeadLetters)> IOutboxDAO.GetStatsAsync(CancellationToken ct) =>
        Run(() => (_outbox.Values.Count(o => o.SentAt is null && !o.DeadLettered), _outbox.Values.Count(o => o.DeadLettered)));

    // ================================================================== IIdempotencyDAO

    public Task<IdempotencyRecordDTO?> TryBeginAsync(IdempotencyRecordDTO record, CancellationToken ct) => Run(() =>
    {
        if (_idempotency.TryGetValue((record.SourceSystem, record.Key), out IdempotencyRecordDTO? cur) && cur.ExpiresAt > record.CreatedAt) return C(cur);
        _idempotency[(record.SourceSystem, record.Key)] = C(record);
        return (IdempotencyRecordDTO?)null;
    });

    public Task CompleteAsync(string sourceSystem, string key, int statusCode, string? responseJson, CancellationToken ct) => Run(() =>
    {
        if (_idempotency.TryGetValue((sourceSystem, key), out IdempotencyRecordDTO? r))
        {
            r.StatusCode = statusCode;
            r.ResponseJson = responseJson;
        }
    });

    public Task DeleteAsync(string sourceSystem, string key, CancellationToken ct) => Run(() => { _idempotency.Remove((sourceSystem, key)); });

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct) => Run(() =>
    {
        List<(string, string)> keys = [.. _idempotency.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key)];
        keys.ForEach(k => _idempotency.Remove(k));
        foreach (ExportJobDTO e in _exports.Values.Where(e => e.ExpiresAt <= now && e.Content is not null))
        {
            e.Content = null;
            e.Status = JobStatus.Expired;
        }

        return keys.Count;
    });

    // ================================================================== IMembershipSettingsDAO

    public Task<MembershipRulesDTO> GetRulesAsync(CancellationToken ct) => Run(() => _settings.Clone());

    public Task<bool> SaveRulesAsync(MembershipRulesDTO rules, CancellationToken ct) => Run(() =>
    {
        if (rules.Version != _settings.Version) return false;
        _settings = rules.Clone();
        _settings.Version++;
        return true;
    });

    public Task<IReadOnlyList<ConsentVersionDTO>> ListConsentVersionsAsync(CancellationToken ct) =>
        Run<IReadOnlyList<ConsentVersionDTO>>(() => CList(_consentVersions.Values.OrderByDescending(v => v.Id)));

    public Task<long> InsertConsentVersionAsync(ConsentVersionDTO version, CancellationToken ct) => Run(() =>
    {
        ConsentVersionDTO v = C(version);
        v.Id = NextId();
        _consentVersions[v.Id] = v;
        return v.Id;
    });

    public Task UpdateConsentVersionAsync(ConsentVersionDTO version, CancellationToken ct) => Run(() => { _consentVersions[version.Id] = C(version); });

    public Task<IReadOnlyList<IntegrationClientDTO>> ListIntegrationClientsAsync(CancellationToken ct) =>
        Run<IReadOnlyList<IntegrationClientDTO>>(() => CList(_integrations.Values.OrderBy(c => c.ClientId)));

    public Task UpsertIntegrationClientAsync(IntegrationClientDTO client, CancellationToken ct) => Run(() => { _integrations[client.ClientId] = C(client); });

    // ================================================================== IMemberPrivacyDAO

    public Task InsertOtpAsync(OtpChallengeEntity challenge, CancellationToken ct) => Run(() => { _otps[challenge.Id] = C(challenge); });

    public Task<OtpChallengeEntity?> GetOtpAsync(string id, CancellationToken ct) => Run(() => _otps.TryGetValue(id, out OtpChallengeEntity? o) ? C(o) : null);

    public Task UpdateOtpAsync(OtpChallengeEntity challenge, CancellationToken ct) => Run(() => { _otps[challenge.Id] = C(challenge); });

    public Task<int> CountOtpSinceAsync(string phoneHash, DateTimeOffset since, CancellationToken ct) => Run(() => _otps.Values.Count(o => o.PhoneHash == phoneHash && o.CreatedAt >= since));

    public Task<long> InsertPrivacyRequestAsync(PrivacyRequestDTO request, CancellationToken ct) => Run(() =>
    {
        PrivacyRequestDTO r = C(request);
        r.Id = NextId();
        _privacy[r.Id] = r;
        return r.Id;
    });

    public Task<PrivacyRequestDTO?> GetPrivacyRequestAsync(long id, CancellationToken ct) => Run(() => _privacy.TryGetValue(id, out PrivacyRequestDTO? r) ? C(r) : null);

    public Task<IReadOnlyList<PrivacyRequestDTO>> ListPrivacyRequestsAsync(long? memberId, PrivacyRequestStatus? status, CancellationToken ct) =>
        Run<IReadOnlyList<PrivacyRequestDTO>>(() => CList(_privacy.Values.Where(r => (memberId is null || r.MemberId == memberId) && (status is null || r.Status == status)).OrderByDescending(r => r.Id)));

    public Task UpdatePrivacyRequestAsync(PrivacyRequestDTO request, CancellationToken ct) => Run(() => { _privacy[request.Id] = C(request); });

    // ================================================================== IMembershipJobDAO

    public Task<bool> TryAcquireLeaseAsync(string job, string holder, DateTimeOffset now, TimeSpan duration, CancellationToken ct) => Run(() =>
    {
        if (_leases.TryGetValue(job, out (string Holder, DateTimeOffset Until) l) && l.Until > now && l.Holder != holder) return false;
        _leases[job] = (holder, now + duration);
        return true;
    });

    public Task<long> InsertExportAsync(ExportJobDTO job, CancellationToken ct) => Run(() =>
    {
        ExportJobDTO j = CopyExport(job, true);
        j.Id = NextId();
        _exports[j.Id] = j;
        return j.Id;
    });

    public Task<ExportJobDTO?> GetExportAsync(long id, bool withContent, CancellationToken ct) => Run(() => _exports.TryGetValue(id, out ExportJobDTO? j) ? CopyExport(j, withContent) : null);

    public Task<IReadOnlyList<ExportJobDTO>> ListExportsAsync(string? requestedBy, JobStatus? status, CancellationToken ct) =>
        Run<IReadOnlyList<ExportJobDTO>>(() => [.. _exports.Values.Where(j => (requestedBy is null || j.RequestedBy == requestedBy) && (status is null || j.Status == status))
            .OrderByDescending(j => j.Id).Select(j => CopyExport(j, false))]);

    public Task UpdateExportAsync(ExportJobDTO job, CancellationToken ct) => Run(() =>
    {
        byte[]? keep = _exports.TryGetValue(job.Id, out ExportJobDTO? cur) ? cur.Content : null;
        ExportJobDTO j = CopyExport(job, true);
        j.Content ??= keep;
        _exports[j.Id] = j;
    });

    private static ExportJobDTO CopyExport(ExportJobDTO j, bool withContent)
    {
        ExportJobDTO x = C(j);
        x.Content = withContent ? j.Content : null;
        return x;
    }

    public Task<long> InsertMessageJobAsync(MessageJobDTO job, CancellationToken ct) => Run(() =>
    {
        MessageJobDTO j = C(job);
        j.Id = NextId();
        _messageJobs[j.Id] = j;
        return j.Id;
    });

    public Task<IReadOnlyList<MessageJobDTO>> ListMessageJobsAsync(JobStatus? status, CancellationToken ct) =>
        Run<IReadOnlyList<MessageJobDTO>>(() => CList(_messageJobs.Values.Where(j => status is null || j.Status == status).OrderByDescending(j => j.Id)));

    public Task UpdateMessageJobAsync(MessageJobDTO job, CancellationToken ct) => Run(() => { _messageJobs[job.Id] = C(job); });

    // ================================================================== IMembershipUnitOfWork

    public Task CommitAsync(MembershipChangeSet cs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cs);
        lock (_sync)
        {
            Validate(cs);
            Apply(cs);
        }

        return Task.CompletedTask;
    }

    /// <summary>ตรวจทุกเงื่อนไขก่อนเขียน (ล้ม = ไม่เปลี่ยนอะไรเลย)</summary>
    private void Validate(MembershipChangeSet cs)
    {
        foreach (AccountChange a in cs.Accounts)
        {
            if (Account(a.NewState.MemberId).Version != a.ExpectedVersion) throw new MembershipConcurrencyException("account");
        }

        foreach (LotChange l in cs.LotChanges)
        {
            if (!_lots.TryGetValue(l.LotId, out PointLotDTO? lot) || lot.Remaining != l.ExpectedRemaining) throw new MembershipConcurrencyException("lot");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (PointLedgerDTO l in cs.Ledger)
        {
            if (_ledgerKeys.Contains(l.IdempotencyKey) || !keys.Add(l.IdempotencyKey)) throw new MembershipConcurrencyException("ledger key");
        }

        foreach (CouponChange c in cs.Coupons)
        {
            bool exists = _coupons.TryGetValue(c.NewState.Code, out CouponDTO? cur);
            if (c.ExpectedVersion == 0 ? exists : !exists || cur!.Version != c.ExpectedVersion) throw new MembershipConcurrencyException("coupon");
        }

        foreach (IGrouping<(long, string), CampaignCounterDelta> g in cs.Counters.GroupBy(d => (d.CampaignId, d.Scope)))
        {
            CampaignUsageDTO u = _counters.TryGetValue(g.Key, out CampaignUsageDTO? x) ? x : new CampaignUsageDTO();
            int uses = g.Sum(d => d.UsesDelta + d.ReservedDelta);
            decimal budget = g.Sum(d => d.BudgetDelta + d.BudgetReservedDelta);
            int? limit = g.Select(d => d.Limit).FirstOrDefault(l => l is not null);
            decimal? budgetLimit = g.Select(d => d.BudgetLimit).FirstOrDefault(l => l is not null);
            if (uses > 0 && limit is int lim && u.Uses + u.Reserved + uses > lim) throw new MembershipLimitException("limit_reached");
            if (budget > 0 && budgetLimit is decimal bl && u.BudgetUsed + u.BudgetReserved + budget > bl) throw new MembershipLimitException("budget_exhausted");
        }

        if (cs.NewReservation is not null && _reservations.ContainsKey(cs.NewReservation.Id)) throw new MembershipConcurrencyException("reservation");
        if (cs.ReservationUpdate is ReservationEntity ru && (!_reservations.TryGetValue(ru.Id, out ReservationEntity? r) || r.Version != ru.Version)) throw new MembershipConcurrencyException("reservation");
        if (cs.NewTransaction is SalesTransactionEntity nt && _transactions.Values.Any(t => t.SourceSystem == nt.SourceSystem && t.TransactionId == nt.TransactionId)) throw new MembershipConcurrencyException("transaction");
        if (cs.TransactionUpdate is SalesTransactionEntity tu && (!_transactions.TryGetValue(tu.Id, out SalesTransactionEntity? t0) || t0.Version != tu.Version)) throw new MembershipConcurrencyException("transaction");
        foreach (MemberEntity m in cs.MemberUpdates)
        {
            if (!_members.TryGetValue(m.Id, out MemberEntity? cur) || cur.Version != m.Version) throw new MembershipConcurrencyException("member");
        }
    }

    private void Apply(MembershipChangeSet cs)
    {
        foreach (AccountChange a in cs.Accounts)
        {
            PointAccountDTO x = a.NewState.Clone();
            x.Version = a.ExpectedVersion + 1;
            _accounts[x.MemberId] = x;
        }

        foreach (LotChange l in cs.LotChanges)
        {
            _lots[l.LotId].Remaining = l.NewRemaining;
        }

        foreach (PointLotDTO l in cs.NewLots)
        {
            PointLotDTO x = C(l);
            x.Id = NextId();
            _lots[x.Id] = x;
        }

        foreach (PointLedgerDTO l in cs.Ledger)
        {
            PointLedgerDTO x = C(l);
            x.Id = NextId();
            _ledger.Add(x);
            _ledgerKeys.Add(x.IdempotencyKey);
        }

        _allocations.AddRange(CList(cs.Allocations));
        foreach (CouponChange c in cs.Coupons)
        {
            CouponDTO x = C(c.NewState);
            x.Version = c.ExpectedVersion + 1;
            if (x.Id == 0) x.Id = _coupons.TryGetValue(x.Code, out CouponDTO? cur) ? cur.Id : NextId();
            _coupons[x.Code] = x;
        }

        foreach (CouponEventDTO e in cs.CouponEvents)
        {
            CouponEventDTO x = C(e);
            x.Id = NextId();
            _couponEvents.Add(x);
        }

        foreach (CampaignCounterDelta d in cs.Counters)
        {
            if (!_counters.TryGetValue((d.CampaignId, d.Scope), out CampaignUsageDTO? u)) _counters[(d.CampaignId, d.Scope)] = u = new CampaignUsageDTO();
            u.Uses = Math.Max(u.Uses + d.UsesDelta, 0);
            u.Reserved = Math.Max(u.Reserved + d.ReservedDelta, 0);
            u.BudgetUsed = Math.Max(u.BudgetUsed + d.BudgetDelta, 0);
            u.BudgetReserved = Math.Max(u.BudgetReserved + d.BudgetReservedDelta, 0);
        }

        if (cs.NewReservation is not null) _reservations[cs.NewReservation.Id] = cs.NewReservation.Clone();
        if (cs.ReservationUpdate is not null)
        {
            ReservationEntity r = cs.ReservationUpdate.Clone();
            r.Version++;
            _reservations[r.Id] = r;
        }

        if (cs.NewTransaction is not null)
        {
            SalesTransactionEntity t = cs.NewTransaction.Clone();
            t.Id = NextId();
            t.Version = 1;
            cs.NewTransaction.Id = t.Id;
            _transactions[t.Id] = t;
        }

        if (cs.TransactionUpdate is not null)
        {
            SalesTransactionEntity t = cs.TransactionUpdate.Clone();
            t.Version++;
            _transactions[t.Id] = t;
        }

        foreach (SalesReturnDTO r in cs.Returns)
        {
            SalesReturnDTO x = C(r);
            x.Id = NextId();
            _returns.Add(x);
        }

        foreach (MemberEntity m in cs.MemberUpdates)
        {
            MemberEntity x = C(m);
            x.Version++;
            _members[x.Id] = x;
        }

        foreach (TierHistoryDTO h in cs.TierHistory)
        {
            TierHistoryDTO x = C(h);
            x.Id = NextId();
            _tierHistory.Add(x);
        }

        if (cs.AdjustmentUpdate is not null) _adjustments[cs.AdjustmentUpdate.Id] = C(cs.AdjustmentUpdate);
        AddOutbox(cs.Outbox);
        AppendAudit(cs.Audit);
    }
}
