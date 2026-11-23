using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>ห้องสนทนาและข้อความ omnichannel (PostgreSQL + Dapper)</summary>
public sealed class ConversationADO : AdoBase, IConversationDAO
{
    /// <summary>SELECT ห้องพร้อมชื่อลูกค้า</summary>
    private const string Select = """
        SELECT v.id, v.customer_id, v.channel, v.external_user_id, v.subject, v.status, v.assignee_subject, v.last_sentiment, v.needs_attention,
               v.last_message_at, v.first_response_at, v.created_at, c.display_name AS customer_name
        FROM conversation v
        LEFT JOIN customer c ON c.id = v.customer_id
        """;

    /// <summary>สร้าง ADO</summary>
    /// <param name="db">connection factory</param>
    /// <param name="logger">logger</param>
    public ConversationADO(IDbConnectionFactory db, ILogger<ConversationADO> logger) : base(db, logger) { }

    /// <inheritdoc />
    public Task<ConversationDTO?> FindOpenAsync(ChannelKind channel, string externalUserId, CancellationToken ct) =>
        RunAsync("conversation.find_open", c => c.QuerySingleOrDefaultAsync<ConversationDTO>(Cmd(
            $"{Select} WHERE v.channel = @channel AND v.external_user_id = @externalUserId AND v.status <> 3", new { channel = (int)channel, externalUserId }, ct)), ct);

    /// <inheritdoc />
    public Task<long> InsertAsync(ConversationDTO conversation, CancellationToken ct) =>
        RunAsync("conversation.insert", c => c.ExecuteScalarAsync<long>(Cmd("""
            INSERT INTO conversation (customer_id, channel, external_user_id, subject, status, assignee_subject, last_sentiment, needs_attention, last_message_at, first_response_at, created_at)
            VALUES (@CustomerId, @Channel, @ExternalUserId, @Subject, @Status, @AssigneeSubject, @LastSentiment, @NeedsAttention, @LastMessageAt, @FirstResponseAt, @CreatedAt)
            RETURNING id
            """, Params(conversation), ct)), ct);

    /// <inheritdoc />
    public Task UpdateAsync(ConversationDTO conversation, CancellationToken ct) =>
        RunAsync("conversation.update", c => c.ExecuteAsync(Cmd("""
            UPDATE conversation SET customer_id = @CustomerId, subject = @Subject, status = @Status, assignee_subject = @AssigneeSubject, last_sentiment = @LastSentiment,
                needs_attention = @NeedsAttention, last_message_at = @LastMessageAt, first_response_at = @FirstResponseAt
            WHERE id = @Id
            """, Params(conversation), ct)), ct);

