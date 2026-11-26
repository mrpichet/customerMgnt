using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Data.Infrastructure;
using Dapper;

namespace CustomerMgnt.Data.Membership;

// แหล่งข้อมูลของระบบวิเคราะห์ (อ่านอย่างเดียว แบ่งหน้าตาม id)
public sealed partial class MembershipADO : IAnaSourceDAO
{
    private sealed class CountRow
    {
        public long Key { get; set; }
        public int Cnt { get; set; }
    }

    private sealed class SumRow
    {
        public long Key { get; set; }
        public decimal Total { get; set; }
    }

    public Task<IReadOnlyList<SalesTransactionEntity>> ListTransactionsAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<SalesTransactionEntity>>("ana.source.transactions", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT id, version, data FROM mbr_transaction WHERE occurred_at >= @from AND occurred_at < @to AND id > @afterId ORDER BY id {Dialect.Take("@take")}",
            new { from, to, afterId, take }, ct)).ConfigureAwait(false)).Select(MapTxn)], ct);

    public Task<IReadOnlyList<PointLedgerDTO>> ListLedgerAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<PointLedgerDTO>>("ana.source.ledger", async c => [.. (await c.QueryAsync<LedgerRow>(Cmd(
            $"""
            SELECT id, member_id AS MemberId, type, points, balance_after AS BalanceAfter, source_system AS SourceSystem, transaction_id AS TransactionId, reference, reason, actor,
                   request_id AS RequestId, idempotency_key AS IdempotencyKey, created_at AS CreatedAt
            FROM mbr_point_ledger WHERE created_at >= @from AND created_at < @to AND id > @afterId ORDER BY id {Dialect.Take("@take")}
            """, new { from, to, afterId, take }, ct)).ConfigureAwait(false)).Select(r => r.ToDto())], ct);

    public Task<IReadOnlyList<CouponEventDTO>> ListCouponEventsAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CouponEventDTO>>("ana.source.coupon_events", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT id, data FROM mbr_coupon_event WHERE at >= @from AND at < @to AND id > @afterId ORDER BY id {Dialect.Take("@take")}",
            new { from, to, afterId, take }, ct)).ConfigureAwait(false)).Select(r => R<CouponEventDTO>(r, (x, y) => x.Id = y.Id))], ct);

    public Task<IReadOnlyList<CouponDTO>> ListCouponsCreatedAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CouponDTO>>("ana.source.coupons_created", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT {CouponSelect} FROM mbr_coupon WHERE created_at >= @from AND created_at < @to AND id > @afterId ORDER BY id {Dialect.Take("@take")}",
            new { from, to, afterId, take }, ct)).ConfigureAwait(false)).Select(MapCoupon)], ct);

    public Task<IReadOnlyList<CouponDTO>> ListCouponsExpiredAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CouponDTO>>("ana.source.coupons_expired", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT {CouponSelect} FROM mbr_coupon WHERE status = 'Expired' AND expires_at >= @from AND expires_at < @to AND id > @afterId ORDER BY id {Dialect.Take("@take")}",
            new { from, to, afterId, take }, ct)).ConfigureAwait(false)).Select(MapCoupon)], ct);

    public Task<IReadOnlyDictionary<long, int>> CountUsableCouponsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        RunAsync<IReadOnlyDictionary<long, int>>("ana.source.coupons_usable", async c => (await c.QueryAsync<CountRow>(Cmd("""
            SELECT campaign_id AS "Key", COUNT(*) AS Cnt FROM mbr_coupon cp
            WHERE created_at < @to AND expires_at >= @from
              AND NOT (status = 'Cancelled' AND NOT EXISTS (SELECT 1 FROM mbr_coupon_event e WHERE e.code = cp.code AND e.at >= @from))
            GROUP BY campaign_id
            """, new { from, to }, ct)).ConfigureAwait(false)).ToDictionary(r => r.Key, r => r.Cnt), ct);

    public Task<IReadOnlyDictionary<long, int>> CountAvailableCouponsByMemberAsync(DateTimeOffset now, CancellationToken ct) =>
        RunAsync<IReadOnlyDictionary<long, int>>("ana.source.coupons_available", async c => (await c.QueryAsync<CountRow>(Cmd("""
            SELECT member_id AS "Key", COUNT(*) AS Cnt FROM mbr_coupon WHERE member_id IS NOT NULL AND status = 'Available' AND expires_at > @now GROUP BY member_id
            """, new { now }, ct)).ConfigureAwait(false)).ToDictionary(r => r.Key, r => r.Cnt), ct);

    public Task<IReadOnlyList<MemberEntity>> ListMembersAsync(long afterId, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<MemberEntity>>("ana.source.members", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT {MemberSelect} FROM {MemberFrom} WHERE m.id > @afterId ORDER BY m.id {Dialect.Take("@take")}", new { afterId, take }, ct)).ConfigureAwait(false)).Select(MapMember)], ct);

    public Task<IReadOnlyDictionary<long, decimal>> SumQualifyingSpendAsync(DateTimeOffset since, CancellationToken ct) =>
        RunAsync<IReadOnlyDictionary<long, decimal>>("ana.source.spend", async c => (await c.QueryAsync<SumRow>(Cmd("""
            SELECT member_id AS "Key", SUM(eligible - returned_eligible) AS Total FROM mbr_transaction WHERE status <> 'Cancelled' AND occurred_at >= @since GROUP BY member_id
            """, new { since }, ct)).ConfigureAwait(false)).ToDictionary(r => r.Key, r => r.Total), ct);

    public Task<IReadOnlyList<MemberPurchaseStatsDTO>> ListPurchaseStatsAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<MemberPurchaseStatsDTO>>("ana.source.purchase_stats", async c => [.. await c.QueryAsync<MemberPurchaseStatsDTO>(Cmd("""
            SELECT member_id AS MemberId, MIN(occurred_at) AS FirstAt, MAX(occurred_at) AS LastAt, COUNT(*) AS Bills FROM mbr_transaction
            WHERE is_test = @f AND status IN ('Completed', 'PartiallyReturned') GROUP BY member_id
            """, new { f = false }, ct)).ConfigureAwait(false)], ct);
}
