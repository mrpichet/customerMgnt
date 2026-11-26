using System.Data.Common;
using Common.Core.Domain;
using Common.Data.Infrastructure;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;
using CustomerMgnt.Data.Infrastructure;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Membership;

/// <summary>
/// Data access ของระบบสมาชิก (PostgreSQL / SQL Server + Dapper) — ตาราง <c>mbr_*</c>
/// คอลัมน์ที่ใช้ค้นหา/ตรวจ version แยกออกมา ข้อมูลเต็มเก็บใน <c>data</c> (JSON); คะแนน lot/ledger/ยอด เก็บเป็นคอลัมน์ปกติเพื่อรวมยอดในฐานข้อมูล
/// </summary>
public sealed partial class MembershipADO : AdoBase, IMemberDAO, ITierDAO, IPointDAO, ICampaignDAO, ICouponDAO, ISegmentDAO, ICheckoutDAO, IApprovalDAO,
    IMembershipAuditDAO, IOutboxDAO, IIdempotencyDAO, IMembershipSettingsDAO, IMemberPrivacyDAO, IMembershipJobDAO
{
    /// <summary>สร้าง</summary>
    public MembershipADO(IDbConnectionFactory db, ILogger<MembershipADO> logger) : base(db, logger) { }

    /// <summary>แถว id + version + JSON</summary>
    private sealed class DataRow
    {
        public long Id { get; set; }
        public long Version { get; set; }
        public string Data { get; set; } = "{}";
        public int Points { get; set; }
    }

    private static string J<T>(T value) => MembershipJson.Write(value);

    private static T R<T>(DataRow row, Action<T, DataRow>? patch = null) where T : new()
    {
        T v = MembershipJson.Read<T>(row.Data);
        patch?.Invoke(v, row);
        return v;
    }

    private string Paging => Dialect.Page("@offset", "@take");

    private static object PageArgs(int page, int pageSize) => new { offset = (Math.Max(page, 1) - 1) * pageSize, take = pageSize };

    private async Task<PagedResultDTO<T>> PagedAsync<T>(string op, string from, string where, string order, DynamicParameters p, int page, int pageSize, string select,
        Func<DataRow, T> map, CancellationToken ct) =>
        await RunAsync(op, async c =>
        {
            page = Math.Max(page, 1);
            p.Add("offset", (page - 1) * pageSize);
            p.Add("take", pageSize);
            long total = await c.ExecuteScalarAsync<long>(Cmd($"SELECT COUNT(*) FROM {from} WHERE {where}", p, ct)).ConfigureAwait(false);
            IEnumerable<DataRow> rows = await c.QueryAsync<DataRow>(Cmd($"SELECT {select} FROM {from} WHERE {where} ORDER BY {order} {Paging}", p, ct)).ConfigureAwait(false);
            return new PagedResultDTO<T>([.. rows.Select(map)], total, page, pageSize);
        }, ct).ConfigureAwait(false);

    // ================================================================== IMemberDAO

    private const string MemberSelect = "m.id, m.version, m.data, COALESCE(a.lot_balance - a.reserved - a.debt, 0) AS points";
    private const string MemberFrom = "mbr_member m LEFT JOIN mbr_point_account a ON a.member_id = m.id";

    private static MemberEntity MapMember(DataRow r) => R<MemberEntity>(r, (m, x) => { m.Id = x.Id; m.Version = x.Version; m.PointsAvailable = Math.Max(x.Points, 0); });

    private Task<MemberEntity?> MemberWhereAsync(string op, string where, object args, CancellationToken ct) =>
        RunAsync(op, async c => (await c.QueryAsync<DataRow>(Cmd($"SELECT {MemberSelect} FROM {MemberFrom} WHERE {where} ORDER BY m.id {Dialect.Take("1")}", args, ct)).ConfigureAwait(false))
            .Select(MapMember).FirstOrDefault(), ct);

    public Task<MemberEntity?> GetAsync(long id, CancellationToken ct) => MemberWhereAsync("member.get", "m.id = @id", new { id }, ct);

    public Task<MemberEntity?> GetByNoAsync(string memberNo, CancellationToken ct) => MemberWhereAsync("member.by_no", "m.member_no = @memberNo", new { memberNo = memberNo.ToUpperInvariant() }, ct);

    public Task<MemberEntity?> GetBySubjectAsync(string subject, CancellationToken ct) => MemberWhereAsync("member.by_subject", "m.auth_subject = @subject", new { subject }, ct);

    private Task<IReadOnlyList<MemberEntity>> MembersAsync(string op, string where, object args, CancellationToken ct) =>
        RunAsync<IReadOnlyList<MemberEntity>>(op, async c => [.. (await c.QueryAsync<DataRow>(Cmd($"SELECT {MemberSelect} FROM {MemberFrom} WHERE {where}", args, ct)).ConfigureAwait(false)).Select(MapMember)], ct);

    public Task<IReadOnlyList<MemberEntity>> FindByPhoneHashAsync(string phoneHash, CancellationToken ct) => MembersAsync("member.by_phone", "m.phone_hash = @phoneHash ORDER BY m.id", new { phoneHash }, ct);

    public Task<IReadOnlyList<MemberEntity>> FindByEmailHashAsync(string emailHash, CancellationToken ct) => MembersAsync("member.by_email", "m.email_hash = @emailHash ORDER BY m.id", new { emailHash }, ct);

    public Task<PagedResultDTO<MemberEntity>> SearchAsync(MemberSearchCriteria c, CancellationToken ct)
    {
        var p = new DynamicParameters();
        var w = new List<string> { "1 = 1" };
        if (!string.IsNullOrWhiteSpace(c.Text))
        {
            w.Add("(m.member_no LIKE @text OR m.first_name LIKE @text OR m.last_name LIKE @text)");
            p.Add("text", "%" + c.Text.Trim().Replace("%", "", StringComparison.Ordinal) + "%");
        }

        if (!string.IsNullOrWhiteSpace(c.MemberNo)) { w.Add("m.member_no = @memberNo"); p.Add("memberNo", c.MemberNo.Trim().ToUpperInvariant()); }
        if (c.PhoneHash is not null) { w.Add("m.phone_hash = @phoneHash"); p.Add("phoneHash", c.PhoneHash); }
        if (c.EmailHash is not null) { w.Add("m.email_hash = @emailHash"); p.Add("emailHash", c.EmailHash); }
        if (c.Status is MemberStatus s) { w.Add("m.status = @status"); p.Add("status", s.ToString()); }
        if (!string.IsNullOrWhiteSpace(c.TierCode)) { w.Add("m.tier_code = @tier"); p.Add("tier", c.TierCode); }
        if (c.SegmentId is long sid) { w.Add("m.id IN (SELECT member_id FROM mbr_segment_member WHERE segment_id = @sid)"); p.Add("sid", sid); }
        if (!string.IsNullOrWhiteSpace(c.CustomerCode)) { w.Add("m.customer_code = @customer"); p.Add("customer", c.CustomerCode.ToUpperInvariant()); }
        return PagedAsync("member.search", MemberFrom, string.Join(" AND ", w), "m.id DESC", p, c.Page, c.PageSize, MemberSelect, MapMember, ct);
    }

    private static object MemberArgs(MemberEntity m) => new
    {
        m.Id, MemberNo = m.MemberNo, m.AuthSubject, m.PhoneHash, m.EmailHash, Status = m.Status.ToString(), m.TierCode, m.CustomerCode, m.TierReviewAt,
        m.FirstName, m.LastName, m.CreatedAt, m.Version, Data = J(m),
    };

    public Task<MemberEntity> InsertAsync(MemberEntity member, MemberConsentDTO? consent, IReadOnlyList<OutboxMessageDTO> outbox, CancellationToken ct) =>
        RunAsync("member.insert", async c =>
        {
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            MemberEntity m = MembershipJson.Read<MemberEntity>(J(member));
            m.Version = Math.Max(m.Version, 1);
            m.MemberNo = "T" + Guid.NewGuid().ToString("N")[..19];
            m.Id = await c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_member",
                "member_no, auth_subject, phone_hash, email_hash, status, tier_code, customer_code, tier_review_at, first_name, last_name, created_at, version, data",
                "@MemberNo, @AuthSubject, @PhoneHash, @EmailHash, @Status, @TierCode, @CustomerCode, @TierReviewAt, @FirstName, @LastName, @CreatedAt, @Version, @Data"), MemberArgs(m), ct, tx)).ConfigureAwait(false);
            m.MemberNo = $"M{m.CreatedAt.Year % 100:00}{m.Id:00000}";
            await c.ExecuteAsync(Cmd("UPDATE mbr_member SET member_no = @MemberNo, data = @Data WHERE id = @Id", MemberArgs(m), ct, tx)).ConfigureAwait(false);
            if (consent is not null)
            {
                consent.MemberId = m.Id;
                await InsertConsentAsync(c, tx, consent, ct).ConfigureAwait(false);
            }

            await InsertOutboxAsync(c, tx, outbox.Select(o =>
            {
                OutboxMessageDTO x = MembershipJson.Read<OutboxMessageDTO>(J(o));
                x.PayloadJson = MembershipJson.WithMember(x.PayloadJson, m.Id, m.MemberNo);
                return x;
            }), ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return m;
        }, ct);

    public Task<bool> UpdateAsync(MemberEntity member, CancellationToken ct) =>
        RunAsync("member.update", async c => await UpdateMemberAsync(c, null, member, ct).ConfigureAwait(false), ct);

    private async Task<bool> UpdateMemberAsync(DbConnection c, DbTransaction? tx, MemberEntity m, CancellationToken ct)
    {
        MemberEntity next = MembershipJson.Read<MemberEntity>(J(m));
        next.Version = m.Version + 1;
        return await c.ExecuteAsync(Cmd("""
            UPDATE mbr_member SET auth_subject = @AuthSubject, phone_hash = @PhoneHash, email_hash = @EmailHash, status = @Status, tier_code = @TierCode,
                customer_code = @CustomerCode, tier_review_at = @TierReviewAt, first_name = @FirstName, last_name = @LastName, version = version + 1, data = @Data
            WHERE id = @Id AND version = @Expected
            """, new
        {
            next.Id, next.AuthSubject, next.PhoneHash, next.EmailHash, Status = next.Status.ToString(), next.TierCode, next.CustomerCode, next.TierReviewAt,
            next.FirstName, next.LastName, Data = J(next), Expected = m.Version,
        }, ct, tx)).ConfigureAwait(false) == 1;
    }

    public Task<IReadOnlyList<MemberEntity>> ListTierReviewDueAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        MembersAsync("member.review_due", $"m.status <> 'Closed' AND m.tier_code IS NOT NULL AND m.tier_review_at <= @now ORDER BY m.tier_review_at {Dialect.Take("@take")}", new { now, take }, ct);

    public Task<IReadOnlyList<MemberEntity>> ListWithoutCustomerAsync(int take, CancellationToken ct) =>
        MembersAsync("member.no_customer", $"m.status = 'Active' AND m.customer_code IS NULL ORDER BY m.id {Dialect.Take("@take")}", new { take }, ct);

    public Task<IReadOnlyList<MemberConsentDTO>> ListConsentsAsync(long memberId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<MemberConsentDTO>>("member.consents", async c => [.. (await c.QueryAsync<DataRow>(Cmd("SELECT id, data FROM mbr_member_consent WHERE member_id = @memberId ORDER BY at", new { memberId }, ct)).ConfigureAwait(false))
            .Select(r => R<MemberConsentDTO>(r, (x, y) => x.Id = y.Id))], ct);

    public Task AddConsentAsync(MemberConsentDTO consent, CancellationToken ct) =>
        RunAsync("member.add_consent", async c => { await InsertConsentAsync(c, null, consent, ct).ConfigureAwait(false); return true; }, ct);

    private Task InsertConsentAsync(DbConnection c, DbTransaction? tx, MemberConsentDTO consent, CancellationToken ct) =>
        c.ExecuteAsync(Cmd("INSERT INTO mbr_member_consent (member_id, at, data) VALUES (@MemberId, @At, @Data)", new { consent.MemberId, consent.At, Data = J(consent) }, ct, tx));

    public Task<IReadOnlyList<TierHistoryDTO>> ListTierHistoryAsync(long memberId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<TierHistoryDTO>>("member.tier_history", async c => [.. (await c.QueryAsync<DataRow>(Cmd("SELECT id, data FROM mbr_tier_history WHERE member_id = @memberId ORDER BY changed_at DESC", new { memberId }, ct)).ConfigureAwait(false))
            .Select(r => R<TierHistoryDTO>(r, (x, y) => x.Id = y.Id))], ct);

    /// <summary>แถวข้อเท็จจริงสำหรับ segment (query คงที่ + parameter)</summary>
    private sealed class FactsRow
    {
        public long Id { get; set; }
        public string Data { get; set; } = "{}";
        public int Points { get; set; }
        public decimal Spend { get; set; }
        public int Freq { get; set; }
        public DateTimeOffset? LastAt { get; set; }
    }

    public Task<IReadOnlyList<MemberFactsDTO>> ListFactsAsync(long afterId, int take, DateTimeOffset now, CancellationToken ct) =>
        RunAsync<IReadOnlyList<MemberFactsDTO>>("member.facts", async c =>
        {
            string sql = $"""
                SELECT m.id, m.data, COALESCE(a.lot_balance - a.reserved - a.debt, 0) AS points,
                    COALESCE((SELECT SUM(t.eligible - t.returned_eligible) FROM mbr_transaction t WHERE t.member_id = m.id AND t.status <> 'Cancelled' AND t.occurred_at >= @since), 0) AS spend,
                    (SELECT COUNT(*) FROM mbr_transaction t WHERE t.member_id = m.id AND t.status <> 'Cancelled' AND t.occurred_at >= @since) AS freq,
                    (SELECT MAX(t.occurred_at) FROM mbr_transaction t WHERE t.member_id = m.id AND t.status <> 'Cancelled') AS last_at
                FROM mbr_member m LEFT JOIN mbr_point_account a ON a.member_id = m.id
                WHERE m.id > @afterId ORDER BY m.id {Dialect.Take("@take")}
                """;
            IEnumerable<FactsRow> rows = await c.QueryAsync<FactsRow>(Cmd(sql, new { afterId, take, since = now.AddMonths(-12) }, ct)).ConfigureAwait(false);
            return [.. rows.Select(r =>
            {
                MemberEntity m = MembershipJson.Read<MemberEntity>(r.Data);
                return new MemberFactsDTO
                {
                    MemberId = r.Id, TierCode = m.TierCode, Status = m.Status, AgeYears = m.BirthYear is int y ? now.Year - y : null, Gender = m.Gender, Province = m.Province,
                    PointsAvailable = Math.Max(r.Points, 0), Spend12m = r.Spend, Frequency12m = r.Freq, RecencyDays = r.LastAt is null ? null : (int)(now - r.LastAt.Value).TotalDays,
                    MarketingConsent = m.MarketingConsent,
                };
            })];
        }, ct);

    public Task<MemberStatsDTO> GetStatsAsync(DateTimeOffset since, CancellationToken ct) =>
        RunAsync("member.stats", async c =>
        {
            var s = new MemberStatsDTO
            {
                Total = await c.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM mbr_member WHERE status <> 'Closed'", null, ct)).ConfigureAwait(false),
                Active = await c.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM mbr_member WHERE status = 'Active'", null, ct)).ConfigureAwait(false),
                NewSince = await c.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM mbr_member WHERE created_at >= @since", new { since }, ct)).ConfigureAwait(false),
            };
            foreach ((string? tier, int n) in await c.QueryAsync<(string?, int)>(Cmd("SELECT tier_code, COUNT(*) FROM mbr_member WHERE status = 'Active' GROUP BY tier_code", null, ct)).ConfigureAwait(false))
            {
                s.ByTier[tier ?? "-"] = n;
            }

            return s;
        }, ct);

    // ================================================================== ITierDAO

    Task<IReadOnlyList<TierDTO>> ITierDAO.ListAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<TierDTO>>("tier.list", async c => [.. (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_tier ORDER BY rank", null, ct)).ConfigureAwait(false)).Select(MapTier)], ct);

    private static TierDTO MapTier(DataRow r) => R<TierDTO>(r, (t, x) => { t.Id = x.Id; t.Version = x.Version; });

    Task<TierDTO?> ITierDAO.GetAsync(long id, CancellationToken ct) =>
        RunAsync("tier.get", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_tier WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(MapTier).FirstOrDefault(), ct);

    Task<long> ITierDAO.InsertAsync(TierDTO tier, CancellationToken ct) =>
        RunAsync("tier.insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_tier", "code, rank, version, data", "@Code, @Rank, 1, @Data"), new { tier.Code, tier.Rank, Data = J(tier) }, ct)), ct);

    Task<bool> ITierDAO.UpdateAsync(TierDTO tier, CancellationToken ct) =>
        RunAsync("tier.update", async c => await c.ExecuteAsync(Cmd("UPDATE mbr_tier SET code = @Code, rank = @Rank, data = @Data, version = version + 1 WHERE id = @Id AND version = @Version",
            new { tier.Id, tier.Code, tier.Rank, tier.Version, Data = J(tier) }, ct)).ConfigureAwait(false) == 1, ct);

    // ================================================================== IPointDAO

    private const string LotColumns = "id, member_id, points, remaining, earned_at, expires_at, source_key";
    private const string LedgerColumns = "id, member_id, type, points, balance_after, source_system, transaction_id, reference, reason, actor, request_id, idempotency_key, created_at";

    public Task<PointAccountDTO> GetAccountAsync(long memberId, CancellationToken ct) =>
        RunAsync("points.account", async c => await c.QuerySingleOrDefaultAsync<PointAccountDTO>(Cmd("SELECT member_id, lot_balance, reserved, debt, lifetime_earned, version FROM mbr_point_account WHERE member_id = @memberId", new { memberId }, ct)).ConfigureAwait(false)
            ?? new PointAccountDTO { MemberId = memberId }, ct);

    public Task<IReadOnlyList<PointLotDTO>> ListLotsAsync(long memberId, bool activeOnly, DateTimeOffset now, CancellationToken ct) =>
        RunAsync<IReadOnlyList<PointLotDTO>>("points.lots", async c => [.. await c.QueryAsync<PointLotDTO>(Cmd($"""
            SELECT {LotColumns} FROM mbr_point_lot WHERE member_id = @memberId {(activeOnly ? "AND remaining > 0 AND expires_at > @now" : string.Empty)}
            ORDER BY expires_at, earned_at, id
            """, new { memberId, now }, ct)).ConfigureAwait(false)], ct);

    public Task<IReadOnlyList<ExpiringPointsDTO>> ListExpiringAsync(long memberId, DateTimeOffset now, DateTimeOffset until, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ExpiringPointsDTO>>("points.expiring", async c => [.. await c.QueryAsync<ExpiringPointsDTO>(Cmd("""
            SELECT expires_at, SUM(remaining) AS points FROM mbr_point_lot
            WHERE member_id = @memberId AND remaining > 0 AND expires_at > @now AND expires_at <= @until GROUP BY expires_at ORDER BY expires_at
            """, new { memberId, now, until }, ct)).ConfigureAwait(false)], ct);

    /// <summary>แถว ledger (type เป็นข้อความ)</summary>
    private sealed class LedgerRow
    {
        public long Id { get; set; }
        public long MemberId { get; set; }
        public string Type { get; set; } = string.Empty;
        public int Points { get; set; }
        public int BalanceAfter { get; set; }
        public string? SourceSystem { get; set; }
        public string? TransactionId { get; set; }
        public string? Reference { get; set; }
        public string? Reason { get; set; }
        public string Actor { get; set; } = string.Empty;
        public string? RequestId { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }

        public PointLedgerDTO ToDto() => new()
        {
            Id = Id, MemberId = MemberId, Type = Enum.Parse<LedgerType>(Type), Points = Points, BalanceAfter = BalanceAfter, SourceSystem = SourceSystem, TransactionId = TransactionId,
            Reference = Reference, Reason = Reason, Actor = Actor, RequestId = RequestId, IdempotencyKey = IdempotencyKey, CreatedAt = CreatedAt,
        };
    }

    public Task<PagedResultDTO<PointLedgerDTO>> SearchLedgerAsync(LedgerCriteria c, CancellationToken ct) =>
        RunAsync("points.ledger", async conn =>
        {
            var p = new DynamicParameters();
            var w = new List<string> { "1 = 1" };
            if (c.MemberId is long mid) { w.Add("member_id = @mid"); p.Add("mid", mid); }
            if (c.Type is LedgerType t) { w.Add("type = @type"); p.Add("type", t.ToString()); }
            if (c.From is DateTimeOffset f) { w.Add("created_at >= @from"); p.Add("from", f); }
            if (c.To is DateTimeOffset to) { w.Add("created_at <= @to"); p.Add("to", to); }
            if (!string.IsNullOrWhiteSpace(c.TransactionId)) { w.Add("transaction_id = @txn"); p.Add("txn", c.TransactionId); }
            int page = Math.Max(c.Page, 1);
            p.Add("offset", (page - 1) * c.PageSize);
            p.Add("take", c.PageSize);
            string where = string.Join(" AND ", w);
            long total = await conn.ExecuteScalarAsync<long>(Cmd($"SELECT COUNT(*) FROM mbr_point_ledger WHERE {where}", p, ct)).ConfigureAwait(false);
            IEnumerable<LedgerRow> rows = await conn.QueryAsync<LedgerRow>(Cmd($"SELECT {LedgerColumns} FROM mbr_point_ledger WHERE {where} ORDER BY created_at DESC, id DESC {Paging}", p, ct)).ConfigureAwait(false);
            return new PagedResultDTO<PointLedgerDTO>([.. rows.Select(r => r.ToDto())], total, page, c.PageSize);
        }, ct);

    public Task<bool> LedgerKeyExistsAsync(string idempotencyKey, CancellationToken ct) =>
        RunAsync("points.key_exists", async c => await c.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM mbr_point_ledger WHERE idempotency_key = @idempotencyKey", new { idempotencyKey }, ct)).ConfigureAwait(false) > 0, ct);

    public Task<IReadOnlyList<PointLotDTO>> ListExpiredLotsAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<PointLotDTO>>("points.expired_lots", async c => [.. await c.QueryAsync<PointLotDTO>(Cmd(
            $"SELECT {LotColumns} FROM mbr_point_lot WHERE remaining > 0 AND expires_at <= @now ORDER BY expires_at {Dialect.Take("@take")}", new { now, take }, ct)).ConfigureAwait(false)], ct);

    public Task<IReadOnlyList<(long MemberId, int Points, DateTimeOffset ExpiresAt)>> ListExpiringForNoticeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        RunAsync<IReadOnlyList<(long, int, DateTimeOffset)>>("points.notice", async c => [.. await c.QueryAsync<(long, int, DateTimeOffset)>(Cmd("""
            SELECT member_id, SUM(remaining), MIN(expires_at) FROM mbr_point_lot WHERE remaining > 0 AND expires_at > @from AND expires_at <= @to GROUP BY member_id
            """, new { from, to }, ct)).ConfigureAwait(false)], ct);

    public Task<int> SumAdjustmentsAsync(string requestedBy, DateTimeOffset since, CancellationToken ct) =>
        RunAsync("points.sum_adjust", c => c.ExecuteScalarAsync<int>(Cmd("""
            SELECT COALESCE(SUM(ABS(points)), 0) FROM mbr_adjustment WHERE requested_by = @requestedBy AND created_at >= @since AND status <> 'Rejected'
            """, new { requestedBy, since }, ct)), ct);

    Task<PointTotalsDTO> IPointDAO.GetTotalsAsync(DateTimeOffset since, CancellationToken ct) =>
        RunAsync("points.totals", async c => new PointTotalsDTO
        {
            Outstanding = await c.ExecuteScalarAsync<long>(Cmd("SELECT COALESCE(SUM(CAST(lot_balance AS BIGINT) - debt), 0) FROM mbr_point_account", null, ct)).ConfigureAwait(false),
            EarnedSince = await c.ExecuteScalarAsync<long>(Cmd("SELECT COALESCE(SUM(CAST(points AS BIGINT)), 0) FROM mbr_point_ledger WHERE type = 'Earn' AND created_at >= @since", new { since }, ct)).ConfigureAwait(false),
            RedeemedSince = -await c.ExecuteScalarAsync<long>(Cmd("SELECT COALESCE(SUM(CAST(points AS BIGINT)), 0) FROM mbr_point_ledger WHERE type = 'Redeem' AND created_at >= @since", new { since }, ct)).ConfigureAwait(false),
        }, ct);

    private static EarnRuleDTO MapRule(DataRow r) => R<EarnRuleDTO>(r, (x, y) => { x.Id = y.Id; x.Version = y.Version; });

    public Task<IReadOnlyList<EarnRuleDTO>> ListRulesAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<EarnRuleDTO>>("rule.list", async c => [.. (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_earn_rule ORDER BY id", null, ct)).ConfigureAwait(false)).Select(MapRule)], ct);

    public Task<EarnRuleDTO?> GetRuleAsync(long id, CancellationToken ct) =>
        RunAsync("rule.get", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_earn_rule WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(MapRule).FirstOrDefault(), ct);

    public Task<long> InsertRuleAsync(EarnRuleDTO rule, CancellationToken ct) =>
        RunAsync("rule.insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_earn_rule", "code, version, data", "@Code, 1, @Data"), new { rule.Code, Data = J(rule) }, ct)), ct);

    public Task<bool> UpdateRuleAsync(EarnRuleDTO rule, CancellationToken ct) =>
        RunAsync("rule.update", async c => await c.ExecuteAsync(Cmd("UPDATE mbr_earn_rule SET code = @Code, data = @Data, version = version + 1 WHERE id = @Id AND version = @Version",
            new { rule.Id, rule.Code, rule.Version, Data = J(rule) }, ct)).ConfigureAwait(false) == 1, ct);

    private static AdjustmentDTO MapAdjustment(DataRow r) => R<AdjustmentDTO>(r, (x, y) => x.Id = y.Id);

    public Task<AdjustmentDTO?> GetAdjustmentAsync(long id, CancellationToken ct) =>
        RunAsync("adjust.get", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, data FROM mbr_adjustment WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(MapAdjustment).FirstOrDefault(), ct);

    public Task<IReadOnlyList<AdjustmentDTO>> ListAdjustmentsAsync(AdjustmentStatus? status, long? memberId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<AdjustmentDTO>>("adjust.list", async c => [.. (await c.QueryAsync<DataRow>(Cmd($"""
            SELECT id, data FROM mbr_adjustment WHERE (@status IS NULL OR status = @status) AND (@memberId IS NULL OR member_id = @memberId) ORDER BY id DESC {Dialect.Take("1000")}
            """, new { status = status?.ToString(), memberId }, ct)).ConfigureAwait(false)).Select(MapAdjustment)], ct);

    public Task<long> InsertAdjustmentAsync(AdjustmentDTO adjustment, CancellationToken ct) =>
        RunAsync("adjust.insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_adjustment", "member_id, status, requested_by, points, created_at, data",
            "@MemberId, @Status, @RequestedBy, @Points, @CreatedAt, @Data"), AdjustmentArgs(adjustment), ct)), ct);

    public Task UpdateAdjustmentAsync(AdjustmentDTO adjustment, CancellationToken ct) =>
        RunAsync("adjust.update", c => UpdateAdjustmentAsync(c, null, adjustment, ct), ct);

    private Task<int> UpdateAdjustmentAsync(DbConnection c, DbTransaction? tx, AdjustmentDTO a, CancellationToken ct) =>
        c.ExecuteAsync(Cmd("UPDATE mbr_adjustment SET status = @Status, data = @Data WHERE id = @Id", AdjustmentArgs(a), ct, tx));

    private static object AdjustmentArgs(AdjustmentDTO a) => new { a.Id, a.MemberId, Status = a.Status.ToString(), a.RequestedBy, a.Points, a.CreatedAt, Data = J(a) };

    // ================================================================== ICampaignDAO

    private static CampaignDTO MapCampaign(DataRow r) => R<CampaignDTO>(r, (x, y) => { x.Id = y.Id; x.Version = y.Version; });

    public Task<PagedResultDTO<CampaignDTO>> SearchAsync(CampaignSearchCriteria c, CancellationToken ct)
    {
        var p = new DynamicParameters();
        var w = new List<string> { "1 = 1" };
        if (c.Status is CampaignStatus s) { w.Add("status = @status"); p.Add("status", s.ToString()); }
        if (c.From is DateTimeOffset f) { w.Add("end_at >= @from"); p.Add("from", f); }
        if (c.To is DateTimeOffset t) { w.Add("start_at <= @to"); p.Add("to", t); }
        if (c.RewardType is RewardType rt) { w.Add("reward_type = @rt"); p.Add("rt", rt.ToString()); }
        if (!string.IsNullOrWhiteSpace(c.Text)) { w.Add("(code LIKE @text OR name LIKE @text)"); p.Add("text", "%" + c.Text.Trim().Replace("%", "", StringComparison.Ordinal) + "%"); }
        return PagedAsync("campaign.search", "mbr_campaign", string.Join(" AND ", w), "id DESC", p, c.Page, c.PageSize, "id, version, data", MapCampaign, ct);
    }

    Task<CampaignDTO?> ICampaignDAO.GetAsync(long id, CancellationToken ct) =>
        RunAsync("campaign.get", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_campaign WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(MapCampaign).FirstOrDefault(), ct);

    Task<CampaignDTO?> ICampaignDAO.GetByCodeAsync(string code, CancellationToken ct) =>
        RunAsync("campaign.by_code", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_campaign WHERE code = @code", new { code = code.ToUpperInvariant() }, ct)).ConfigureAwait(false)).Select(MapCampaign).FirstOrDefault(), ct);

    public Task<IReadOnlyList<CampaignDTO>> ListByStatusAsync(IReadOnlyCollection<CampaignStatus> statuses, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CampaignDTO>>("campaign.by_status", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT id, version, data FROM mbr_campaign WHERE {(Dialect.IsSqlServer() ? "status IN (SELECT [value] FROM OPENJSON(@s))" : "status = ANY(@s)")} ORDER BY priority, id",
            new { s = Dialect.SetParam(statuses.Select(x => x.ToString())) }, ct)).ConfigureAwait(false)).Select(MapCampaign)], ct);

    public Task<IReadOnlyList<CampaignDTO>> ListByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct) =>
        ids.Count == 0 ? Task.FromResult<IReadOnlyList<CampaignDTO>>([]) :
        RunAsync<IReadOnlyList<CampaignDTO>>("campaign.by_ids", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT id, version, data FROM mbr_campaign WHERE {Dialect.InSet("id", "ids")} ORDER BY id", new { ids = Dialect.SetParam(ids) }, ct)).ConfigureAwait(false)).Select(MapCampaign)], ct);

    private static object CampaignArgs(CampaignDTO x) => new
    {
        x.Id, Code = x.Code.ToUpperInvariant(), x.Name, Status = x.Status.ToString(), RewardType = x.RewardType.ToString(), x.StartAt, x.EndAt, x.Priority, x.Version, Data = J(x),
    };

    Task<long> ICampaignDAO.InsertAsync(CampaignDTO campaign, CancellationToken ct) =>
        RunAsync("campaign.insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_campaign", "code, name, status, reward_type, start_at, end_at, priority, version, data",
            "@Code, @Name, @Status, @RewardType, @StartAt, @EndAt, @Priority, 1, @Data"), CampaignArgs(campaign), ct)), ct);

    Task<bool> ICampaignDAO.UpdateAsync(CampaignDTO campaign, CancellationToken ct) =>
        RunAsync("campaign.update", async c => await c.ExecuteAsync(Cmd("""
            UPDATE mbr_campaign SET code = @Code, name = @Name, status = @Status, reward_type = @RewardType, start_at = @StartAt, end_at = @EndAt, priority = @Priority,
                data = @Data, version = version + 1 WHERE id = @Id AND version = @Version
            """, CampaignArgs(campaign), ct)).ConfigureAwait(false) == 1, ct);

    /// <summary>แถวตัวนับ</summary>
    private sealed class CounterRow
    {
        public long CampaignId { get; set; }
        public string Scope { get; set; } = string.Empty;
        public int Uses { get; set; }
        public int Reserved { get; set; }
        public decimal BudgetUsed { get; set; }
        public decimal BudgetReserved { get; set; }
    }

    public Task<IReadOnlyDictionary<(long CampaignId, string Scope), CampaignUsageDTO>> GetCountersAsync(IReadOnlyCollection<long> campaignIds, IReadOnlyCollection<string> scopes, CancellationToken ct) =>
        RunAsync<IReadOnlyDictionary<(long, string), CampaignUsageDTO>>("campaign.counters", async c =>
        {
            if (campaignIds.Count == 0 || scopes.Count == 0)
            {
                return new Dictionary<(long, string), CampaignUsageDTO>();
            }

            string scopeIn = Dialect.IsSqlServer() ? "scope IN (SELECT [value] FROM OPENJSON(@scopes))" : "scope = ANY(@scopes)";
            IEnumerable<CounterRow> rows = await c.QueryAsync<CounterRow>(Cmd($"SELECT campaign_id, scope, uses, reserved, budget_used, budget_reserved FROM mbr_campaign_counter WHERE {Dialect.InSet("campaign_id", "ids")} AND {scopeIn}",
                new { ids = Dialect.SetParam(campaignIds), scopes = Dialect.SetParam(scopes) }, ct)).ConfigureAwait(false);
            return rows.ToDictionary(r => (r.CampaignId, r.Scope), r => new CampaignUsageDTO { Uses = r.Uses, Reserved = r.Reserved, BudgetUsed = r.BudgetUsed, BudgetReserved = r.BudgetReserved });
        }, ct);

    // ================================================================== ICouponDAO

    private static CouponBatchDTO MapBatch(DataRow r) => R<CouponBatchDTO>(r, (x, y) => x.Id = y.Id);

    public Task<IReadOnlyList<CouponBatchDTO>> ListBatchesAsync(long? campaignId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CouponBatchDTO>>("coupon.batches", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            "SELECT id, data FROM mbr_coupon_batch WHERE (@campaignId IS NULL OR campaign_id = @campaignId) ORDER BY id DESC", new { campaignId }, ct)).ConfigureAwait(false)).Select(MapBatch)], ct);

    public Task<CouponBatchDTO?> GetBatchAsync(long id, CancellationToken ct) =>
        RunAsync("coupon.batch", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, data FROM mbr_coupon_batch WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(MapBatch).FirstOrDefault(), ct);

    public Task<long> InsertBatchAsync(CouponBatchDTO batch, CancellationToken ct) =>
        RunAsync("coupon.batch_insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_coupon_batch", "campaign_id, data", "@CampaignId, @Data"), new { batch.CampaignId, Data = J(batch) }, ct)), ct);

    public Task UpdateBatchAsync(CouponBatchDTO batch, CancellationToken ct) =>
        RunAsync("coupon.batch_update", c => c.ExecuteAsync(Cmd("UPDATE mbr_coupon_batch SET data = @Data WHERE id = @Id", new { batch.Id, Data = J(batch) }, ct)), ct);

    private const string CouponSelect = "id, version, data";

    private static CouponDTO MapCoupon(DataRow r) => R<CouponDTO>(r, (x, y) => { x.Id = y.Id; x.Version = y.Version; });

    Task<CouponDTO?> ICouponDAO.GetByCodeAsync(string code, CancellationToken ct) =>
        RunAsync("coupon.get", async c => (await c.QueryAsync<DataRow>(Cmd($"SELECT {CouponSelect} FROM mbr_coupon WHERE code = @code", new { code = code.ToUpperInvariant() }, ct)).ConfigureAwait(false)).Select(MapCoupon).FirstOrDefault(), ct);

    public Task<IReadOnlyList<CouponDTO>> GetByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct) =>
        codes.Count == 0 ? Task.FromResult<IReadOnlyList<CouponDTO>>([]) :
        RunAsync<IReadOnlyList<CouponDTO>>("coupon.get_many", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT {CouponSelect} FROM mbr_coupon WHERE {(Dialect.IsSqlServer() ? "code IN (SELECT [value] FROM OPENJSON(@codes))" : "code = ANY(@codes)")}",
            new { codes = Dialect.SetParam(codes.Select(x => x.ToUpperInvariant())) }, ct)).ConfigureAwait(false)).Select(MapCoupon)], ct);

    public Task<PagedResultDTO<CouponDTO>> SearchAsync(CouponSearchCriteria c, CancellationToken ct)
    {
        var p = new DynamicParameters();
        var w = new List<string> { "1 = 1" };
        if (!string.IsNullOrWhiteSpace(c.Code)) { w.Add("code LIKE @code"); p.Add("code", "%" + c.Code.Trim().ToUpperInvariant().Replace("%", "", StringComparison.Ordinal) + "%"); }
        if (c.BatchId is long b) { w.Add("batch_id = @batch"); p.Add("batch", b); }
        if (c.CampaignId is long cid) { w.Add("campaign_id = @cid"); p.Add("cid", cid); }
        if (c.MemberId is long mid) { w.Add("member_id = @mid"); p.Add("mid", mid); }
        if (c.Status is CouponStatus s) { w.Add("status = @status"); p.Add("status", s.ToString()); }
        return PagedAsync("coupon.search", "mbr_coupon", string.Join(" AND ", w), "created_at DESC, code", p, c.Page, c.PageSize, CouponSelect, MapCoupon, ct);
    }

    public Task<IReadOnlyList<CouponDTO>> ListByMemberAsync(long memberId, CouponStatus? status, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CouponDTO>>("coupon.by_member", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT {CouponSelect} FROM mbr_coupon WHERE member_id = @memberId AND (@status IS NULL OR status = @status) ORDER BY expires_at", new { memberId, status = status?.ToString() }, ct)).ConfigureAwait(false)).Select(MapCoupon)], ct);

    private static object CouponArgs(CouponDTO x, long expected) => new
    {
        Code = x.Code.ToUpperInvariant(), x.BatchId, x.CampaignId, x.MemberId, Status = x.Status.ToString(), x.ExpiresAt, x.CreatedAt, Version = expected + 1, Expected = expected, Data = J(x),
    };

    private const string CouponInsert = """
        INSERT INTO mbr_coupon (code, batch_id, campaign_id, member_id, status, expires_at, created_at, version, data)
        VALUES (@Code, @BatchId, @CampaignId, @MemberId, @Status, @ExpiresAt, @CreatedAt, @Version, @Data)
        """;

    public Task<int> InsertManyAsync(IReadOnlyList<CouponDTO> coupons, CancellationToken ct) =>
        RunAsync("coupon.insert_many", async c =>
        {
            // รหัสซ้ำ = ข้าม (ตรวจรหัสที่มีอยู่ก่อน แล้วเพิ่มเฉพาะที่ไม่ซ้ำ)
            HashSet<string> existing = [.. (await GetByCodesAsync([.. coupons.Select(x => x.Code)], ct).ConfigureAwait(false)).Select(x => x.Code)];
            List<CouponDTO> fresh = [.. coupons.Where(x => !existing.Contains(x.Code.ToUpperInvariant())).DistinctBy(x => x.Code.ToUpperInvariant())];
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            await c.ExecuteAsync(Cmd(CouponInsert, fresh.Select(x => CouponArgs(x, 0)), ct, tx)).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return fresh.Count;
        }, ct);

    public Task<IReadOnlyList<CouponDTO>> TakeUnassignedAsync(long batchId, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CouponDTO>>("coupon.unassigned", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT {CouponSelect} FROM mbr_coupon WHERE batch_id = @batchId AND member_id IS NULL AND status = 'Available' ORDER BY id {Dialect.Take("@take")}", new { batchId, take }, ct)).ConfigureAwait(false)).Select(MapCoupon)], ct);

    public Task<IReadOnlyList<CouponEventDTO>> ListEventsAsync(string code, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CouponEventDTO>>("coupon.events", async c => [.. (await c.QueryAsync<DataRow>(Cmd("SELECT id, data FROM mbr_coupon_event WHERE code = @code ORDER BY at, id",
            new { code = code.ToUpperInvariant() }, ct)).ConfigureAwait(false)).Select(r => R<CouponEventDTO>(r, (x, y) => x.Id = y.Id))], ct);

    public Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct) =>
        RunAsync("coupon.expire", async c =>
        {
            List<CouponDTO> due = [.. (await c.QueryAsync<DataRow>(Cmd($"SELECT {CouponSelect} FROM mbr_coupon WHERE status IN ('Available', 'Reversed') AND expires_at <= @now ORDER BY id {Dialect.Take("5000")}",
                new { now }, ct)).ConfigureAwait(false)).Select(MapCoupon)];
            int n = 0;
            foreach (CouponDTO x in due)
            {
                await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
                CouponStatus from = x.Status;
                x.Status = CouponStatus.Expired;
                if (await c.ExecuteAsync(Cmd("UPDATE mbr_coupon SET status = @Status, version = @Version, data = @Data WHERE code = @Code AND version = @Expected", CouponArgs(x, x.Version), ct, tx)).ConfigureAwait(false) == 1)
                {
                    await InsertCouponEventsAsync(c, tx, [new CouponEventDTO { Code = x.Code, FromStatus = from, ToStatus = CouponStatus.Expired, Actor = "system", At = now }], ct).ConfigureAwait(false);
                    n++;
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            return n;
        }, ct);

    private Task InsertCouponEventsAsync(DbConnection c, DbTransaction tx, IEnumerable<CouponEventDTO> events, CancellationToken ct) =>
        c.ExecuteAsync(Cmd("INSERT INTO mbr_coupon_event (code, at, data) VALUES (@Code, @At, @Data)", events.Select(e => new { Code = e.Code.ToUpperInvariant(), e.At, Data = J(e) }), ct, tx));

    // ================================================================== ISegmentDAO

    private static SegmentDTO MapSegment(DataRow r) => R<SegmentDTO>(r, (x, y) => { x.Id = y.Id; x.Version = y.Version; });

    Task<IReadOnlyList<SegmentDTO>> ISegmentDAO.ListAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<SegmentDTO>>("segment.list", async c => [.. (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_segment ORDER BY id", null, ct)).ConfigureAwait(false)).Select(MapSegment)], ct);

    Task<SegmentDTO?> ISegmentDAO.GetAsync(long id, CancellationToken ct) =>
        RunAsync("segment.get", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_segment WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(MapSegment).FirstOrDefault(), ct);

    Task<long> ISegmentDAO.InsertAsync(SegmentDTO segment, CancellationToken ct) =>
        RunAsync("segment.insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_segment", "version, data", "1, @Data"), new { Data = J(segment) }, ct)), ct);

    Task<bool> ISegmentDAO.UpdateAsync(SegmentDTO segment, CancellationToken ct) =>
        RunAsync("segment.update", async c => await c.ExecuteAsync(Cmd("UPDATE mbr_segment SET data = @Data, version = version + 1 WHERE id = @Id AND version = @Version",
            new { segment.Id, segment.Version, Data = J(segment) }, ct)).ConfigureAwait(false) == 1, ct);

    public Task ReplaceMembersAsync(long segmentId, IReadOnlyList<long> memberIds, DateTimeOffset refreshedAt, CancellationToken ct) =>
        RunAsync("segment.replace", async c =>
        {
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            await c.ExecuteAsync(Cmd("DELETE FROM mbr_segment_member WHERE segment_id = @segmentId", new { segmentId }, ct, tx)).ConfigureAwait(false);
            await c.ExecuteAsync(Cmd("INSERT INTO mbr_segment_member (segment_id, member_id) VALUES (@segmentId, @memberId)", memberIds.Distinct().Select(memberId => new { segmentId, memberId }), ct, tx)).ConfigureAwait(false);
            DataRow? row = (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_segment WHERE id = @segmentId", new { segmentId }, ct, tx)).ConfigureAwait(false)).FirstOrDefault();
            if (row is not null)
            {
                SegmentDTO s = MapSegment(row);
                s.MemberCount = memberIds.Count;
                s.RefreshedAt = refreshedAt;
                await c.ExecuteAsync(Cmd("UPDATE mbr_segment SET data = @Data WHERE id = @segmentId", new { segmentId, Data = J(s) }, ct, tx)).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, ct);

    public Task<IReadOnlySet<long>> ListSegmentIdsOfMemberAsync(long memberId, CancellationToken ct) =>
        RunAsync<IReadOnlySet<long>>("segment.of_member", async c => (await c.QueryAsync<long>(Cmd("SELECT segment_id FROM mbr_segment_member WHERE member_id = @memberId", new { memberId }, ct)).ConfigureAwait(false)).ToHashSet(), ct);

    public Task<PagedResultDTO<long>> PageMemberIdsAsync(long segmentId, int page, int pageSize, CancellationToken ct) =>
        RunAsync("segment.page_members", async c =>
        {
            page = Math.Max(page, 1);
            long total = await c.ExecuteScalarAsync<long>(Cmd("SELECT COUNT(*) FROM mbr_segment_member WHERE segment_id = @segmentId", new { segmentId }, ct)).ConfigureAwait(false);
            IEnumerable<long> ids = await c.QueryAsync<long>(Cmd($"SELECT member_id FROM mbr_segment_member WHERE segment_id = @segmentId ORDER BY member_id {Paging}",
                new { segmentId, offset = (page - 1) * pageSize, take = pageSize }, ct)).ConfigureAwait(false);
            return new PagedResultDTO<long>([.. ids], total, page, pageSize);
        }, ct);
}
