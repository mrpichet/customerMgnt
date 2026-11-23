namespace CustomerMgnt.Core.Domain;

// ค่าตัวเลขของ enum ถูกบันทึกลงฐานข้อมูล ห้ามเปลี่ยนเลขของสมาชิกที่มีอยู่แล้ว (เพิ่มต่อท้ายได้เท่านั้น)

/// <summary>ประเภทลูกค้า</summary>
public enum CustomerKind
{
    /// <summary>บุคคลธรรมดา</summary>
    Person = 1,
    /// <summary>นิติบุคคล (บริษัท, หน่วยงาน)</summary>
    Juristic = 2,
}

/// <summary>สถานะลูกค้า</summary>
public enum CustomerStatus
{
    /// <summary>ลูกค้าเป้าหมาย (ยังไม่ซื้อ)</summary>
    Prospect = 1,
    /// <summary>ลูกค้าปัจจุบัน</summary>
    Active = 2,
    /// <summary>ไม่มีความเคลื่อนไหว</summary>
    Inactive = 3,
    /// <summary>เลิกใช้บริการแล้ว</summary>
    Churned = 4,
}

/// <summary>ช่องทางการติดต่อ (Omnichannel)</summary>
public enum ChannelKind
{
    /// <summary>LINE Official Account</summary>
    Line = 1,
    /// <summary>อีเมล</summary>
    Email = 2,
    /// <summary>Facebook Messenger</summary>
    Facebook = 3,
    /// <summary>Instagram Direct</summary>
    Instagram = 4,
    /// <summary>Web chat บนเว็บไซต์</summary>
    WebChat = 5,
    /// <summary>โทรศัพท์</summary>
    Phone = 6,
    /// <summary>SMS</summary>
    Sms = 7,
    /// <summary>Microsoft Teams</summary>
    Teams = 8,
    /// <summary>WhatsApp</summary>
    WhatsApp = 9,
    /// <summary>Telegram</summary>
    Telegram = 10,
    /// <summary>รีวิว (Google / Facebook / Marketplace)</summary>
    Review = 11,
}

/// <summary>ทิศทางข้อความ</summary>
public enum MessageDirection
{
    /// <summary>ลูกค้าส่งเข้ามา</summary>
    Inbound = 1,
    /// <summary>ทีมงานตอบกลับ</summary>
    Outbound = 2,
}

/// <summary>สถานะการสนทนา</summary>
public enum ConversationStatus
{
    /// <summary>รอทีมงานตอบ</summary>
    Open = 1,
    /// <summary>รอลูกค้าตอบ</summary>
    Pending = 2,
    /// <summary>ปิดเรื่องแล้ว</summary>
    Resolved = 3,
}

/// <summary>ประเภทกิจกรรม</summary>
public enum ActivityKind
{
    /// <summary>โทร</summary>
    Call = 1,
    /// <summary>ประชุม (สร้างใน MessageHub)</summary>
    Meeting = 2,
    /// <summary>อีเมล</summary>
    Email = 3,
    /// <summary>แชต</summary>
    Chat = 4,
    /// <summary>งานติดตาม (follow-up)</summary>
    Task = 5,
    /// <summary>บันทึก</summary>
    Note = 6,
    /// <summary>เข้าพบลูกค้า</summary>
    Visit = 7,
}

/// <summary>สถานะกิจกรรม</summary>
public enum ActivityStatus
{
    /// <summary>วางแผนไว้</summary>
    Planned = 1,
    /// <summary>ทำแล้ว</summary>
    Done = 2,
    /// <summary>ยกเลิก</summary>
    Cancelled = 3,
}

/// <summary>ประเภทคำขอของลูกค้า</summary>
public enum RequestKind
{
    /// <summary>คำขอทั่วไป</summary>
    Request = 1,
    /// <summary>TOR (Terms of Reference) สำหรับโครงการใหม่</summary>
    Tor = 2,
    /// <summary>ขอเปลี่ยนแปลงขอบเขต</summary>
    ChangeRequest = 3,
    /// <summary>แจ้งปัญหา (ส่งต่อ IssueTracking พร้อม Severity/SLA)</summary>
    Incident = 4,
}

/// <summary>สถานะคำขอ</summary>
public enum RequestStatus
{
    /// <summary>รับเรื่องใหม่</summary>
    New = 1,
    /// <summary>กำลังพิจารณา</summary>
    UnderReview = 2,
    /// <summary>อนุมัติแล้ว (พร้อมส่งต่อ)</summary>
    Approved = 3,
    /// <summary>ส่งต่อระบบปลายทางแล้ว</summary>
    Forwarded = 4,
    /// <summary>ปฏิเสธ</summary>
    Rejected = 5,
    /// <summary>ปิดเรื่อง</summary>
    Closed = 6,
}

