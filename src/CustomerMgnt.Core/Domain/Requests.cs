using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace CustomerMgnt.Core.Domain;

// Input model แยกจาก DTO เพื่อกัน over-posting (ผู้เรียกตั้งค่า field ภายใน เช่น Id/Version/TaxIdHash ไม่ได้)
// DataAnnotations เป็นด่านแรก (Web API ตรวจอัตโนมัติ + ใช้สร้าง JSON schema ให้ AI) และ Service ตรวจซ้ำเสมอ

/// <summary>ลงทะเบียนลูกค้า (บุคคล หรือ นิติบุคคล)</summary>
public sealed class RegisterCustomerRequest
{
    /// <summary>บุคคล หรือ นิติบุคคล</summary>
    [Description("Person or Juristic")] public CustomerKind Kind { get; set; }
    /// <summary>รหัสลูกค้า (ว่าง = ระบบสร้างให้ CUST-xxxxxx)</summary>
    [StringLength(30)] public string? Code { get; set; }
    /// <summary>ชื่อ (บุคคล)</summary>
    [StringLength(100)] public string? FirstName { get; set; }
    /// <summary>นามสกุล (บุคคล)</summary>
    [StringLength(100)] public string? LastName { get; set; }
    /// <summary>ชื่อจดทะเบียน (นิติบุคคล)</summary>
    [StringLength(300)] public string? LegalName { get; set; }
    /// <summary>ชื่อที่แสดง (ว่าง = สร้างจากชื่อ)</summary>
    [StringLength(300)] public string? DisplayName { get; set; }
    /// <summary>เลขผู้เสียภาษี 13 หลัก หรือเลขบัตรประชาชน (เก็บแบบเข้ารหัส)</summary>
    [StringLength(20)] public string? TaxId { get; set; }
    /// <summary>อีเมล</summary>
    [EmailAddress, StringLength(320)] public string? Email { get; set; }
    /// <summary>โทรศัพท์</summary>
    [StringLength(32)] public string? Phone { get; set; }
    /// <summary>อุตสาหกรรม</summary>
    [StringLength(100)] public string? Industry { get; set; }
    /// <summary>จำนวนพนักงาน</summary>
    [Range(0, 10_000_000)] public int? EmployeeCount { get; set; }
    /// <summary>รายได้ต่อปี</summary>
    [Range(0, 1_000_000_000_000)] public decimal? AnnualRevenue { get; set; }
    /// <summary>เว็บไซต์ (https เท่านั้น)</summary>
    [Url, StringLength(300)] public string? Website { get; set; }
    /// <summary>ที่อยู่</summary>
    [StringLength(1000)] public string? Address { get; set; }
    /// <summary>จังหวัด</summary>
    [StringLength(100)] public string? Province { get; set; }
    /// <summary>ประเทศ ISO alpha-2</summary>
    [StringLength(2, MinimumLength = 2)] public string Country { get; set; } = "TH";
    /// <summary>เขตเวลา IANA</summary>
    [StringLength(64)] public string TimeZoneId { get; set; } = "Asia/Bangkok";
    /// <summary>สถานะเริ่มต้น</summary>
    public CustomerStatus Status { get; set; } = CustomerStatus.Prospect;
    /// <summary>ผู้ดูแล (ว่าง = ผู้ลงทะเบียน)</summary>
    [StringLength(128)] public string? OwnerSubject { get; set; }
    /// <summary>ป้ายกำกับ</summary>
    [StringLength(500)] public string? Tags { get; set; }
    /// <summary>ยินยอมรับข้อความการตลาด</summary>
    public bool MarketingConsent { get; set; }
}

/// <summary>แก้ไขลูกค้า (null = ไม่เปลี่ยน)</summary>
public sealed class UpdateCustomerRequest
{
    /// <summary>Version ที่อ่านล่าสุด (บังคับ)</summary>
    [Range(1, long.MaxValue)] public long Version { get; set; }
    /// <summary>ชื่อที่แสดง</summary>
    [StringLength(300)] public string? DisplayName { get; set; }
    /// <summary>อีเมล</summary>
    [EmailAddress, StringLength(320)] public string? Email { get; set; }
    /// <summary>โทรศัพท์</summary>
    [StringLength(32)] public string? Phone { get; set; }
    /// <summary>อุตสาหกรรม</summary>
    [StringLength(100)] public string? Industry { get; set; }
    /// <summary>จำนวนพนักงาน</summary>
    [Range(0, 10_000_000)] public int? EmployeeCount { get; set; }
    /// <summary>รายได้ต่อปี</summary>
    [Range(0, 1_000_000_000_000)] public decimal? AnnualRevenue { get; set; }
    /// <summary>ที่อยู่</summary>
    [StringLength(1000)] public string? Address { get; set; }
    /// <summary>สถานะ</summary>
    public CustomerStatus? Status { get; set; }
    /// <summary>ผู้ดูแล</summary>
    [StringLength(128)] public string? OwnerSubject { get; set; }
    /// <summary>ป้ายกำกับ</summary>
    [StringLength(500)] public string? Tags { get; set; }
    /// <summary>ยินยอมรับข้อความการตลาด</summary>
    public bool? MarketingConsent { get; set; }
}

