using System.Data.Common;
using Common.Data.Infrastructure;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;
using CustomerMgnt.Data.Infrastructure;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.MemberAnalytics;

/// <summary>
/// Data access ของระบบวิเคราะห์ (PostgreSQL / SQL Server + Dapper) — ตาราง <c>ana_*</c>
/// read model เก็บคอลัมน์ที่ใช้กรอง/รวมยอด + JSON ใน <c>data</c>; เอกสารตรวจ version ตอนแก้
/// </summary>
public sealed class AnaADO : AdoBase, IAnaReadModelDAO, IAnaDocumentDAO, IAnaMessageDAO
{
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public AnaADO(IDbConnectionFactory db, ILogger<AnaADO> logger, TimeProvider clock) : base(db, logger) => _clock = clock;

    private static string J<T>(T value) => MembershipJson.Write(value);

    private bool IsUnique(DbException ex) => Dialect.IsUniqueViolation(ex);

    private static DateTime D(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    private Task RunTxAsync(string op, Func<DbConnection, DbTransaction, Task> work, CancellationToken ct) =>
        RunAsync(op, async c =>
        {
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            await work(c, tx).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, ct);

    private sealed class DataOnly
    {
        public string Data { get; set; } = "{}";
    }

    // ================================================================== read model

    public Task ReplaceMemberDaysAsync(DateOnly day, IReadOnlyList<AnaMemberDayDTO> rows, CancellationToken ct) =>
        RunTxAsync("ana.member_daily.replace", async (c, tx) =>
        {
            await c.ExecuteAsync(Cmd("DELETE FROM ana_member_daily WHERE day = @day", new { day = D(day) }, ct, tx)).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                await c.ExecuteAsync(Cmd("INSERT INTO ana_member_daily (day, member_id, branch_code, channel, net, bills, data) VALUES (@Day, @MemberId, @BranchCode, @Channel, @Net, @Bills, @Data)",
                    rows.Select(r => new { Day = D(day), r.MemberId, r.BranchCode, Channel = r.Channel.ToString(), r.Net, r.Bills, Data = J(r) }), ct, tx)).ConfigureAwait(false);
            }
        }, ct);

    public Task<IReadOnlyList<AnaMemberDayDTO>> ListMemberDaysAsync(DateOnly from, DateOnly to, IReadOnlyCollection<string>? branches, IReadOnlyCollection<SalesChannel>? channels,
        CancellationToken ct) =>
        RunAsync<IReadOnlyList<AnaMemberDayDTO>>("ana.member_daily.list", async c =>
        {
            var p = new DynamicParameters();
            p.Add("from", D(from));
            p.Add("to", D(to));
            var w = new List<string> { "day >= @from", "day <= @to" };
            if (branches is not null)
            {
                if (branches.Count == 0)
                {
                    return [];
                }

                w.Add(Dialect.InList("UPPER(branch_code)", "branches"));
                p.Add("branches", branches.Select(b => b.ToUpperInvariant()).Distinct().ToArray());
            }

            if (channels is not null)
            {
                if (channels.Count == 0)
                {
                    return [];
                }

                w.Add(Dialect.InList("channel", "channels"));
                p.Add("channels", channels.Select(x => x.ToString()).Distinct().ToArray());
            }

            IEnumerable<DataOnly> rows = await c.QueryAsync<DataOnly>(Cmd($"SELECT data FROM ana_member_daily WHERE {string.Join(" AND ", w)} ORDER BY day, member_id", p, ct)).ConfigureAwait(false);
            return [.. rows.Select(r => MembershipJson.Read<AnaMemberDayDTO>(r.Data))];
        }, ct);

    public Task ReplaceSnapshotsAsync(IReadOnlyList<AnaMemberSnapshotDTO> rows, CancellationToken ct) =>
        RunTxAsync("ana.snapshot.replace", async (c, tx) =>
        {
            await c.ExecuteAsync(Cmd("DELETE FROM ana_member_snapshot", null, ct, tx)).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                await c.ExecuteAsync(Cmd("INSERT INTO ana_member_snapshot (member_id, refreshed_at, data) VALUES (@MemberId, @RefreshedAt, @Data)",
                    rows.Select(r => new { r.MemberId, r.RefreshedAt, Data = J(r) }), ct, tx)).ConfigureAwait(false);
            }
        }, ct);

