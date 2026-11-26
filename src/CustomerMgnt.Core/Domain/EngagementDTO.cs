namespace CustomerMgnt.Core.Domain;

/// <summary>การสนทนา 1 เรื่องของลูกค้าในช่องทางหนึ่ง (Chat Center / Omnichannel inbox)</summary>
public sealed class ConversationDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>ลูกค้า (null = ยังจับคู่ไม่ได้ รอทีมงานผูก)</summary>
    public long? CustomerId { get; set; }
    /// <summary>ช่องทาง</summary>
    public ChannelKind Channel { get; set; }
    /// <summary>รหัสผู้ส่งในช่องทาง (LINE userId, อีเมล ...)</summary>
    public string ExternalUserId { get; set; } = string.Empty;
    /// <summary>หัวข้อ (อีเมล) หรือข้อความแรก</summary>
    public string? Subject { get; set; }
    /// <summary>สถานะ</summary>
    public ConversationStatus Status { get; set; } = ConversationStatus.Open;
    /// <summary>ผู้รับผิดชอบ (AuthServer subject)</summary>
    public string? AssigneeSubject { get; set; }
    /// <summary>คะแนนอารมณ์ล่าสุด -1..1</summary>
    public double LastSentiment { get; set; }
    /// <summary>ต้องรีบดูแล (ลูกค้าไม่พอใจ)</summary>
    public bool NeedsAttention { get; set; }
    /// <summary>เวลาข้อความล่าสุด</summary>
    public DateTimeOffset LastMessageAt { get; set; }
    /// <summary>เวลาที่ทีมงานตอบครั้งแรก (ใช้วัด SLA first response)</summary>
    public DateTimeOffset? FirstResponseAt { get; set; }
    /// <summary>เวลาสร้าง</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>ชื่อลูกค้า (read model)</summary>
    public string? CustomerName { get; set; }
}

/// <summary>ข้อความ 1 รายการในการสนทนา</summary>
public sealed class ChatMessageDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>การสนทนา</summary>
    public long ConversationId { get; set; }
    /// <summary>ขาเข้า/ขาออก</summary>
    public MessageDirection Direction { get; set; }
    /// <summary>ข้อความ (ทำความสะอาด control character แล้ว)</summary>
    public string Body { get; set; } = string.Empty;
    /// <summary>รหัสข้อความในช่องทางต้นทาง (กันรับซ้ำเมื่อ webhook ส่งซ้ำ)</summary>
    public string? ExternalMessageId { get; set; }
    /// <summary>ผู้ส่งฝั่งทีมงาน (ขาออก)</summary>
    public string? SenderSubject { get; set; }
    /// <summary>คะแนนอารมณ์ -1..1</summary>
    public double Sentiment { get; set; }
    /// <summary>ป้ายอารมณ์</summary>
    public SentimentLabel SentimentLabel { get; set; }
    /// <summary>เวลา</summary>
    public DateTimeOffset SentAt { get; set; }
}