/// <summary>เงื่อนไขค้นหาลูกค้า</summary>
public sealed class CustomerSearchCriteria
{
    /// <summary>ค้นจากชื่อ/รหัส/อีเมล (contains)</summary>
    [StringLength(100)] public string? Text { get; set; }
    /// <summary>ประเภท</summary>
    public CustomerKind? Kind { get; set; }
    /// <summary>สถานะ</summary>
    public CustomerStatus? Status { get; set; }
    /// <summary>ผู้ดูแล</summary>
    [StringLength(128)] public string? OwnerSubject { get; set; }
    /// <summary>กลุ่ม</summary>
    [StringLength(60)] public string? Segment { get; set; }
    /// <summary>หน้า (เริ่ม 1)</summary>
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    /// <summary>ขนาดหน้า (สูงสุด 500)</summary>
    [Range(1, 500)] public int PageSize { get; set; } = 50;
}

/// <summary>ผูกบุคคลเข้ากับนิติบุคคล</summary>
public sealed class CreateRelationshipRequest
{
    /// <summary>บุคคล</summary>
    [Description("Customer id of the person")] public long PersonId { get; set; }
    /// <summary>นิติบุคคล</summary>
    [Description("Customer id of the juristic person (company)")] public long OrganizationId { get; set; }
    /// <summary>ตำแหน่ง</summary>
    [Required, StringLength(150)] public string Position { get; set; } = string.Empty;
    /// <summary>แผนก</summary>
    [StringLength(150)] public string? Department { get; set; }
    /// <summary>ผู้ติดต่อหลัก</summary>
    public bool IsPrimaryContact { get; set; }
    /// <summary>ผู้มีอำนาจตัดสินใจ</summary>
    public bool IsDecisionMaker { get; set; }
    /// <summary>เริ่ม</summary>
    public DateOnly ValidFrom { get; set; }
    /// <summary>สิ้นสุด (null = ยังดำรงตำแหน่ง)</summary>
    public DateOnly? ValidTo { get; set; }
}

/// <summary>ผูกตัวตนในช่องทางกับลูกค้า</summary>
public sealed class ChannelIdentityRequest
{
    /// <summary>ช่องทาง</summary>
    public ChannelKind Channel { get; set; }
    /// <summary>รหัสในช่องทาง</summary>
    [Required, StringLength(200)] public string ExternalId { get; set; } = string.Empty;
    /// <summary>ชื่อที่แสดง</summary>
    [StringLength(200)] public string? DisplayName { get; set; }
}

