using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Data.Membership;

// แหล่งข้อมูลของระบบวิเคราะห์ (อ่านอย่างเดียว)
public sealed partial class InMemoryMembershipStore : IAnaSourceDAO
{
    public Task<IReadOnlyList<SalesTransactionEntity>> ListTransactionsAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct) =>
        Run<IReadOnlyList<SalesTransactionEntity>>(() => [.. _transactions.Values.Where(t => t.OccurredAt >= from && t.OccurredAt < to && t.Id > afterId).OrderBy(t => t.Id).Take(take).Select(t => t.Clone())]);

    public Task<IReadOnlyList<PointLedgerDTO>> ListLedgerAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct) =>
        Run<IReadOnlyList<PointLedgerDTO>>(() => CList(_ledger.Where(l => l.CreatedAt >= from && l.CreatedAt < to && l.Id > afterId).OrderBy(l => l.Id).Take(take)));

    public Task<IReadOnlyList<CouponEventDTO>> ListCouponEventsAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct) =>
        Run<IReadOnlyList<CouponEventDTO>>(() => CList(_couponEvents.Where(e => e.At >= from && e.At < to && e.Id > afterId).OrderBy(e => e.Id).Take(take)));

    public Task<IReadOnlyList<CouponDTO>> ListCouponsCreatedAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct) =>
        Run<IReadOnlyList<CouponDTO>>(() => CList(_coupons.Values.Where(c => c.CreatedAt >= from && c.CreatedAt < to && c.Id > afterId).OrderBy(c => c.Id).Take(take)));

    public Task<IReadOnlyList<CouponDTO>> ListCouponsExpiredAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct) =>
        Run<IReadOnlyList<CouponDTO>>(() => CList(_coupons.Values.Where(c => c.Status == CouponStatus.Expired && c.ExpiresAt >= from && c.ExpiresAt < to && c.Id > afterId).OrderBy(c => c.Id).Take(take)));

    public Task<IReadOnlyDictionary<long, int>> CountUsableCouponsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Run<IReadOnlyDictionary<long, int>>(() => _coupons.Values
            .Where(c => c.CreatedAt < to && c.ExpiresAt >= from && !(c.Status == CouponStatus.Cancelled && !_couponEvents.Any(e => e.Code == c.Code && e.ToStatus == CouponStatus.Cancelled && e.At >= from)))
            .GroupBy(c => c.CampaignId).ToDictionary(g => g.Key, g => g.Count()));

    public Task<IReadOnlyDictionary<long, int>> CountAvailableCouponsByMemberAsync(DateTimeOffset now, CancellationToken ct) =>
        Run<IReadOnlyDictionary<long, int>>(() => _coupons.Values.Where(c => c.MemberId is not null && c.Status == CouponStatus.Available && c.ExpiresAt > now)
            .GroupBy(c => c.MemberId!.Value).ToDictionary(g => g.Key, g => g.Count()));

    public Task<IReadOnlyList<MemberEntity>> ListMembersAsync(long afterId, int take, CancellationToken ct) =>
        Run<IReadOnlyList<MemberEntity>>(() => [.. _members.Values.Where(m => m.Id > afterId).OrderBy(m => m.Id).Take(take).Select(m => WithPoints(C(m)))]);

    public Task<IReadOnlyDictionary<long, decimal>> SumQualifyingSpendAsync(DateTimeOffset since, CancellationToken ct) =>
        Run<IReadOnlyDictionary<long, decimal>>(() => _transactions.Values.Where(t => t.Status != TransactionStatus.Cancelled && t.OccurredAt >= since)
            .GroupBy(t => t.MemberId).ToDictionary(g => g.Key, g => g.Sum(t => t.Eligible - t.ReturnedEligible)));

    public Task<IReadOnlyList<MemberPurchaseStatsDTO>> ListPurchaseStatsAsync(CancellationToken ct) =>
        Run<IReadOnlyList<MemberPurchaseStatsDTO>>(() => [.. _transactions.Values.Where(t => !t.IsTest && t.Status is TransactionStatus.Completed or TransactionStatus.PartiallyReturned)
            .GroupBy(t => t.MemberId).Select(g => new MemberPurchaseStatsDTO { MemberId = g.Key, FirstAt = g.Min(t => t.OccurredAt), LastAt = g.Max(t => t.OccurredAt), Bills = g.Count() })]);
}