/// <summary>
/// กิจกรรมการติดตามลูกค้า: โทร ประชุม อีเมล แชต งาน — พร้อมวันครบกำหนดและการแจ้งเตือน
/// การประชุมสร้างใน MessageHub (<see cref="MessageHubMeetingId"/>) เพื่อใช้ Agenda, RSVP และ reminder ของ MessageHub
/// </summary>
public sealed class ActivityDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>โอกาสการขายที่เกี่ยวข้อง</summary>
    public long? OpportunityId { get; set; }
    /// <summary>ประเภท</summary>
    public ActivityKind Kind { get; set; }
    /// <summary>หัวข้อ</summary>
    public string Subject { get; set; } = string.Empty;
    /// <summary>รายละเอียด/บันทึกผล</summary>
    public string? Notes { get; set; }
    /// <summary>สถานะ</summary>
    public ActivityStatus Status { get; set; } = ActivityStatus.Planned;
    /// <summary>ผู้รับผิดชอบ</summary>
    public string OwnerSubject { get; set; } = string.Empty;
    /// <summary>กำหนดเวลา (UTC)</summary>
    public DateTimeOffset DueAt { get; set; }
    /// <summary>ระยะเวลา (นาที) สำหรับโทร/ประชุม</summary>
    public int? DurationMinutes { get; set; }
    /// <summary>แจ้งเตือนล่วงหน้า (นาที) — null = ไม่แจ้ง</summary>
    public int? RemindBeforeMinutes { get; set; }
    /// <summary>เวลาที่ส่งแจ้งเตือนแล้ว (กันส่งซ้ำ)</summary>
    public DateTimeOffset? ReminderSentAt { get; set; }
    /// <summary>เวลาทำเสร็จ</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>ผลลัพธ์ (เช่น สนใจ / ไม่รับสาย / นัดใหม่)</summary>
    public string? Outcome { get; set; }
    /// <summary>รหัสการประชุมใน MessageHub</summary>
    public Guid? MessageHubMeetingId { get; set; }
    /// <summary>เวลาสร้าง</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>เลยกำหนดหรือไม่ ณ เวลาที่ระบุ</summary>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <returns>true ถ้ายังไม่ทำและเลยกำหนดแล้ว</returns>
    public bool IsOverdue(DateTimeOffset now) => Status == ActivityStatus.Planned && DueAt < now;
}

/// <summary>เหตุการณ์พฤติกรรมของลูกค้า (email open/click, web visit, purchase ...) — วัตถุดิบของ ML ทุกตัว</summary>
public sealed class EngagementEventDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>ชนิดเหตุการณ์</summary>
    public EngagementKind Kind { get; set; }
    /// <summary>ช่องทาง (ถ้ามี)</summary>
    public ChannelKind? Channel { get; set; }
    /// <summary>รหัสสินค้า/เนื้อหาที่เกี่ยวข้อง</summary>
    public string? ItemCode { get; set; }
    /// <summary>มูลค่า (ยอดซื้อ, เวลาที่อยู่บนหน้าเว็บเป็นวินาที ฯลฯ)</summary>
    public decimal Value { get; set; }
    /// <summary>เวลาเกิดเหตุการณ์ (UTC)</summary>
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>สัญญา MA / บริการ (ใช้ทำนายการต่อสัญญาและ upsell)</summary>
public sealed class ContractDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>เลขที่สัญญา</summary>
    public string ContractNo { get; set; } = string.Empty;
    /// <summary>ชื่อบริการ/ผลิตภัณฑ์</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>วันเริ่ม</summary>
    public DateOnly StartDate { get; set; }
    /// <summary>วันสิ้นสุด</summary>
    public DateOnly EndDate { get; set; }
    /// <summary>มูลค่าต่อปี</summary>
    public decimal AnnualValue { get; set; }
    /// <summary>สถานะ</summary>
    public ContractStatus Status { get; set; } = ContractStatus.Active;
    /// <summary>จำนวนครั้งที่ต่อสัญญามาแล้ว</summary>
    public int RenewalCount { get; set; }
    /// <summary>SLA policy ที่ผูกกับสัญญานี้</summary>
    public long? SlaPolicyId { get; set; }

    /// <summary>รหัสผลิตภัณฑ์ใน ProductMgnt ที่สัญญานี้ครอบคลุม (ตัวพิมพ์ใหญ่; null = ไม่ผูกผลิตภัณฑ์)</summary>
    public string? ProductCode { get; set; }
}

/// <summary>รายการในแคตตาล็อก (สินค้า/บริการ/บทความ/โปรโมชัน) สำหรับระบบแนะนำ</summary>
public sealed class CatalogItemDTO
{
    /// <summary>รหัส</summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>ชื่อ</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>ชนิด</summary>
    public CatalogItemKind Kind { get; set; }
    /// <summary>หมวด</summary>
    public string? Category { get; set; }
    /// <summary>ราคา</summary>
    public decimal Price { get; set; }
    /// <summary>ใช้งานอยู่</summary>
    public bool IsActive { get; set; } = true;
}
