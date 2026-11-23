using Common.Core.Domain;
using Common.Core.Exceptions;
using Common.Core.Security;
using CustomerMgnt.Core.Analytics;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Integration;
using CustomerMgnt.Core.Security;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// Chat Center / Omnichannel: รวมข้อความจาก LINE OA, อีเมล, Facebook, Instagram, Web chat, รีวิว ไว้ใน inbox เดียว
/// <list type="bullet">
/// <item>จับคู่ผู้ส่งกับลูกค้าจากตัวตนในช่องทาง (ChannelIdentity)</item>
/// <item>วิเคราะห์อารมณ์ทุกข้อความขาเข้า และแจ้งทีมทันทีเมื่อลูกค้าไม่พอใจ (Sentiment alert)</item>
/// <item>ตอบกลับผ่าน MessageHub ช่องทางเดิม เพื่อให้ทีมดูแลต่อเนื่อง และวัด first response time</item>
/// </list>
/// </summary>
public sealed class OmnichannelService
{
    private readonly IConversationDAO _conversations;
    private readonly IRelationshipDAO _identities;
    private readonly ICustomerDAO _customers;
    private readonly IEngagementDAO _engagement;
    private readonly IMessageHubGateway _hub;
    private readonly SentimentAnalysisService _sentiment;
    private readonly NotificationQueue _notifications;
    private readonly TimeProvider _clock;
    private readonly ILogger<OmnichannelService> _logger;