/// <summary>ขั้นของ Pipeline การขาย</summary>
public enum PipelineStage
{
    /// <summary>ลูกค้าเป้าหมายใหม่</summary>
    New = 1,
    /// <summary>ติดต่อแล้ว</summary>
    Contacted = 2,
    /// <summary>ผ่านการคัดกรอง</summary>
    Qualified = 3,
    /// <summary>ส่งข้อเสนอ</summary>
    Proposal = 4,
    /// <summary>ต่อรอง</summary>
    Negotiation = 5,
    /// <summary>ปิดการขายสำเร็จ</summary>
    Won = 6,
    /// <summary>ปิดการขายไม่สำเร็จ</summary>
    Lost = 7,
}

/// <summary>ระบบภายนอกที่เชื่อมต่อ</summary>
public enum ExternalSystem
{
    /// <summary>mrpichet/issuetracking</summary>
    IssueTracking = 1,
    /// <summary>mrpichet/ProjectMgnt</summary>
    ProjectMgnt = 2,
    /// <summary>mrpichet/requirementMgnt</summary>
    RequirementMgnt = 3,
    /// <summary>mrpichet/messagehub</summary>
    MessageHub = 4,
}

/// <summary>สถานะ SLA ของงานหนึ่งรายการ</summary>
public enum SlaState
{
    /// <summary>ไม่มี SLA (ลูกค้าไม่มีสัญญา หรือไม่ได้กำหนด severity)</summary>
    NotApplicable = 0,
    /// <summary>อยู่ในเวลา</summary>
    OnTrack = 1,
    /// <summary>ใกล้ผิด SLA (ใช้เวลาเกินเกณฑ์เตือน)</summary>
    AtRisk = 2,
    /// <summary>ผิด SLA แล้ว</summary>
    Breached = 3,
    /// <summary>ทำสำเร็จภายใน SLA</summary>
    Met = 4,
}

/// <summary>อารมณ์ของข้อความ</summary>
public enum SentimentLabel
{
    /// <summary>ไม่พอใจ</summary>
    Negative = -1,
    /// <summary>เป็นกลาง</summary>
    Neutral = 0,
    /// <summary>พอใจ</summary>
    Positive = 1,
}

/// <summary>เหตุการณ์พฤติกรรมของลูกค้า (ข้อมูลสำหรับ ML)</summary>
public enum EngagementKind
{
    /// <summary>ส่งอีเมลถึงลูกค้า</summary>
    EmailSent = 1,
    /// <summary>ลูกค้าเปิดอีเมล</summary>
    EmailOpen = 2,
    /// <summary>ลูกค้าคลิกลิงก์ในอีเมล</summary>
    EmailClick = 3,
    /// <summary>เข้าชมเว็บไซต์</summary>
    WebVisit = 4,
    /// <summary>ซื้อสินค้า/บริการ (Value = ยอดเงิน)</summary>
    Purchase = 5,
    /// <summary>ร้องเรียน</summary>
    Complaint = 6,
    /// <summary>เปิด ticket แจ้งปัญหา</summary>
    SupportTicket = 7,
    /// <summary>อ่านบทความ/เนื้อหา (ItemCode = รหัสเนื้อหา)</summary>
    ContentView = 8,
    /// <summary>ดูสินค้า (ItemCode = รหัสสินค้า)</summary>
    ProductView = 9,
    /// <summary>เปิดอ่านข้อความแจ้งเตือน (LINE/SMS)</summary>
    MessageOpen = 10,
}

/// <summary>สถานะสัญญา MA / บริการ</summary>
public enum ContractStatus
{
    /// <summary>มีผลอยู่</summary>
    Active = 1,
    /// <summary>ต่อสัญญาแล้ว</summary>
    Renewed = 2,
    /// <summary>หมดอายุ (ไม่ต่อ)</summary>
    Expired = 3,
    /// <summary>ยกเลิก</summary>
    Cancelled = 4,
}

/// <summary>ประเภทรายการในแคตตาล็อก (ใช้แนะนำ)</summary>
public enum CatalogItemKind
{
    /// <summary>สินค้า</summary>
    Product = 1,
    /// <summary>บริการ</summary>
    Service = 2,
    /// <summary>บทความ/เนื้อหา</summary>
    Content = 3,
    /// <summary>โปรโมชัน</summary>
    Promotion = 4,
}
