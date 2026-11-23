using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.AspNetCore.Authentication;
using Common.AspNetCore.Hosting;
using CustomerMgnt.Api.Infrastructure;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Api.Controllers;

/// <summary>ศูนย์รวมแชท omnichannel (LINE OA, อีเมล, Facebook, ...) — inbox, ข้อความ, ตอบกลับผ่าน MessageHub</summary>
/// <param name="omnichannel">บริการ omnichannel</param>
[Route("api/conversations")]
public sealed class ConversationsController(OmnichannelService omnichannel) : CustomerMgntControllerBase
{
    /// <summary>รายการห้องสนทนา (ห้องที่ต้องดูแลขึ้นก่อน)</summary>
    /// <param name="status">สถานะ</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="needsAttentionOnly">เฉพาะที่อารมณ์ลบ/ต้องดูแล</param>
    /// <param name="take">จำนวน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ห้องสนทนา</returns>
    [HttpGet]
    [RequirePermission(Permissions.ChatRead)]
    public async Task<ActionResult<IReadOnlyList<ConversationDTO>>> List([FromQuery] ConversationStatus? status, [FromQuery] long? customerId, [FromQuery] bool needsAttentionOnly = false, [FromQuery] int take = 100, CancellationToken ct = default) =>
        Ok(await omnichannel.ListAsync(CurrentUser, status, customerId, needsAttentionOnly, Math.Clamp(take, 1, 500), ct));

    /// <summary>ข้อความในห้อง</summary>
    /// <param name="id">ห้อง</param>
    /// <param name="take">จำนวนล่าสุด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อความเรียงตามเวลา</returns>
    [HttpGet("{id:long}/messages")]
    [RequirePermission(Permissions.ChatRead)]
    public async Task<ActionResult<IReadOnlyList<ChatMessageDTO>>> Messages(long id, [FromQuery] int take = 200, CancellationToken ct = default) =>
        Ok(await omnichannel.GetMessagesAsync(CurrentUser, id, Math.Clamp(take, 1, 1000), ct));

    /// <summary>ตอบกลับลูกค้า (ส่งผ่าน MessageHub ตามช่องทางของห้อง)</summary>
    /// <param name="id">ห้อง</param>
    /// <param name="request">ข้อความ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อความที่ส่ง</returns>
    [HttpPost("{id:long}/reply")]
    [RequirePermission(Permissions.ChatReply)]
    public async Task<ActionResult<ChatMessageDTO>> Reply(long id, [FromBody] ReplyRequest request, CancellationToken ct) =>
        Ok(await omnichannel.ReplyAsync(CurrentUser, id, request, ct));

    /// <summary>มอบหมาย/ผูกลูกค้า/เปลี่ยนสถานะห้อง</summary>
    /// <param name="id">ห้อง</param>
    /// <param name="request">ค่าที่แก้</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ห้องหลังแก้</returns>
    [HttpPatch("{id:long}")]
    [RequirePermission(Permissions.ChatReply)]
    public async Task<ActionResult<ConversationDTO>> Update(long id, [FromBody] ConversationUpdateRequest request, CancellationToken ct) =>
        Ok(await omnichannel.UpdateAsync(CurrentUser, id, request.AssigneeSubject, request.CustomerId, request.Status, ct));
}

/// <summary>
/// Webhook ขาเข้าจากช่องทางภายนอก (ไม่ใช้ JWT แต่ตรวจลายเซ็น HMAC-SHA256 แบบ constant-time + rate limit เข้ม)
/// </summary>
/// <param name="omnichannel">บริการ omnichannel</param>
/// <param name="options">secret ของ webhook</param>
/// <param name="clock">นาฬิกา</param>
/// <param name="logger">logger</param>
[Route("api/webhooks")]
[AllowAnonymous]
[EnableRateLimiting(CommonWebHostExtensions.StrictRateLimitPolicy)]
public sealed class WebhooksController(OmnichannelService omnichannel, IOptions<WebhookOptions> options, TimeProvider clock, ILogger<WebhooksController> logger) : CustomerMgntControllerBase
{
    /// <summary>ขนาด body สูงสุดของ webhook</summary>
    private const int MaxBody = 1024 * 1024;