    public Task<IReadOnlyList<AnaMemberSnapshotDTO>> ListSnapshotsAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<AnaMemberSnapshotDTO>>("ana.snapshot.list", async c =>
            [.. (await c.QueryAsync<DataOnly>(Cmd("SELECT data FROM ana_member_snapshot ORDER BY member_id", null, ct)).ConfigureAwait(false))
                .Select(r => MembershipJson.Read<AnaMemberSnapshotDTO>(r.Data))], ct);

    private sealed class PointsRow
    {
        public DateTime Day { get; set; }
        public string Type { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
        public long Points { get; set; }
        public int Entries { get; set; }
    }

    public Task ReplacePointsDayAsync(DateOnly day, IReadOnlyList<AnaPointsDayDTO> rows, CancellationToken ct) =>
        RunTxAsync("ana.points_daily.replace", async (c, tx) =>
        {
            await c.ExecuteAsync(Cmd("DELETE FROM ana_points_daily WHERE day = @day", new { day = D(day) }, ct, tx)).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                await c.ExecuteAsync(Cmd("INSERT INTO ana_points_daily (day, type, branch_code, points, entries) VALUES (@Day, @Type, @BranchCode, @Points, @Entries)",
                    rows.Select(r => new { Day = D(day), Type = r.Type.ToString(), r.BranchCode, r.Points, r.Entries }), ct, tx)).ConfigureAwait(false);
            }
        }, ct);

    public Task<IReadOnlyList<AnaPointsDayDTO>> ListPointsDaysAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        RunAsync<IReadOnlyList<AnaPointsDayDTO>>("ana.points_daily.list", async c =>
            [.. (await c.QueryAsync<PointsRow>(Cmd("SELECT day AS Day, type AS Type, branch_code AS BranchCode, points AS Points, entries AS Entries FROM ana_points_daily WHERE day >= @from AND day <= @to ORDER BY day",
                new { from = D(from), to = D(to) }, ct)).ConfigureAwait(false))
                .Select(r => new AnaPointsDayDTO
                {
                    Day = DateOnly.FromDateTime(r.Day), Type = Enum.Parse<LedgerType>(r.Type), BranchCode = r.BranchCode, Points = r.Points, Entries = r.Entries,
                })], ct);

    private sealed class CouponRow
    {
        public DateTime Day { get; set; }
        public string CampaignCode { get; set; } = string.Empty;
        public long BatchId { get; set; }
        public string Ev { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
        public int Cnt { get; set; }
    }

    public Task ReplaceCouponDayAsync(DateOnly day, IReadOnlyList<AnaCouponDayDTO> rows, CancellationToken ct) =>
        RunTxAsync("ana.coupon_daily.replace", async (c, tx) =>
        {
            await c.ExecuteAsync(Cmd("DELETE FROM ana_coupon_daily WHERE day = @day", new { day = D(day) }, ct, tx)).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                await c.ExecuteAsync(Cmd("INSERT INTO ana_coupon_daily (day, campaign_code, batch_id, event, branch_code, cnt) VALUES (@Day, @CampaignCode, @BatchId, @Ev, @BranchCode, @Count)",
                    rows.Select(r => new { Day = D(day), r.CampaignCode, r.BatchId, Ev = r.Event.ToString(), r.BranchCode, r.Count }), ct, tx)).ConfigureAwait(false);
            }
        }, ct);