    /// <inheritdoc />
    public Task<ConversationDTO?> GetAsync(long id, CancellationToken ct) =>
        RunAsync("conversation.get", c => c.QuerySingleOrDefaultAsync<ConversationDTO>(Cmd($"{Select} WHERE v.id = @id", new { id }, ct)), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationDTO>> ListAsync(ConversationStatus? status, long? customerId, bool needsAttentionOnly, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ConversationDTO>>("conversation.list", async c =>
        {
            // Step 1: ตัวกรองเป็น parameter ทั้งหมด เรียงตามข้อความล่าสุด
            string sql = $"""
                {Select}
                WHERE (@status::int IS NULL OR v.status = @status) AND (@customerId::bigint IS NULL OR v.customer_id = @customerId)
                  AND (@needsAttentionOnly = FALSE OR v.needs_attention)
                ORDER BY v.needs_attention DESC, v.last_message_at DESC
                LIMIT @take
                """;
            return (await c.QueryAsync<ConversationDTO>(Cmd(sql, new { status = (int?)status, customerId, needsAttentionOnly, take = Math.Clamp(take, 1, 1_000) }, ct)).ConfigureAwait(false)).ToList();
        }, ct);

    /// <inheritdoc />
    public Task<long?> InsertMessageAsync(ChatMessageDTO message, CancellationToken ct) =>
        RunAsync("message.insert", c =>
        {
            // Step 1: ON CONFLICT DO NOTHING บน external_message_id → ข้อความซ้ำคืน null (idempotent webhook)
            const string sql = """
                INSERT INTO chat_message (conversation_id, direction, body, external_message_id, sender_subject, sentiment, sentiment_label, sent_at)
                VALUES (@ConversationId, @Direction, @Body, @ExternalMessageId, @SenderSubject, @Sentiment, @SentimentLabel, @SentAt)
                ON CONFLICT (conversation_id, external_message_id) WHERE external_message_id IS NOT NULL DO NOTHING
                RETURNING id
                """;
            return c.ExecuteScalarAsync<long?>(Cmd(sql, new
            {
                message.ConversationId,
                Direction = (int)message.Direction,
                message.Body,
                message.ExternalMessageId,
                message.SenderSubject,
                message.Sentiment,
                SentimentLabel = (int)message.SentimentLabel,
                message.SentAt,
            }, ct));
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessageDTO>> ListMessagesAsync(long conversationId, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ChatMessageDTO>>("message.list", async c =>
        {
            // Step 1: ดึงล่าสุด N ข้อความ แล้วเรียงจากเก่าไปใหม่เพื่อแสดงผล
            const string sql = """
                SELECT * FROM (
                    SELECT id, conversation_id, direction, body, external_message_id, sender_subject, sentiment, sentiment_label, sent_at
                    FROM chat_message WHERE conversation_id = @conversationId ORDER BY sent_at DESC, id DESC LIMIT @take
                ) m ORDER BY sent_at, id
                """;
            return (await c.QueryAsync<ChatMessageDTO>(Cmd(sql, new { conversationId, take = Math.Clamp(take, 1, 1_000) }, ct)).ConfigureAwait(false)).ToList();
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, (double Average, int Count)>> SentimentByCustomerAsync(IReadOnlyCollection<long> customerIds, DateTimeOffset since, CancellationToken ct) =>
        RunAsync<IReadOnlyDictionary<long, (double Average, int Count)>>("message.sentiment", async c =>
        {
            // Step 1: เฉลี่ยอารมณ์ของข้อความขาเข้าต่อลูกค้า (aggregate ในฐานข้อมูล)
            if (customerIds.Count == 0) return new Dictionary<long, (double, int)>();
            const string sql = """
                SELECT v.customer_id AS customer_id, AVG(m.sentiment) AS average, COUNT(*)::int AS count
                FROM chat_message m JOIN conversation v ON v.id = m.conversation_id
                WHERE v.customer_id = ANY(@ids) AND m.direction = 1 AND m.sent_at >= @since
                GROUP BY v.customer_id
                """;
            IEnumerable<SentimentRow> rows = await c.QueryAsync<SentimentRow>(Cmd(sql, new { ids = customerIds.ToArray(), since }, ct)).ConfigureAwait(false);
            return rows.ToDictionary(r => r.CustomerId, r => (r.Average, r.Count));
        }, ct);

    /// <summary>parameter object</summary>
    private static object Params(ConversationDTO x) => new
    {
        x.Id,
        x.CustomerId,
        Channel = (int)x.Channel,
        x.ExternalUserId,
        x.Subject,
        Status = (int)x.Status,
        x.AssigneeSubject,
        x.LastSentiment,
        x.NeedsAttention,
        x.LastMessageAt,
        x.FirstResponseAt,
        x.CreatedAt,
    };

    /// <summary>แถวผลรวมอารมณ์</summary>
    private sealed class SentimentRow
    {
        /// <summary>ลูกค้า</summary>
        public long CustomerId { get; set; }

        /// <summary>ค่าเฉลี่ย</summary>
        public double Average { get; set; }

        /// <summary>จำนวน</summary>
        public int Count { get; set; }
    }
}
