namespace CustomerMgnt.Api.Infrastructure;

/// <summary>ค่าตั้ง webhook ขาเข้า (section <c>Webhooks</c>) — secret ควรมาจากไฟล์ใน /run/secrets</summary>
public sealed class WebhookOptions
{
    /// <summary>ชื่อ section</summary>
    public const string SectionName = "Webhooks";

    /// <summary>Channel secret ของ LINE Official Account (ใช้ตรวจ X-Line-Signature)</summary>
    public string LineChannelSecret { get; set; } = string.Empty;

    /// <summary>shared secret ของ webhook กลาง (email gateway, Facebook relay, web chat) — ตรวจ X-Signature: sha256=hex</summary>
    public string InboundSecret { get; set; } = string.Empty;

    /// <summary>ยอมรับเหตุการณ์ที่เก่ากว่านี้ไม่ได้ (กัน replay) — วินาที</summary>
    public int MaxClockSkewSeconds { get; set; } = 300;
}
