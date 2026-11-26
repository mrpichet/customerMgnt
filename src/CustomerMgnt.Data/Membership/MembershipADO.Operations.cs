using System.Data.Common;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Membership.Services;
using CustomerMgnt.Data.Infrastructure;
using Dapper;

namespace CustomerMgnt.Data.Membership;

/// <summary>checkout, อนุมัติ, audit, outbox, idempotency, ค่าตั้ง, OTP/PDPA, งานเบื้องหลัง</summary>
public sealed partial class MembershipADO
{
    // ================================================================== ICheckoutDAO

    private static ReservationEntity MapReservation(DataRow r) => R<ReservationEntity>(r, (x, y) => x.Version = y.Version);

    public Task<ReservationEntity?> GetReservationAsync(string id, CancellationToken ct) =>
        RunAsync("reservation.get", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT version, data FROM mbr_reservation WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(MapReservation).FirstOrDefault(), ct);

    public Task<IReadOnlyList<ReservationEntity>> ListExpiredReservationsAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ReservationEntity>>("reservation.expired", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT version, data FROM mbr_reservation WHERE status = 'Reserved' AND expires_at <= @now ORDER BY expires_at {Dialect.Take("@take")}", new { now, take }, ct)).ConfigureAwait(false)).Select(MapReservation)], ct);

    private static SalesTransactionEntity MapTxn(DataRow r) => R<SalesTransactionEntity>(r, (x, y) => { x.Id = y.Id; x.Version = y.Version; });

    public Task<SalesTransactionEntity?> GetTransactionAsync(string sourceSystem, string transactionId, CancellationToken ct) =>
        RunAsync("txn.get", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_transaction WHERE source_system = @sourceSystem AND transaction_id = @transactionId",
            new { sourceSystem, transactionId }, ct)).ConfigureAwait(false)).Select(MapTxn).FirstOrDefault(), ct);

    public Task<SalesTransactionEntity?> GetTransactionByIdAsync(long id, CancellationToken ct) =>
        RunAsync("txn.by_id", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_transaction WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(MapTxn).FirstOrDefault(), ct);

    public Task<PagedResultDTO<SalesTransactionEntity>> SearchTransactionsAsync(TransactionSearchCriteria c, CancellationToken ct)
    {
        var p = new DynamicParameters();
        var w = new List<string> { "1 = 1" };
        if (c.MemberId is long mid) { w.Add("member_id = @mid"); p.Add("mid", mid); }
        if (!string.IsNullOrWhiteSpace(c.TransactionId)) { w.Add("transaction_id = @txn"); p.Add("txn", c.TransactionId); }
        if (!string.IsNullOrWhiteSpace(c.SourceSystem)) { w.Add("source_system = @src"); p.Add("src", c.SourceSystem); }
        if (!string.IsNullOrWhiteSpace(c.BranchCode)) { w.Add("branch_code = @branch"); p.Add("branch", c.BranchCode); }
        if (c.From is DateTimeOffset f) { w.Add("occurred_at >= @from"); p.Add("from", f); }
        if (c.To is DateTimeOffset t) { w.Add("occurred_at <= @to"); p.Add("to", t); }
        if (c.Status is TransactionStatus s) { w.Add("status = @status"); p.Add("status", s.ToString()); }
        return PagedAsync("txn.search", "mbr_transaction", string.Join(" AND ", w), "occurred_at DESC, id DESC", p, c.Page, c.PageSize, "id, version, data", MapTxn, ct);
    }

    public Task<IReadOnlyList<SalesReturnDTO>> ListReturnsAsync(string sourceSystem, string transactionId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<SalesReturnDTO>>("txn.returns", async c => [.. (await c.QueryAsync<DataRow>(Cmd("SELECT id, data FROM mbr_return WHERE source_system = @sourceSystem AND transaction_id = @transactionId ORDER BY id",
            new { sourceSystem, transactionId }, ct)).ConfigureAwait(false)).Select(r => R<SalesReturnDTO>(r, (x, y) => x.Id = y.Id))], ct);

    public Task<decimal> GetQualifyingSpendAsync(long memberId, DateTimeOffset since, CancellationToken ct) =>
        RunAsync("txn.spend", c => c.ExecuteScalarAsync<decimal>(Cmd("""
            SELECT COALESCE(SUM(eligible - returned_eligible), 0) FROM mbr_transaction WHERE member_id = @memberId AND status <> 'Cancelled' AND occurred_at >= @since
            """, new { memberId, since }, ct)), ct);

    Task<SalesTotalsDTO> ICheckoutDAO.GetTotalsAsync(DateTimeOffset since, CancellationToken ct) =>
        RunAsync("txn.totals", async c =>
        {
            (decimal sales, int n) = await c.QuerySingleAsync<(decimal, int)>(Cmd("SELECT COALESCE(SUM(net_paid), 0), COUNT(*) FROM mbr_transaction WHERE status <> 'Cancelled' AND occurred_at >= @since", new { since }, ct)).ConfigureAwait(false);
            return new SalesTotalsDTO { Sales = sales, Transactions = n };
        }, ct);

    public Task<long> InsertPendingReturnAsync(PendingReturnEntity pending, CancellationToken ct) =>
        RunAsync("txn.pending_insert", async c =>
        {
            long? existing = await c.ExecuteScalarAsync<long?>(Cmd("SELECT id FROM mbr_pending_return WHERE source_system = @SourceSystem AND transaction_id = @TransactionId AND return_id = @ReturnId", pending, ct)).ConfigureAwait(false);
            return existing ?? await c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_pending_return", "source_system, transaction_id, return_id, processed_at, data",
                "@SourceSystem, @TransactionId, @ReturnId, @ProcessedAt, @Data"), new { pending.SourceSystem, pending.TransactionId, pending.ReturnId, pending.ProcessedAt, Data = J(pending) }, ct)).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<PendingReturnEntity>> ListPendingReturnsAsync(int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<PendingReturnEntity>>("txn.pending_list", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT id, data FROM mbr_pending_return WHERE processed_at IS NULL ORDER BY id {Dialect.Take("@take")}", new { take }, ct)).ConfigureAwait(false)).Select(r => R<PendingReturnEntity>(r, (x, y) => x.Id = y.Id))], ct);

    public Task UpdatePendingReturnAsync(PendingReturnEntity pending, CancellationToken ct) =>
        RunAsync("txn.pending_update", c => c.ExecuteAsync(Cmd("UPDATE mbr_pending_return SET processed_at = @ProcessedAt, data = @Data WHERE id = @Id", new { pending.Id, pending.ProcessedAt, Data = J(pending) }, ct)), ct);

    // ================================================================== IApprovalDAO

    private static ApprovalRequestDTO MapApproval(DataRow r) => R<ApprovalRequestDTO>(r, (x, y) => { x.Id = y.Id; x.Version = y.Version; });

    Task<long> IApprovalDAO.InsertAsync(ApprovalRequestDTO request, CancellationToken ct) =>
        RunAsync("approval.insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_approval", "type, status, requested_by, version, data", "@Type, @Status, @RequestedBy, 1, @Data"),
            new { Type = request.Type.ToString(), Status = request.Status.ToString(), request.RequestedBy, Data = J(request) }, ct)), ct);

    Task<ApprovalRequestDTO?> IApprovalDAO.GetAsync(long id, CancellationToken ct) =>
        RunAsync("approval.get", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_approval WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(MapApproval).FirstOrDefault(), ct);

    Task<IReadOnlyList<ApprovalRequestDTO>> IApprovalDAO.SearchAsync(ApprovalSearchCriteria c, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ApprovalRequestDTO>>("approval.search", async conn => [.. (await conn.QueryAsync<DataRow>(Cmd($"""
            SELECT id, version, data FROM mbr_approval
            WHERE (@status IS NULL OR status = @status) AND (@type IS NULL OR type = @type) AND (@by IS NULL OR requested_by = @by)
            ORDER BY id DESC {Dialect.Take("500")}
            """, new { status = c.Status?.ToString(), type = c.Type?.ToString(), by = c.RequestedBy }, ct)).ConfigureAwait(false)).Select(MapApproval)], ct);

    Task<bool> IApprovalDAO.UpdateAsync(ApprovalRequestDTO request, CancellationToken ct) =>
        RunAsync("approval.update", async c => await c.ExecuteAsync(Cmd("UPDATE mbr_approval SET status = @Status, data = @Data, version = version + 1 WHERE id = @Id AND version = @Version",
            new { request.Id, Status = request.Status.ToString(), request.Version, Data = J(request) }, ct)).ConfigureAwait(false) == 1, ct);

    // ================================================================== IMembershipAuditDAO

    public Task AppendAsync(IReadOnlyList<MembershipAuditDTO> entries, CancellationToken ct) =>
        entries.Count == 0 ? Task.CompletedTask : RunAsync("audit.append", async c =>
        {
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            await AppendAuditAsync(c, tx, entries, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>ต่อ chain ภายใต้ lock ของ transaction (กันสองรายการอ้าง hash ก่อนหน้าเดียวกัน)</summary>
    private async Task AppendAuditAsync(DbConnection c, DbTransaction tx, IEnumerable<MembershipAuditDTO> entries, CancellationToken ct)
    {
        await c.ExecuteAsync(Cmd(Dialect.IsSqlServer()
            ? "EXEC sp_getapplock @Resource = 'mbr_audit', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000"
            : "SELECT pg_advisory_xact_lock(4D425241)".Replace("4D425241", "1296191041", StringComparison.Ordinal), null, ct, tx)).ConfigureAwait(false);
        string? prev = await c.ExecuteScalarAsync<string?>(Cmd($"SELECT hash FROM mbr_audit ORDER BY id DESC {Dialect.Take("1")}", null, ct, tx)).ConfigureAwait(false);
        foreach (MembershipAuditDTO e in entries)
        {
            MembershipAuditChain.Seal(e, prev);
            prev = e.Hash;
            await c.ExecuteAsync(Cmd("INSERT INTO mbr_audit (at, actor, action, target, prev_hash, hash, data) VALUES (@At, @Actor, @Action, @Target, @PrevHash, @Hash, @Data)",
                new { e.At, e.Actor, e.Action, e.Target, e.PrevHash, e.Hash, Data = J(e) }, ct, tx)).ConfigureAwait(false);
        }
    }

    Task<PagedResultDTO<MembershipAuditDTO>> IMembershipAuditDAO.SearchAsync(AuditSearchCriteria c, CancellationToken ct)
    {
        var p = new DynamicParameters();
        var w = new List<string> { "1 = 1" };
        if (!string.IsNullOrWhiteSpace(c.Actor)) { w.Add("actor LIKE @actor"); p.Add("actor", "%" + c.Actor.Trim().Replace("%", "", StringComparison.Ordinal) + "%"); }
        if (!string.IsNullOrWhiteSpace(c.Action)) { w.Add("action LIKE @action"); p.Add("action", c.Action.Trim().Replace("%", "", StringComparison.Ordinal) + "%"); }
        if (!string.IsNullOrWhiteSpace(c.Target)) { w.Add("target = @target"); p.Add("target", c.Target.Trim()); }
        if (c.From is DateTimeOffset f) { w.Add("at >= @from"); p.Add("from", f); }
        if (c.To is DateTimeOffset t) { w.Add("at <= @to"); p.Add("to", t); }
        return PagedAsync("audit.search", "mbr_audit", string.Join(" AND ", w), "id DESC", p, c.Page, c.PageSize, "id, data",
            r => R<MembershipAuditDTO>(r, (x, y) => x.Id = y.Id), ct);
    }

    // ================================================================== IOutboxDAO

    private const string OutboxColumns = "id, event_id, topic, payload AS payload_json, attempts, next_attempt_at, created_at, sent_at, dead_lettered, last_error";

    public Task<IReadOnlyList<OutboxMessageDTO>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<OutboxMessageDTO>>("outbox.due", async c => [.. await c.QueryAsync<OutboxMessageDTO>(Cmd(
            $"SELECT {OutboxColumns} FROM mbr_outbox WHERE sent_at IS NULL AND dead_lettered = @no AND next_attempt_at <= @now ORDER BY id {Dialect.Take("@take")}",
            new { now, take, no = false }, ct)).ConfigureAwait(false)], ct);

    public Task MarkSentAsync(long id, DateTimeOffset sentAt, CancellationToken ct) =>
        RunAsync("outbox.sent", c => c.ExecuteAsync(Cmd("UPDATE mbr_outbox SET sent_at = @sentAt WHERE id = @id", new { id, sentAt }, ct)), ct);

    public Task MarkFailedAsync(long id, int attempts, DateTimeOffset nextAttemptAt, string error, bool deadLetter, CancellationToken ct) =>
        RunAsync("outbox.failed", c => c.ExecuteAsync(Cmd("UPDATE mbr_outbox SET attempts = @attempts, next_attempt_at = @nextAttemptAt, last_error = @error, dead_lettered = @deadLetter WHERE id = @id",
            new { id, attempts, nextAttemptAt, error = error.Length > 500 ? error[..500] : error, deadLetter }, ct)), ct);

    public Task EnqueueAsync(IReadOnlyList<OutboxMessageDTO> messages, CancellationToken ct) =>
        messages.Count == 0 ? Task.CompletedTask : RunAsync("outbox.enqueue", async c =>
        {
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            await InsertOutboxAsync(c, tx, messages, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, ct);

    private Task InsertOutboxAsync(DbConnection c, DbTransaction tx, IEnumerable<OutboxMessageDTO> messages, CancellationToken ct) =>
        c.ExecuteAsync(Cmd("""
            INSERT INTO mbr_outbox (event_id, topic, payload, attempts, next_attempt_at, created_at, sent_at, dead_lettered, last_error)
            VALUES (@EventId, @Topic, @PayloadJson, 0, @NextAttemptAt, @CreatedAt, NULL, @No, NULL)
            """, messages.Select(m => new { m.EventId, m.Topic, m.PayloadJson, m.NextAttemptAt, m.CreatedAt, No = false }), ct, tx));

    Task<(int Backlog, int DeadLetters)> IOutboxDAO.GetStatsAsync(CancellationToken ct) =>
        RunAsync("outbox.stats", async c => (
            await c.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM mbr_outbox WHERE sent_at IS NULL AND dead_lettered = @no", new { no = false }, ct)).ConfigureAwait(false),
            await c.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM mbr_outbox WHERE dead_lettered = @yes", new { yes = true }, ct)).ConfigureAwait(false)), ct);

    // ================================================================== IIdempotencyDAO

    private const string IdemColumns = "source_system, idem_key AS [key], request_hash, status_code, response AS response_json, created_at, expires_at";

    private string IdemSelect => Dialect.IsSqlServer() ? IdemColumns : IdemColumns.Replace("[key]", "\"key\"", StringComparison.Ordinal);

    public async Task<IdempotencyRecordDTO?> TryBeginAsync(IdempotencyRecordDTO record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            return await RunAsync("idem.begin", async c =>
            {
                await c.ExecuteAsync(Cmd("DELETE FROM mbr_idempotency WHERE source_system = @SourceSystem AND idem_key = @Key AND expires_at <= @CreatedAt", record, ct)).ConfigureAwait(false);
                await c.ExecuteAsync(Cmd("""
                    INSERT INTO mbr_idempotency (source_system, idem_key, request_hash, status_code, response, created_at, expires_at)
                    VALUES (@SourceSystem, @Key, @RequestHash, 0, NULL, @CreatedAt, @ExpiresAt)
                    """, record, ct)).ConfigureAwait(false);
                return (IdempotencyRecordDTO?)null;
            }, ct).ConfigureAwait(false);
        }
        catch (ConflictException)
        {
            return await RunAsync("idem.get", c => c.QuerySingleOrDefaultAsync<IdempotencyRecordDTO>(Cmd(
                $"SELECT {IdemSelect} FROM mbr_idempotency WHERE source_system = @SourceSystem AND idem_key = @Key", record, ct)), ct).ConfigureAwait(false);
        }
    }

    public Task CompleteAsync(string sourceSystem, string key, int statusCode, string? responseJson, CancellationToken ct) =>
        RunAsync("idem.complete", c => c.ExecuteAsync(Cmd("UPDATE mbr_idempotency SET status_code = @statusCode, response = @responseJson WHERE source_system = @sourceSystem AND idem_key = @key",
            new { sourceSystem, key, statusCode, responseJson }, ct)), ct);

    public Task DeleteAsync(string sourceSystem, string key, CancellationToken ct) =>
        RunAsync("idem.delete", c => c.ExecuteAsync(Cmd("DELETE FROM mbr_idempotency WHERE source_system = @sourceSystem AND idem_key = @key", new { sourceSystem, key }, ct)), ct);

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct) =>
        RunAsync("idem.purge", async c =>
        {
            await c.ExecuteAsync(Cmd("UPDATE mbr_export SET content = NULL, status = 'Expired' WHERE expires_at <= @now AND content IS NOT NULL", new { now }, ct)).ConfigureAwait(false);
            return await c.ExecuteAsync(Cmd("DELETE FROM mbr_idempotency WHERE expires_at <= @now", new { now }, ct)).ConfigureAwait(false);
        }, ct);

    // ================================================================== IMembershipSettingsDAO

    public Task<MembershipRulesDTO> GetRulesAsync(CancellationToken ct) =>
        RunAsync("settings.rules", async c =>
        {
            DataRow? row = await c.QuerySingleOrDefaultAsync<DataRow>(Cmd("SELECT id, version, data FROM mbr_settings WHERE id = 1", null, ct)).ConfigureAwait(false);
            MembershipRulesDTO rules = row is null ? new MembershipRulesDTO() : MembershipJson.Read<MembershipRulesDTO>(row.Data);
            rules.Version = row?.Version ?? 0;
            return rules;
        }, ct);

    public Task<bool> SaveRulesAsync(MembershipRulesDTO rules, CancellationToken ct) =>
        RunAsync("settings.save_rules", async c =>
        {
            MembershipRulesDTO next = rules.Clone();
            next.Version = rules.Version + 1;
            return await c.ExecuteAsync(Cmd("UPDATE mbr_settings SET data = @Data, version = version + 1 WHERE id = 1 AND version = @Version", new { rules.Version, Data = J(next) }, ct)).ConfigureAwait(false) == 1;
        }, ct);

    public Task<IReadOnlyList<ConsentVersionDTO>> ListConsentVersionsAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<ConsentVersionDTO>>("settings.consents", async c => [.. (await c.QueryAsync<DataRow>(Cmd("SELECT id, data FROM mbr_consent_version ORDER BY id DESC", null, ct)).ConfigureAwait(false))
            .Select(r => R<ConsentVersionDTO>(r, (x, y) => x.Id = y.Id))], ct);

    public Task<long> InsertConsentVersionAsync(ConsentVersionDTO version, CancellationToken ct) =>
        RunAsync("settings.consent_insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_consent_version", "data", "@Data"), new { Data = J(version) }, ct)), ct);

    public Task UpdateConsentVersionAsync(ConsentVersionDTO version, CancellationToken ct) =>
        RunAsync("settings.consent_update", c => c.ExecuteAsync(Cmd("UPDATE mbr_consent_version SET data = @Data WHERE id = @Id", new { version.Id, Data = J(version) }, ct)), ct);

    public Task<IReadOnlyList<IntegrationClientDTO>> ListIntegrationClientsAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<IntegrationClientDTO>>("settings.integrations", async c => [.. (await c.QueryAsync<DataRow>(Cmd("SELECT data FROM mbr_integration_client ORDER BY client_id", null, ct)).ConfigureAwait(false))
            .Select(r => R<IntegrationClientDTO>(r))], ct);

    public Task UpsertIntegrationClientAsync(IntegrationClientDTO client, CancellationToken ct) =>
        RunAsync("settings.integration_upsert", async c =>
        {
            object args = new { client.ClientId, Data = J(client) };
            if (await c.ExecuteAsync(Cmd("UPDATE mbr_integration_client SET data = @Data WHERE client_id = @ClientId", args, ct)).ConfigureAwait(false) == 0)
            {
                await c.ExecuteAsync(Cmd("INSERT INTO mbr_integration_client (client_id, data) VALUES (@ClientId, @Data)", args, ct)).ConfigureAwait(false);
            }

            return true;
        }, ct);

    // ================================================================== IMemberPrivacyDAO

    public Task InsertOtpAsync(OtpChallengeEntity challenge, CancellationToken ct) =>
        RunAsync("otp.insert", c => c.ExecuteAsync(Cmd("INSERT INTO mbr_otp (id, phone_hash, created_at, data) VALUES (@Id, @PhoneHash, @CreatedAt, @Data)",
            new { challenge.Id, challenge.PhoneHash, challenge.CreatedAt, Data = J(challenge) }, ct)), ct);

    public Task<OtpChallengeEntity?> GetOtpAsync(string id, CancellationToken ct) =>
        RunAsync("otp.get", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT data FROM mbr_otp WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(r => R<OtpChallengeEntity>(r)).FirstOrDefault(), ct);

    public Task UpdateOtpAsync(OtpChallengeEntity challenge, CancellationToken ct) =>
        RunAsync("otp.update", c => c.ExecuteAsync(Cmd("UPDATE mbr_otp SET data = @Data WHERE id = @Id", new { challenge.Id, Data = J(challenge) }, ct)), ct);

    public Task<int> CountOtpSinceAsync(string phoneHash, DateTimeOffset since, CancellationToken ct) =>
        RunAsync("otp.count", c => c.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM mbr_otp WHERE phone_hash = @phoneHash AND created_at >= @since", new { phoneHash, since }, ct)), ct);

    private static PrivacyRequestDTO MapPrivacy(DataRow r) => R<PrivacyRequestDTO>(r, (x, y) => x.Id = y.Id);

    public Task<long> InsertPrivacyRequestAsync(PrivacyRequestDTO request, CancellationToken ct) =>
        RunAsync("privacy.insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_privacy_request", "member_id, status, data", "@MemberId, @Status, @Data"),
            new { request.MemberId, Status = request.Status.ToString(), Data = J(request) }, ct)), ct);

    public Task<PrivacyRequestDTO?> GetPrivacyRequestAsync(long id, CancellationToken ct) =>
        RunAsync("privacy.get", async c => (await c.QueryAsync<DataRow>(Cmd("SELECT id, data FROM mbr_privacy_request WHERE id = @id", new { id }, ct)).ConfigureAwait(false)).Select(MapPrivacy).FirstOrDefault(), ct);

    public Task<IReadOnlyList<PrivacyRequestDTO>> ListPrivacyRequestsAsync(long? memberId, PrivacyRequestStatus? status, CancellationToken ct) =>
        RunAsync<IReadOnlyList<PrivacyRequestDTO>>("privacy.list", async c => [.. (await c.QueryAsync<DataRow>(Cmd($"""
            SELECT id, data FROM mbr_privacy_request WHERE (@memberId IS NULL OR member_id = @memberId) AND (@status IS NULL OR status = @status) ORDER BY id DESC {Dialect.Take("1000")}
            """, new { memberId, status = status?.ToString() }, ct)).ConfigureAwait(false)).Select(MapPrivacy)], ct);

    public Task UpdatePrivacyRequestAsync(PrivacyRequestDTO request, CancellationToken ct) =>
        RunAsync("privacy.update", c => c.ExecuteAsync(Cmd("UPDATE mbr_privacy_request SET status = @Status, data = @Data WHERE id = @Id",
            new { request.Id, Status = request.Status.ToString(), Data = J(request) }, ct)), ct);

    // ================================================================== IMembershipJobDAO

    public Task<bool> TryAcquireLeaseAsync(string job, string holder, DateTimeOffset now, TimeSpan duration, CancellationToken ct) =>
        RunAsync("job.lease", async c =>
        {
            object args = new { job, holder, now, until = now + duration };
            if (await c.ExecuteAsync(Cmd("UPDATE mbr_job_lease SET holder = @holder, until_at = @until WHERE job = @job AND (until_at <= @now OR holder = @holder)", args, ct)).ConfigureAwait(false) == 1)
            {
                return true;
            }

            try
            {
                await c.ExecuteAsync(Cmd("INSERT INTO mbr_job_lease (job, holder, until_at) VALUES (@job, @holder, @until)", args, ct)).ConfigureAwait(false);
                return true;
            }
            catch (DbException ex) when (Dialect.IsUniqueViolation(ex))
            {
                return false;
            }
        }, ct);

    /// <summary>แถวงานส่งออก</summary>
    private sealed class ExportRow
    {
        public long Id { get; set; }
        public string Data { get; set; } = "{}";
        public byte[]? Content { get; set; }
    }

    private static ExportJobDTO MapExport(ExportRow r)
    {
        ExportJobDTO x = MembershipJson.Read<ExportJobDTO>(r.Data);
        x.Id = r.Id;
        x.Content = r.Content;
        return x;
    }

    private static object ExportArgs(ExportJobDTO j) => new { j.Id, j.RequestedBy, Status = j.Status.ToString(), j.ExpiresAt, Data = J(j), j.Content };

    public Task<long> InsertExportAsync(ExportJobDTO job, CancellationToken ct) =>
        RunAsync("export.insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_export", "requested_by, status, expires_at, data, content", "@RequestedBy, @Status, @ExpiresAt, @Data, @Content"),
            ExportArgs(job), ct)), ct);

    public Task<ExportJobDTO?> GetExportAsync(long id, bool withContent, CancellationToken ct) =>
        RunAsync("export.get", async c => (await c.QueryAsync<ExportRow>(Cmd($"SELECT id, data{(withContent ? ", content" : string.Empty)} FROM mbr_export WHERE id = @id", new { id }, ct)).ConfigureAwait(false))
            .Select(MapExport).FirstOrDefault(), ct);

    public Task<IReadOnlyList<ExportJobDTO>> ListExportsAsync(string? requestedBy, JobStatus? status, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ExportJobDTO>>("export.list", async c => [.. (await c.QueryAsync<ExportRow>(Cmd($"""
            SELECT id, data FROM mbr_export WHERE (@requestedBy IS NULL OR requested_by = @requestedBy) AND (@status IS NULL OR status = @status) ORDER BY id DESC {Dialect.Take("200")}
            """, new { requestedBy, status = status?.ToString() }, ct)).ConfigureAwait(false)).Select(MapExport)], ct);

    public Task UpdateExportAsync(ExportJobDTO job, CancellationToken ct) =>
        RunAsync("export.update", c => c.ExecuteAsync(Cmd("UPDATE mbr_export SET status = @Status, expires_at = @ExpiresAt, data = @Data, content = COALESCE(@Content, content) WHERE id = @Id",
            ExportArgs(job), ct)), ct);

    private static MessageJobDTO MapMessageJob(DataRow r) => R<MessageJobDTO>(r, (x, y) => x.Id = y.Id);

    public Task<long> InsertMessageJobAsync(MessageJobDTO job, CancellationToken ct) =>
        RunAsync("msgjob.insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_message_job", "status, data", "@Status, @Data"), new { Status = job.Status.ToString(), Data = J(job) }, ct)), ct);

    public Task<IReadOnlyList<MessageJobDTO>> ListMessageJobsAsync(JobStatus? status, CancellationToken ct) =>
        RunAsync<IReadOnlyList<MessageJobDTO>>("msgjob.list", async c => [.. (await c.QueryAsync<DataRow>(Cmd(
            $"SELECT id, data FROM mbr_message_job WHERE (@status IS NULL OR status = @status) ORDER BY id DESC {Dialect.Take("200")}", new { status = status?.ToString() }, ct)).ConfigureAwait(false)).Select(MapMessageJob)], ct);

    public Task UpdateMessageJobAsync(MessageJobDTO job, CancellationToken ct) =>
        RunAsync("msgjob.update", c => c.ExecuteAsync(Cmd("UPDATE mbr_message_job SET status = @Status, data = @Data WHERE id = @Id", new { job.Id, Status = job.Status.ToString(), Data = J(job) }, ct)), ct);
}