    /// <summary>
    /// LINE Messaging API webhook — ตรวจ <c>X-Line-Signature</c> = Base64(HMAC-SHA256(channelSecret, body))
    /// แล้วนำข้อความ text เข้าสู่ inbox (ข้อความซ้ำจาก LINE retry ถูกกรองด้วย message id)
    /// </summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>200 เมื่อรับแล้ว (LINE ต้องการตอบเร็ว), 401 เมื่อลายเซ็นผิด</returns>
    [HttpPost("line")]
    [RequestSizeLimit(MaxBody)]
    [Consumes("application/json")]
    public async Task<IActionResult> Line(CancellationToken ct)
    {
        // Step 1: อ่าน body ดิบ (ลายเซ็นคำนวณจาก byte จริง)
        byte[] body = await ReadBodyAsync(ct);
        string secret = options.Value.LineChannelSecret;
        if (string.IsNullOrEmpty(secret) || !VerifyBase64Signature(body, Request.Headers["X-Line-Signature"].ToString(), secret))
        {
            logger.LogWarning("Rejected LINE webhook with invalid signature from {Ip}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        // Step 2: แยกเหตุการณ์ข้อความ text
        int accepted = 0;
        foreach (InboundMessageRequest message in ParseLineEvents(body, clock.GetUtcNow(), options.Value.MaxClockSkewSeconds))
        {
            await omnichannel.ReceiveAsync(message, ct);
            accepted++;
        }

        return Ok(new { accepted });
    }

    /// <summary>
    /// webhook กลางสำหรับช่องทางอื่น (email gateway, Facebook/Instagram relay, web chat) —
    /// body = InboundMessageRequest, header <c>X-Signature: sha256=&lt;hex HMAC-SHA256(secret, body)&gt;</c>
    /// </summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ห้องสนทนาที่รับข้อความ</returns>
    [HttpPost("inbound")]
    [RequestSizeLimit(MaxBody)]
    [Consumes("application/json")]
    public async Task<IActionResult> Inbound(CancellationToken ct)
    {
        // Step 1: ตรวจลายเซ็น
        byte[] body = await ReadBodyAsync(ct);
        string secret = options.Value.InboundSecret;
        string header = Request.Headers["X-Signature"].ToString();
        if (string.IsNullOrEmpty(secret) || !header.StartsWith("sha256=", StringComparison.Ordinal) || !VerifyHexSignature(body, header["sha256=".Length..], secret))
        {
            logger.LogWarning("Rejected inbound webhook with invalid signature from {Ip}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        // Step 2: แปลง + ตรวจค่า (DataAnnotations) แล้วส่งเข้า inbox
        InboundMessageRequest? message;
        try
        {
            message = JsonSerializer.Deserialize<InboundMessageRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return Problem("Malformed JSON.", statusCode: StatusCodes.Status400BadRequest);
        }

        string? error = null;
        if (message is null || !TryValidate(message, out error))
        {
            return Problem(error ?? "Invalid message.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (message.SentAt is { } sent && Math.Abs((clock.GetUtcNow() - sent).TotalSeconds) > options.Value.MaxClockSkewSeconds * 12)
        {
            return Problem("Message timestamp is outside the accepted window.", statusCode: StatusCodes.Status400BadRequest);
        }

        ConversationDTO conv = await omnichannel.ReceiveAsync(message, ct);
        return Ok(new { conversationId = conv.Id });
    }

    /// <summary>JSON ของ webhook (camelCase, enum เป็น string หรือตัวเลข)</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>แปลงเหตุการณ์ LINE เป็นข้อความขาเข้า (เฉพาะ message/text จากผู้ใช้; เหตุการณ์เก่าเกินถูกข้าม)</summary>
    /// <param name="body">JSON body</param>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <param name="maxSkewSeconds">อายุสูงสุดของเหตุการณ์ (วินาที) — LINE redelivery อาจช้า จึงยอมให้ 12 เท่า</param>
    /// <returns>ข้อความ</returns>
    internal static IEnumerable<InboundMessageRequest> ParseLineEvents(byte[] body, DateTimeOffset now, int maxSkewSeconds)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("events", out JsonElement events) || events.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<InboundMessageRequest>();
        foreach (JsonElement e in events.EnumerateArray())
        {
            // Step 1: เฉพาะข้อความ text ที่มี userId
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out JsonElement et) || et.GetString() != "message" ||
                !e.TryGetProperty("message", out JsonElement m) || !m.TryGetProperty("type", out JsonElement mt) || mt.GetString() != "text" ||
                !m.TryGetProperty("text", out JsonElement textEl) || textEl.ValueKind != JsonValueKind.String ||
                !e.TryGetProperty("source", out JsonElement src) || !src.TryGetProperty("userId", out JsonElement uid))
            {
                continue;
            }

            // Step 2: กัน replay ของเหตุการณ์เก่ามาก
            DateTimeOffset sentAt = e.TryGetProperty("timestamp", out JsonElement ts) && ts.TryGetInt64(out long ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : now;
            if (Math.Abs((now - sentAt).TotalSeconds) > maxSkewSeconds * 12)
            {
                continue;
            }

            string text = textEl.GetString() ?? string.Empty;
            result.Add(new InboundMessageRequest
            {
                Channel = ChannelKind.Line,
                ExternalUserId = uid.ValueKind == JsonValueKind.String ? uid.GetString()! : string.Empty,
                Body = text.Length > 20_000 ? text[..20_000] : text,
                ExternalMessageId = m.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                SentAt = sentAt,
            });
        }

        return result.Where(r => r.ExternalUserId.Length is > 0 and <= 200 && r.Body.Length > 0).ToList();
    }

    /// <summary>ตรวจลายเซ็น Base64 แบบ constant-time</summary>
    /// <param name="body">body</param>
    /// <param name="signature">ลายเซ็น Base64</param>
    /// <param name="secret">secret</param>
    /// <returns>ถูกต้องหรือไม่</returns>
    internal static bool VerifyBase64Signature(byte[] body, string signature, string secret)
    {
        Span<byte> provided = stackalloc byte[64];
        if (!Convert.TryFromBase64String(signature, provided, out int written) || written != 32)
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided[..written]);
    }

    /// <summary>ตรวจลายเซ็น hex แบบ constant-time</summary>
    /// <param name="body">body</param>
    /// <param name="hex">ลายเซ็น hex</param>
    /// <param name="secret">secret</param>
    /// <returns>ถูกต้องหรือไม่</returns>
    internal static bool VerifyHexSignature(byte[] body, string hex, string secret)
    {
        if (hex.Length != 64)
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body), provided);
    }

    /// <summary>ตรวจ DataAnnotations ของ object</summary>
    private static bool TryValidate(object model, out string? error)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        bool ok = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model, new(model), results, validateAllProperties: true);
        error = ok ? null : string.Join("; ", results.Select(r => r.ErrorMessage));
        return ok;
    }

    /// <summary>อ่าน body ดิบ (จำกัดขนาดแล้วโดย RequestSizeLimit)</summary>
    private async Task<byte[]> ReadBodyAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