    /// <summary>สร้างบริการ</summary>
    /// <param name="conversations">DAO แชต</param>
    /// <param name="identities">DAO ตัวตนในช่องทาง</param>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="engagement">DAO พฤติกรรม (บันทึก complaint)</param>
    /// <param name="hub">MessageHub</param>
    /// <param name="sentiment">วิเคราะห์อารมณ์</param>
    /// <param name="notifications">คิวแจ้งเตือน</param>
    /// <param name="clock">นาฬิกา</param>
    /// <param name="logger">logger</param>
    public OmnichannelService(IConversationDAO conversations, IRelationshipDAO identities, ICustomerDAO customers, IEngagementDAO engagement, IMessageHubGateway hub,
        SentimentAnalysisService sentiment, NotificationQueue notifications, TimeProvider clock, ILogger<OmnichannelService> logger)
    {
        _conversations = conversations;
        _identities = identities;
        _customers = customers;
        _engagement = engagement;
        _hub = hub;
        _sentiment = sentiment;
        _notifications = notifications;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// รับข้อความขาเข้า (เรียกจาก webhook ที่ตรวจลายเซ็นแล้ว) — idempotent ด้วย ExternalMessageId
    /// </summary>
    /// <param name="message">ข้อความที่ normalize แล้ว</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>conversation ที่ข้อความถูกเพิ่มเข้าไป</returns>
    public async Task<ConversationDTO> ReceiveAsync(InboundMessageRequest message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Step 1: ทำความสะอาดข้อมูลจากภายนอก (ไม่เชื่อ input)
        string externalUserId = CustomerService.NormalizeExternalId(message.Channel, message.ExternalUserId);
        string body = InputGuard.Required(InputGuard.StripControlCharacters(message.Body), "body", 20_000);
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset sentAt = message.SentAt is { } s && s <= now.AddMinutes(5) ? s : now;

        // Step 2: จับคู่ลูกค้าจากตัวตนในช่องทาง
        ChannelIdentityDTO? identity = await _identities.FindIdentityAsync(message.Channel, externalUserId, ct).ConfigureAwait(false);

        // Step 3: หา conversation ที่ยังเปิด หรือสร้างใหม่
        ConversationDTO conv = await _conversations.FindOpenAsync(message.Channel, externalUserId, ct).ConfigureAwait(false)
            ?? new ConversationDTO
            {
                CustomerId = identity?.CustomerId,
                Channel = message.Channel,
                ExternalUserId = externalUserId,
                Subject = Truncate(InputGuard.StripControlCharacters(message.Subject) is { Length: > 0 } subj ? subj : body, 300),
                Status = ConversationStatus.Open,
                CreatedAt = now,
                LastMessageAt = sentAt,
            };
        if (conv.Id == 0)
        {
            conv.Id = await _conversations.InsertAsync(conv, ct).ConfigureAwait(false);
        }

        // Step 4: วิเคราะห์อารมณ์แล้วบันทึกข้อความ (ซ้ำ = webhook retry → คืนผลเดิม)
        SentimentDTO sentiment = _sentiment.Analyze(body);
        long? messageId = await _conversations.InsertMessageAsync(new ChatMessageDTO
        {
            ConversationId = conv.Id,
            Direction = MessageDirection.Inbound,
            Body = body,
            ExternalMessageId = message.ExternalMessageId,
            Sentiment = sentiment.Score,
            SentimentLabel = sentiment.Label,
            SentAt = sentAt,
        }, ct).ConfigureAwait(false);
        if (messageId is null)
        {
            return conv;
        }

        // Step 5: อัปเดตสถานะ conversation (ลูกค้าตอบ → กลับเป็น Open)
        bool wasAttention = conv.NeedsAttention;
        conv.Status = ConversationStatus.Open;
        conv.LastMessageAt = sentAt;
        conv.LastSentiment = sentiment.Score;
        conv.NeedsAttention = conv.NeedsAttention || sentiment.NeedsAttention;
        conv.CustomerId ??= identity?.CustomerId;
        await _conversations.UpdateAsync(conv, ct).ConfigureAwait(false);

        // Step 6: ลูกค้าไม่พอใจ → บันทึกเป็น complaint (feature ของ churn) และแจ้งทีม (ครั้งแรกของ conversation)
        if (sentiment.NeedsAttention && conv.CustomerId is { } customerId)
        {
            await _engagement.InsertManyAsync([new EngagementEventDTO { CustomerId = customerId, Kind = EngagementKind.Complaint, Channel = message.Channel, OccurredAt = sentAt, Value = (decimal)sentiment.Score }], ct).ConfigureAwait(false);
        }

        if (sentiment.NeedsAttention && !wasAttention)
        {
            string who = conv.CustomerId is { } cid && await _customers.GetAsync(cid, ct).ConfigureAwait(false) is { } c ? $"{c.DisplayName} ({c.Code})" : message.DisplayName ?? externalUserId;
            _notifications.Enqueue(new StaffNotification(conv.AssigneeSubject ?? (identity is null ? null : (await _customers.GetAsync(identity.CustomerId, ct).ConfigureAwait(false))?.OwnerSubject),
                $"[ด่วน] ลูกค้าไม่พอใจ: {who}",
                $"ช่องทาง {message.Channel} คะแนนอารมณ์ {sentiment.Score:0.00} สัญญาณ: {string.Join(", ", sentiment.Signals)}\nข้อความ: {Truncate(body, 500)}\nConversation #{conv.Id}"));
            _logger.LogInformation("Negative sentiment alert for conversation {ConversationId}", conv.Id);
        }

        return conv;
    }

    /// <summary>ตอบลูกค้า (ส่งผ่าน MessageHub ช่องทางเดิม)</summary>
    /// <param name="user">ผู้เรียก (chat.reply)</param>
    /// <param name="conversationId">conversation</param>
    /// <param name="request">ข้อความ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อความที่บันทึก</returns>
    /// <exception cref="ValidationException">ช่องทางนี้ส่งออกไม่ได้</exception>
    public async Task<ChatMessageDTO> ReplyAsync(UserContextDTO user, long conversationId, ReplyRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + conversation
        AccessGuard.Require(user, Permissions.ChatReply);
        ArgumentNullException.ThrowIfNull(request);
        ConversationDTO conv = await _conversations.GetAsync(conversationId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Conversation not found.");
        string body = InputGuard.Required(InputGuard.StripControlCharacters(request.Body), "body", 10_000);

        // Step 2: ส่งออกผ่าน MessageHub (ช่องทางที่ไม่รองรับ เช่น รีวิว → ต้องตอบที่แพลตฟอร์มนั้นเอง)
        bool sent = await _hub.SendToCustomerAsync(conv.Channel, conv.ExternalUserId, conv.Subject is null ? null : "Re: " + conv.Subject, body, ct).ConfigureAwait(false);
        ValidationException.ThrowIf(!sent, "channel", $"Replies through {conv.Channel} are not supported by MessageHub. Reply on the original platform.");

        // Step 3: บันทึกข้อความขาออก
        DateTimeOffset now = _clock.GetUtcNow();
        var msg = new ChatMessageDTO { ConversationId = conv.Id, Direction = MessageDirection.Outbound, Body = body, SenderSubject = user.Subject, SentAt = now };
        msg.Id = await _conversations.InsertMessageAsync(msg, ct).ConfigureAwait(false) ?? 0;

        // Step 4: first response time, ผู้รับผิดชอบ, สถานะ
        conv.FirstResponseAt ??= now;
        conv.AssigneeSubject ??= user.Subject;
        conv.Status = request.Resolve ? ConversationStatus.Resolved : ConversationStatus.Pending;
        conv.NeedsAttention = !request.Resolve && conv.NeedsAttention;
        await _conversations.UpdateAsync(conv, ct).ConfigureAwait(false);
        return msg;
    }

    /// <summary>มอบหมาย/ผูกลูกค้า/ปิด conversation</summary>
    /// <param name="user">ผู้เรียก (chat.reply)</param>
    /// <param name="conversationId">conversation</param>
    /// <param name="assigneeSubject">ผู้รับผิดชอบใหม่ (null = ไม่เปลี่ยน)</param>
    /// <param name="customerId">ผูกกับลูกค้า (null = ไม่เปลี่ยน) — ผูกตัวตนในช่องทางให้ด้วยเพื่อจับคู่อัตโนมัติครั้งต่อไป</param>
    /// <param name="status">สถานะใหม่ (null = ไม่เปลี่ยน)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>conversation หลังแก้ไข</returns>
    public async Task<ConversationDTO> UpdateAsync(UserContextDTO user, long conversationId, string? assigneeSubject, long? customerId, ConversationStatus? status, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + conversation
        AccessGuard.Require(user, Permissions.ChatReply);
        ConversationDTO conv = await _conversations.GetAsync(conversationId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Conversation not found.");

        // Step 2: ผูกลูกค้า + ตัวตนในช่องทาง
        if (customerId is { } cid)
        {
            _ = await _customers.GetAsync(cid, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
            conv.CustomerId = cid;
            if (await _identities.FindIdentityAsync(conv.Channel, conv.ExternalUserId, ct).ConfigureAwait(false) is null)
            {
                await _identities.InsertIdentityAsync(new ChannelIdentityDTO { CustomerId = cid, Channel = conv.Channel, ExternalId = conv.ExternalUserId, Verified = true }, ct).ConfigureAwait(false);
            }
        }

        // Step 3: ผู้รับผิดชอบ/สถานะ
        if (assigneeSubject is not null) conv.AssigneeSubject = InputGuard.Required(assigneeSubject, "assigneeSubject", 128);
        if (status is not null)
        {
            conv.Status = status.Value;
            conv.NeedsAttention = status != ConversationStatus.Resolved && conv.NeedsAttention;
        }

        await _conversations.UpdateAsync(conv, ct).ConfigureAwait(false);
        return conv;
    }

    /// <summary>inbox</summary>
    /// <param name="user">ผู้เรียก (chat.read)</param>
    /// <param name="status">สถานะ</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="needsAttentionOnly">เฉพาะเคสลูกค้าไม่พอใจ</param>
    /// <param name="take">จำนวน (สูงสุด 500)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    public Task<IReadOnlyList<ConversationDTO>> ListAsync(UserContextDTO user, ConversationStatus? status, long? customerId, bool needsAttentionOnly, int take = 100, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.ChatRead);
        return _conversations.ListAsync(status, customerId, needsAttentionOnly, Math.Clamp(take, 1, 500), ct);
    }

    /// <summary>ข้อความใน conversation</summary>
    /// <param name="user">ผู้เรียก (chat.read)</param>
    /// <param name="conversationId">conversation</param>
    /// <param name="take">จำนวนล่าสุด (สูงสุด 1000)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อความเรียงตามเวลา</returns>
    public async Task<IReadOnlyList<ChatMessageDTO>> GetMessagesAsync(UserContextDTO user, long conversationId, int take = 200, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.ChatRead);
        _ = await _conversations.GetAsync(conversationId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Conversation not found.");
        return await _conversations.ListMessagesAsync(conversationId, Math.Clamp(take, 1, 1000), ct).ConfigureAwait(false);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