    public Task<IReadOnlyList<AnaCouponDayDTO>> ListCouponDaysAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        RunAsync<IReadOnlyList<AnaCouponDayDTO>>("ana.coupon_daily.list", async c =>
            [.. (await c.QueryAsync<CouponRow>(Cmd("SELECT day AS Day, campaign_code AS CampaignCode, batch_id AS BatchId, event AS Ev, branch_code AS BranchCode, cnt AS Cnt FROM ana_coupon_daily WHERE day >= @from AND day <= @to ORDER BY day",
                new { from = D(from), to = D(to) }, ct)).ConfigureAwait(false))
                .Select(r => new AnaCouponDayDTO
                {
                    Day = DateOnly.FromDateTime(r.Day), CampaignCode = r.CampaignCode, BatchId = r.BatchId, Event = Enum.Parse<CouponMetricEvent>(r.Ev), BranchCode = r.BranchCode, Count = r.Cnt,
                })], ct);

    public Task MarkDirtyAsync(string table, IReadOnlyCollection<DateOnly> days, CancellationToken ct)
    {
        if (days.Count == 0)
        {
            return Task.CompletedTask;
        }

        DateTimeOffset now = _clock.GetUtcNow();
        string sql = Dialect.IsSqlServer()
            ? """
              UPDATE ana_dirty_day WITH (UPDLOCK, SERIALIZABLE) SET marked_at = @now WHERE tbl = @tbl AND day = @day;
              IF @@ROWCOUNT = 0 INSERT INTO ana_dirty_day (tbl, day, marked_at) VALUES (@tbl, @day, @now);
              """
            : "INSERT INTO ana_dirty_day (tbl, day, marked_at) VALUES (@tbl, @day, @now) ON CONFLICT (tbl, day) DO UPDATE SET marked_at = EXCLUDED.marked_at";
        return RunTxAsync("ana.dirty.mark", (c, tx) =>
            c.ExecuteAsync(Cmd(sql, days.Distinct().Select(d => new { tbl = table, day = D(d), now }), ct, tx)), ct);
    }

    public Task<IReadOnlyList<DateOnly>> ListDirtyAsync(string table, CancellationToken ct) =>
        RunAsync<IReadOnlyList<DateOnly>>("ana.dirty.list", async c =>
            [.. (await c.QueryAsync<DateTime>(Cmd("SELECT day FROM ana_dirty_day WHERE tbl = @table ORDER BY day", new { table }, ct)).ConfigureAwait(false)).Select(DateOnly.FromDateTime)], ct);