/// <summary>ข้อความขาเข้าที่ normalize แล้ว (จาก LINE webhook, email gateway, social webhook)</summary>
public sealed class InboundMessageRequest
{
    /// <summary>ช่องทาง</summary>
    public ChannelKind Channel { get; set; }
    /// <summary>รหัสผู้ส่งในช่องทาง</summary>
    [Required, StringLength(200)] public string ExternalUserId { get; set; } = string.Empty;
    /// <summary>ชื่อผู้ส่ง</summary>
    [StringLength(200)] public string? DisplayName { get; set; }
    /// <summary>หัวข้อ (อีเมล)</summary>
    [StringLength(300)] public string? Subject { get; set; }
    /// <summary>ข้อความ</summary>
    [Required, StringLength(20_000)] public string Body { get; set; } = string.Empty;
    /// <summary>รหัสข้อความต้นทาง (กันซ้ำ)</summary>
    [StringLength(200)] public string? ExternalMessageId { get; set; }
    /// <summary>เวลาส่ง (null = ตอนนี้)</summary>
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>ตอบลูกค้าในการสนทนา (ส่งผ่าน MessageHub)</summary>
public sealed class ReplyRequest
{
    /// <summary>ข้อความ</summary>
    [Required, StringLength(10_000, MinimumLength = 1)] public string Body { get; set; } = string.Empty;
    /// <summary>ปิดเรื่องหลังตอบ</summary>
    public bool Resolve { get; set; }
}

/// <summary>สร้างกิจกรรม</summary>
public sealed class CreateActivityRequest
{
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>โอกาสการขาย</summary>
    public long? OpportunityId { get; set; }
    /// <summary>ประเภท</summary>
    public ActivityKind Kind { get; set; } = ActivityKind.Task;
    /// <summary>หัวข้อ</summary>
    [Required, StringLength(300, MinimumLength = 2)] public string Subject { get; set; } = string.Empty;
    /// <summary>รายละเอียด</summary>
    [StringLength(10_000)] public string? Notes { get; set; }
    /// <summary>ผู้รับผิดชอบ (ว่าง = ผู้สร้าง)</summary>
    [StringLength(128)] public string? OwnerSubject { get; set; }
    /// <summary>กำหนดเวลา (UTC)</summary>
    public DateTimeOffset DueAt { get; set; }
    /// <summary>ระยะเวลา (นาที)</summary>
    [Range(0, 1440)] public int? DurationMinutes { get; set; }
    /// <summary>แจ้งเตือนล่วงหน้า (นาที)</summary>
    [Range(0, 10_080)] public int? RemindBeforeMinutes { get; set; } = 30;
    /// <summary>สถานะเริ่มต้น (บันทึกย้อนหลัง = Done)</summary>
    public ActivityStatus Status { get; set; } = ActivityStatus.Planned;
    /// <summary>สำหรับ Meeting: ผู้เข้าร่วม (รหัสผู้ใช้ MessageHub) — ระบบสร้างประชุมใน MessageHub และส่งคำเชิญ</summary>
    public List<Guid> MeetingAttendees { get; set; } = [];
    /// <summary>สำหรับ Meeting: วาระ</summary>
    public List<string> MeetingAgenda { get; set; } = [];
}

/// <summary>ปิดกิจกรรม</summary>
public sealed class CompleteActivityRequest
{
    /// <summary>ผลลัพธ์</summary>
    [StringLength(300)] public string? Outcome { get; set; }
    /// <summary>บันทึกเพิ่มเติม</summary>
    [StringLength(10_000)] public string? Notes { get; set; }
    /// <summary>สร้างงานติดตามต่อ (follow-up) หลังจากนี้กี่วัน (null = ไม่สร้าง)</summary>
    [Range(1, 365)] public int? FollowUpInDays { get; set; }
}

/// <summary>สร้างคำขอ/TOR</summary>
public sealed class CreateCustomerRequestRequest
{
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>ผู้ติดต่อ</summary>
    public long? ContactPersonId { get; set; }
    /// <summary>ประเภท</summary>
    public RequestKind Kind { get; set; } = RequestKind.Request;
    /// <summary>หัวข้อ</summary>
    [Required, StringLength(300, MinimumLength = 3)] public string Title { get; set; } = string.Empty;
    /// <summary>รายละเอียด</summary>
    [StringLength(100_000)] public string? Description { get; set; }
    /// <summary>severity (Incident) ต้องเป็นรหัสที่มีใน SLA ของลูกค้า</summary>
    [StringLength(20)] public string? SeverityCode { get; set; }
    /// <summary>งบประมาณ</summary>
    [Range(0, 1_000_000_000_000)] public decimal? Budget { get; set; }
    /// <summary>วันที่ต้องการ</summary>
    public DateOnly? DesiredDate { get; set; }
    /// <summary>รายการ TOR</summary>
    [MaxLength(2_000)] public List<TorItemRequest> Items { get; set; } = [];
}

/// <summary>ข้อ TOR</summary>
public sealed class TorItemRequest
{
    /// <summary>เลขข้อ</summary>
    [Required, StringLength(30)] public string ItemNo { get; set; } = string.Empty;
    /// <summary>ข้อความ</summary>
    [Required, StringLength(10_000, MinimumLength = 3)] public string Text { get; set; } = string.Empty;
    /// <summary>Low/Medium/High/Critical</summary>
    [StringLength(10)] public string Priority { get; set; } = "Medium";
    /// <summary>บังคับ</summary>
    public bool Mandatory { get; set; } = true;
}

/// <summary>ตัดสินคำขอ</summary>
public sealed class RequestDecisionRequest
{
    /// <summary>Version ที่อ่านล่าสุด</summary>
    [Range(1, long.MaxValue)] public long Version { get; set; }
    /// <summary>สถานะใหม่ (UnderReview / Approved / Rejected / Closed)</summary>
    public RequestStatus Status { get; set; }
    /// <summary>หมายเหตุ</summary>
    [StringLength(2000)] public string? Comment { get; set; }
}

/// <summary>ตัวเลือกการส่งต่อคำขอ</summary>
public sealed class ForwardRequestOptions
{
    /// <summary>ส่งไป ProjectMgnt (สร้างโครงการ)</summary>
    public bool ToProjectMgnt { get; set; }
    /// <summary>ส่งไป RequirementMgnt (สร้าง project + requirement จากข้อ TOR)</summary>
    public bool ToRequirementMgnt { get; set; }
    /// <summary>ส่งไป IssueTracking (สร้าง issue พร้อม severity/SLA)</summary>
    public bool ToIssueTracking { get; set; }
    /// <summary>รหัสโครงการ (ProjectMgnt/RequirementMgnt) A-Z0-9 2-10 ตัว</summary>
    [StringLength(10, MinimumLength = 2)] public string? ProjectCode { get; set; }
    /// <summary>PM ใน ProjectMgnt</summary>
    public long? ProjectManagerId { get; set; }
    /// <summary>Waterfall / Agile / Scrum / Kanban / Hybrid</summary>
    [StringLength(20)] public string Methodology { get; set; } = "Waterfall";
    /// <summary>มาตรฐานใน RequirementMgnt เช่น Iso29110</summary>
    [StringLength(20)] public string RequirementStandard { get; set; } = "Iso29110";
    /// <summary>module ใน RequirementMgnt ที่จะใส่ requirement (null = ใช้ module แรกของ project)</summary>
    public long? RequirementModuleId { get; set; }
    /// <summary>project ใน IssueTracking (null = ใช้ project ที่ผูกกับลูกค้า)</summary>
    public long? IssueTrackingProjectId { get; set; }
    /// <summary>issue type ใน IssueTracking</summary>
    public int IssueTypeId { get; set; } = 1;
}

/// <summary>สร้างโอกาสการขาย</summary>
public sealed class CreateOpportunityRequest
{
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>ชื่อ</summary>
    [Required, StringLength(300, MinimumLength = 3)] public string Title { get; set; } = string.Empty;
    /// <summary>มูลค่า</summary>
    [Range(0, 1_000_000_000_000)] public decimal Amount { get; set; }
    /// <summary>ความน่าจะเป็นที่เซลส์ประเมิน</summary>
    [Range(0, 100)] public int? Probability { get; set; }
    /// <summary>คาดว่าจะปิด</summary>
    public DateOnly ExpectedCloseDate { get; set; }
    /// <summary>ที่มา</summary>
    [StringLength(60)] public string? Source { get; set; }
    /// <summary>ผู้รับผิดชอบ</summary>
    [StringLength(128)] public string? OwnerSubject { get; set; }
}

/// <summary>ย้าย stage</summary>
public sealed class MoveStageRequest
{
    /// <summary>Version ที่อ่านล่าสุด</summary>
    [Range(1, long.MaxValue)] public long Version { get; set; }
    /// <summary>stage ใหม่</summary>
    public PipelineStage Stage { get; set; }
    /// <summary>มูลค่าใหม่ (ถ้าเปลี่ยน)</summary>
    [Range(0, 1_000_000_000_000)] public decimal? Amount { get; set; }
    /// <summary>เหตุผลที่แพ้ (บังคับเมื่อ Lost)</summary>
    [StringLength(500)] public string? LostReason { get; set; }
}

/// <summary>สร้าง/แก้ไขนโยบาย SLA พร้อมระดับ severity</summary>
public sealed class SlaPolicyRequest
{
    /// <summary>ลูกค้า (null = นโยบายมาตรฐาน)</summary>
    public long? CustomerId { get; set; }
    /// <summary>Version (แก้ไขเท่านั้น)</summary>
    public long? Version { get; set; }
    /// <summary>ชื่อ</summary>
    [Required, StringLength(150, MinimumLength = 2)] public string Name { get; set; } = string.Empty;
    /// <summary>คำอธิบาย</summary>
    [StringLength(2000)] public string? Description { get; set; }
    /// <summary>มีผลตั้งแต่</summary>
    public DateOnly ValidFrom { get; set; }
    /// <summary>มีผลถึง</summary>
    public DateOnly? ValidTo { get; set; }
    /// <summary>เขตเวลา</summary>
    [StringLength(64)] public string TimeZoneId { get; set; } = "Asia/Bangkok";
    /// <summary>เริ่มงาน HH:mm</summary>
    public TimeOnly BusinessStart { get; set; } = new(8, 30);
    /// <summary>เลิกงาน HH:mm</summary>
    public TimeOnly BusinessEnd { get; set; } = new(17, 30);
    /// <summary>วันทำการ (bit0 = จันทร์)</summary>
    [Range(1, 127)] public int WorkingDaysMask { get; set; } = 31;
    /// <summary>วันหยุดพิเศษ</summary>
    [MaxLength(400)] public List<DateOnly> Holidays { get; set; } = [];
    /// <summary>เกณฑ์ใกล้ผิด SLA 0.5..0.99</summary>
    [Range(0.5, 0.99)] public double AtRiskThreshold { get; set; } = 0.8;
    /// <summary>ระดับ severity (อย่างน้อย 1)</summary>
    [MinLength(1), MaxLength(20)] public List<SeverityLevelRequest> Levels { get; set; } = [];
}

/// <summary>ระดับ severity</summary>
public sealed class SeverityLevelRequest
{
    /// <summary>รหัส เช่น S1</summary>
    [Required, StringLength(20, MinimumLength = 1)] public string Code { get; set; } = string.Empty;
    /// <summary>ชื่อ</summary>
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    /// <summary>คำอธิบาย</summary>
    [StringLength(1000)] public string? Description { get; set; }
    /// <summary>อันดับมาตรฐาน 1..5</summary>
    [Range(1, 5)] public int Rank { get; set; }
    /// <summary>เวลาตอบ (นาที)</summary>
    [Range(1, 525_600)] public int ResponseMinutes { get; set; }
    /// <summary>เวลาแก้ไข (นาที)</summary>
    [Range(1, 525_600)] public int ResolutionMinutes { get; set; }
    /// <summary>นับเฉพาะเวลาทำการ</summary>
    public bool BusinessHoursOnly { get; set; } = true;
    /// <summary>Priority ใน IssueTracking (ว่าง = แปลงจาก Rank)</summary>
    [StringLength(10)] public string? IssuePriority { get; set; }
}

/// <summary>ขอกำหนดเวลา SLA (IssueTracking เรียกตอนสร้าง issue)</summary>
public sealed class SlaCalculateRequest
{
    /// <summary>รหัสลูกค้า</summary>
    [Required, StringLength(30)] public string CustomerCode { get; set; } = string.Empty;
    /// <summary>รหัส severity</summary>
    [Required, StringLength(20)] public string SeverityCode { get; set; } = string.Empty;
    /// <summary>เวลาเริ่มนับ (null = ตอนนี้)</summary>
    public DateTimeOffset? StartedAt { get; set; }
}

/// <summary>ขอประเมินสถานะ SLA ของงาน</summary>
public sealed class SlaEvaluateRequest
{
    /// <summary>รหัสลูกค้า</summary>
    [Required, StringLength(30)] public string CustomerCode { get; set; } = string.Empty;
    /// <summary>รหัส severity</summary>
    [Required, StringLength(20)] public string SeverityCode { get; set; } = string.Empty;
    /// <summary>เวลาเปิดงาน</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>เวลาตอบครั้งแรก</summary>
    public DateTimeOffset? FirstRespondedAt { get; set; }
    /// <summary>เวลาแก้เสร็จ</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>บันทึกเหตุการณ์พฤติกรรม (bulk สูงสุด 10,000 รายการต่อครั้ง)</summary>
public sealed class EngagementEventRequest
{
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>ชนิด</summary>
    public EngagementKind Kind { get; set; }
    /// <summary>ช่องทาง</summary>
    public ChannelKind? Channel { get; set; }
    /// <summary>รหัสสินค้า/เนื้อหา</summary>
    [StringLength(60)] public string? ItemCode { get; set; }
    /// <summary>มูลค่า</summary>
    [Range(-1_000_000_000, 1_000_000_000)] public decimal Value { get; set; }
    /// <summary>เวลาเกิด (null = ตอนนี้)</summary>
    public DateTimeOffset? OccurredAt { get; set; }
}

/// <summary>สร้างสัญญา</summary>
public sealed class ContractRequest
{
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>เลขที่สัญญา</summary>
    [Required, StringLength(50)] public string ContractNo { get; set; } = string.Empty;
    /// <summary>ชื่อบริการ</summary>
    [Required, StringLength(300)] public string Title { get; set; } = string.Empty;
    /// <summary>เริ่ม</summary>
    public DateOnly StartDate { get; set; }
    /// <summary>สิ้นสุด</summary>
    public DateOnly EndDate { get; set; }
    /// <summary>มูลค่าต่อปี</summary>
    [Range(0, 1_000_000_000_000)] public decimal AnnualValue { get; set; }
    /// <summary>SLA policy</summary>
    public long? SlaPolicyId { get; set; }
}
