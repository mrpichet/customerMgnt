using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>กิจกรรม/นัดหมาย/งานติดตาม (PostgreSQL + Dapper)</summary>
public sealed class ActivityADO : AdoBase, IActivityDAO
{
    /// <summary>คอลัมน์ทั้งหมด</summary>
    private const string Columns = """
        id, customer_id, opportunity_id, kind, subject, notes, status, owner_subject, due_at, duration_minutes, remind_before_minutes,
        reminder_sent_at, completed_at, outcome, message_hub_meeting_id, created_at
        """;

    /// <summary>สร้าง ADO</summary>
    /// <param name="db">connection factory</param>
    /// <param name="logger">logger</param>
    public ActivityADO(IDbConnectionFactory db, ILogger<ActivityADO> logger) : base(db, logger) { }

    /// <inheritdoc />
    public Task<long> InsertAsync(ActivityDTO activity, CancellationToken ct) =>
        RunAsync("activity.insert", c => c.ExecuteScalarAsync<long>(Cmd("""
            INSERT INTO activity (customer_id, opportunity_id, kind, subject, notes, status, owner_subject, due_at, duration_minutes, remind_before_minutes,
                reminder_sent_at, completed_at, outcome, message_hub_meeting_id, created_at)
            VALUES (@CustomerId, @OpportunityId, @Kind, @Subject, @Notes, @Status, @OwnerSubject, @DueAt, @DurationMinutes, @RemindBeforeMinutes,
                @ReminderSentAt, @CompletedAt, @Outcome, @MessageHubMeetingId, @CreatedAt)
            RETURNING id
            """, Params(activity), ct)), ct);

    /// <inheritdoc />
    public Task UpdateAsync(ActivityDTO activity, CancellationToken ct) =>
        RunAsync("activity.update", c => c.ExecuteAsync(Cmd("""
            UPDATE activity SET subject = @Subject, notes = @Notes, status = @Status, owner_subject = @OwnerSubject, due_at = @DueAt, duration_minutes = @DurationMinutes,
                remind_before_minutes = @RemindBeforeMinutes, reminder_sent_at = @ReminderSentAt, completed_at = @CompletedAt, outcome = @Outcome,
                message_hub_meeting_id = @MessageHubMeetingId
            WHERE id = @Id
            """, Params(activity), ct)), ct);

    /// <inheritdoc />
    public Task<ActivityDTO?> GetAsync(long id, CancellationToken ct) =>
        RunAsync("activity.get", c => c.QuerySingleOrDefaultAsync<ActivityDTO>(Cmd($"SELECT {Columns} FROM activity WHERE id = @id", new { id }, ct)), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ActivityDTO>> ListByCustomerAsync(long customerId, bool openOnly, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ActivityDTO>>("activity.list_customer", async c =>
            (await c.QueryAsync<ActivityDTO>(Cmd(
                $"SELECT {Columns} FROM activity WHERE customer_id = @customerId AND (@openOnly = FALSE OR status = 1) ORDER BY due_at DESC LIMIT @take",
                new { customerId, openOnly, take = Math.Clamp(take, 1, 1_000) }, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ActivityDTO>> ListOpenByOwnerAsync(string ownerSubject, DateTimeOffset until, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ActivityDTO>>("activity.list_owner", async c =>
            (await c.QueryAsync<ActivityDTO>(Cmd(
                $"SELECT {Columns} FROM activity WHERE owner_subject = @ownerSubject AND status = 1 AND due_at <= @until ORDER BY due_at LIMIT 1000",
                new { ownerSubject, until }, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ActivityDTO>> ListDueRemindersAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ActivityDTO>>("activity.due_reminders", async c =>
        {
            // Step 1: งานที่ยังไม่เสร็จ ยังไม่เตือน และถึงเวลาเตือนแล้ว (due_at - remind_before ≤ now) ใช้ partial index ix_activity_reminder
            const string sql = $"""
                SELECT {Columns} FROM activity
                WHERE status = 1 AND reminder_sent_at IS NULL AND remind_before_minutes IS NOT NULL
                  AND due_at - make_interval(mins => remind_before_minutes) <= @now AND due_at > @now - INTERVAL '1 day'
                ORDER BY due_at LIMIT @take
                """;
            return (await c.QueryAsync<ActivityDTO>(Cmd(sql, new { now, take = Math.Clamp(take, 1, 5_000) }, ct)).ConfigureAwait(false)).ToList();
        }, ct);

    /// <inheritdoc />
    public Task<bool> TryMarkReminderSentAsync(long id, DateTimeOffset sentAt, CancellationToken ct) =>
        RunAsync("activity.mark_reminder", async c =>
            await c.ExecuteAsync(Cmd("UPDATE activity SET reminder_sent_at = @sentAt WHERE id = @id AND reminder_sent_at IS NULL", new { id, sentAt }, ct)).ConfigureAwait(false) == 1, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, (DateTimeOffset? LastContactAt, int Done90Days)>> ContactStatsAsync(IReadOnlyCollection<long> customerIds, CancellationToken ct) =>
        RunAsync<IReadOnlyDictionary<long, (DateTimeOffset? LastContactAt, int Done90Days)>>("activity.contact_stats", async c =>
        {
            // Step 1: ติดต่อล่าสุด + จำนวนกิจกรรมที่ทำเสร็จใน 90 วัน ต่อลูกค้า
            if (customerIds.Count == 0) return new Dictionary<long, (DateTimeOffset?, int)>();
            const string sql = """
                SELECT customer_id, MAX(completed_at) AS last_contact_at,
                       COUNT(*) FILTER (WHERE completed_at >= now() - INTERVAL '90 days')::int AS done90_days
                FROM activity WHERE customer_id = ANY(@ids) AND status = 2
                GROUP BY customer_id
                """;
            IEnumerable<ContactRow> rows = await c.QueryAsync<ContactRow>(Cmd(sql, new { ids = customerIds.ToArray() }, ct)).ConfigureAwait(false);
            return rows.ToDictionary(r => r.CustomerId, r => (r.LastContactAt, r.Done90Days));
        }, ct);

    /// <summary>parameter object</summary>
    private static object Params(ActivityDTO x) => new
    {
        x.Id,
        x.CustomerId,
        x.OpportunityId,
        Kind = (int)x.Kind,
        x.Subject,
        x.Notes,
        Status = (int)x.Status,
        x.OwnerSubject,
        x.DueAt,
        x.DurationMinutes,
        x.RemindBeforeMinutes,
        x.ReminderSentAt,
        x.CompletedAt,
        x.Outcome,
        x.MessageHubMeetingId,
        x.CreatedAt,
    };

    /// <summary>แถวสถิติการติดต่อ</summary>
    private sealed class ContactRow
    {
        /// <summary>ลูกค้า</summary>
        public long CustomerId { get; set; }

        /// <summary>ติดต่อล่าสุด</summary>
        public DateTimeOffset? LastContactAt { get; set; }

        /// <summary>จำนวนใน 90 วัน</summary>
        public int Done90Days { get; set; }
    }
}