    public Task ClearDirtyAsync(string table, IReadOnlyCollection<DateOnly> days, DateTimeOffset markedBefore, CancellationToken ct)
    {
        if (days.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunTxAsync("ana.dirty.clear", (c, tx) =>
            c.ExecuteAsync(Cmd("DELETE FROM ana_dirty_day WHERE tbl = @tbl AND day = @day AND marked_at < @before",
                days.Distinct().Select(d => new { tbl = table, day = D(d), before = markedBefore }), ct, tx)), ct);
    }

    // ================================================================== เอกสาร

    private sealed class DocRow
    {
        public string Id { get; set; } = string.Empty;
        public string? Owner { get; set; }
        public long Version { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string Data { get; set; } = "{}";
    }

    private const string DocSelect = "id AS Id, owner AS Owner, version AS Version, updated_at AS UpdatedAt, data AS Data";

    private static AnaDocument<T> MapDoc<T>(DocRow r) where T : new() => new(r.Id, r.Owner, r.Version, r.UpdatedAt, MembershipJson.Read<T>(r.Data));

    public Task<AnaDocument<T>?> GetAsync<T>(string collection, string id, CancellationToken ct) where T : new() =>
        RunAsync("ana.doc.get", async c =>
        {
            DocRow? r = await c.QueryFirstOrDefaultAsync<DocRow>(Cmd($"SELECT {DocSelect} FROM ana_document WHERE collection = @collection AND id = @id", new { collection, id }, ct)).ConfigureAwait(false);
            return r is null ? null : MapDoc<T>(r);
        }, ct);

    public Task<IReadOnlyList<AnaDocument<T>>> ListAsync<T>(string collection, string? owner, CancellationToken ct) where T : new() =>
        RunAsync<IReadOnlyList<AnaDocument<T>>>("ana.doc.list", async c =>
            [.. (await c.QueryAsync<DocRow>(Cmd($"SELECT {DocSelect} FROM ana_document WHERE collection = @collection{(owner is null ? string.Empty : " AND owner = @owner")} ORDER BY updated_at DESC",
                new { collection, owner }, ct)).ConfigureAwait(false)).Select(MapDoc<T>)], ct);

    public Task<bool> InsertAsync<T>(string collection, string id, string? owner, T value, DateTimeOffset now, CancellationToken ct) =>
        RunAsync("ana.doc.insert", async c =>
        {
            try
            {
                await c.ExecuteAsync(Cmd("INSERT INTO ana_document (collection, id, owner, version, updated_at, data) VALUES (@collection, @id, @owner, 1, @now, @data)",
                    new { collection, id, owner, now, data = J(value) }, ct)).ConfigureAwait(false);
                return true;
            }
            catch (DbException ex) when (IsUnique(ex))
            {
                return false;
            }
        }, ct);

    public Task<bool> UpdateAsync<T>(string collection, string id, string? owner, long expectedVersion, T value, DateTimeOffset now, CancellationToken ct) =>
        RunAsync("ana.doc.update", async c =>
            await c.ExecuteAsync(Cmd("UPDATE ana_document SET owner = @owner, version = version + 1, updated_at = @now, data = @data WHERE collection = @collection AND id = @id AND version = @expectedVersion",
                new { collection, id, owner, expectedVersion, now, data = J(value) }, ct)).ConfigureAwait(false) == 1, ct);

    public Task UpsertAsync<T>(string collection, string id, string? owner, T value, DateTimeOffset now, CancellationToken ct)
    {
        string sql = Dialect.IsSqlServer()
            ? """
              UPDATE ana_document WITH (UPDLOCK, SERIALIZABLE) SET owner = @owner, version = version + 1, updated_at = @now, data = @data WHERE collection = @collection AND id = @id;
              IF @@ROWCOUNT = 0 INSERT INTO ana_document (collection, id, owner, version, updated_at, data) VALUES (@collection, @id, @owner, 1, @now, @data);
              """
            : """
              INSERT INTO ana_document (collection, id, owner, version, updated_at, data) VALUES (@collection, @id, @owner, 1, @now, @data)
              ON CONFLICT (collection, id) DO UPDATE SET owner = EXCLUDED.owner, version = ana_document.version + 1, updated_at = EXCLUDED.updated_at, data = EXCLUDED.data
              """;
        return RunAsync("ana.doc.upsert", c => c.ExecuteAsync(Cmd(sql, new { collection, id, owner, now, data = J(value) }, ct)), ct);
    }

    public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct) =>
        RunAsync("ana.doc.delete", async c =>
            await c.ExecuteAsync(Cmd("DELETE FROM ana_document WHERE collection = @collection AND id = @id", new { collection, id }, ct)).ConfigureAwait(false) > 0, ct);

    public Task<int> PurgeAsync(string collection, DateTimeOffset before, CancellationToken ct) =>
        RunAsync("ana.doc.purge", c => c.ExecuteAsync(Cmd("DELETE FROM ana_document WHERE collection = @collection AND updated_at < @before", new { collection, before }, ct)), ct);

    // ================================================================== ข้อความ

    public Task InsertRecipientsAsync(IReadOnlyList<MessageRecipientDTO> recipients, CancellationToken ct)
    {
        if (recipients.Count == 0)
        {
            return Task.CompletedTask;
        }

        string sql = Dialect.IsSqlServer()
            ? """
              IF NOT EXISTS (SELECT 1 FROM ana_message_recipient WITH (UPDLOCK, HOLDLOCK) WHERE message_id = @MessageId)
                  INSERT INTO ana_message_recipient (message_id, campaign_code, member_id, sent_at) VALUES (@MessageId, @CampaignCode, @MemberId, @SentAt);
              """
            : "INSERT INTO ana_message_recipient (message_id, campaign_code, member_id, sent_at) VALUES (@MessageId, @CampaignCode, @MemberId, @SentAt) ON CONFLICT (message_id) DO NOTHING";
        return RunTxAsync("ana.recipient.insert", (c, tx) =>
            c.ExecuteAsync(Cmd(sql, recipients.DistinctBy(r => r.MessageId, StringComparer.Ordinal).Select(r => new { r.MessageId, r.CampaignCode, r.MemberId, r.SentAt }), ct, tx)), ct);
    }

    private const string RecipientSelect = "message_id AS MessageId, campaign_code AS CampaignCode, member_id AS MemberId, sent_at AS SentAt";

    public Task<MessageRecipientDTO?> GetRecipientAsync(string messageId, CancellationToken ct) =>
        RunAsync("ana.recipient.get", c =>
            c.QueryFirstOrDefaultAsync<MessageRecipientDTO?>(Cmd($"SELECT {RecipientSelect} FROM ana_message_recipient WHERE message_id = @messageId", new { messageId }, ct)), ct);

    public Task<IReadOnlyList<MessageRecipientDTO>> ListRecipientsAsync(string? campaignCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        RunAsync<IReadOnlyList<MessageRecipientDTO>>("ana.recipient.list", async c =>
            [.. await c.QueryAsync<MessageRecipientDTO>(Cmd(
                $"SELECT {RecipientSelect} FROM ana_message_recipient WHERE sent_at >= @from AND sent_at < @to{(campaignCode is null ? string.Empty : " AND UPPER(campaign_code) = @code")}",
                new { from, to, code = campaignCode?.ToUpperInvariant() }, ct)).ConfigureAwait(false)], ct);

    public Task<bool> TryInsertEventAsync(MessageEventDTO e, CancellationToken ct) =>
        RunAsync("ana.message_event.insert", async c =>
        {
            try
            {
                await c.ExecuteAsync(Cmd("INSERT INTO ana_message_event (event_id, type, message_id, campaign_code, member_id, occurred_at) VALUES (@EventId, @Type, @MessageId, @CampaignCode, @MemberId, @OccurredAt)",
                    new { EventId = e.EventId.ToString("D"), Type = e.Type.ToString(), e.MessageId, e.CampaignCode, e.MemberId, e.OccurredAt }, ct)).ConfigureAwait(false);
                return true;
            }
            catch (DbException ex) when (IsUnique(ex))
            {
                return false;
            }
        }, ct);

    private sealed class EventRow
    {
        public string EventId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string? MessageId { get; set; }
        public string? CampaignCode { get; set; }
        public long MemberId { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
    }

    public Task<IReadOnlyList<MessageEventDTO>> ListEventsAsync(string? campaignCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        RunAsync<IReadOnlyList<MessageEventDTO>>("ana.message_event.list", async c =>
            [.. (await c.QueryAsync<EventRow>(Cmd(
                "SELECT event_id AS EventId, type AS Type, message_id AS MessageId, campaign_code AS CampaignCode, member_id AS MemberId, occurred_at AS OccurredAt FROM ana_message_event "
                + $"WHERE occurred_at >= @from AND occurred_at < @to{(campaignCode is null ? string.Empty : " AND UPPER(campaign_code) = @code")} ORDER BY occurred_at",
                new { from, to, code = campaignCode?.ToUpperInvariant() }, ct)).ConfigureAwait(false))
                .Select(r => new MessageEventDTO
                {
                    EventId = Guid.Parse(r.EventId), Type = Enum.Parse<MessageEventType>(r.Type), MessageId = r.MessageId, CampaignCode = r.CampaignCode, MemberId = r.MemberId, OccurredAt = r.OccurredAt,
                })], ct);
}
