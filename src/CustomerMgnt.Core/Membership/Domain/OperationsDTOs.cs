using System.ComponentModel.DataAnnotations;

namespace CustomerMgnt.Core.Membership.Domain;

/// <summary>ขั้นอนุมัติ</summary>
public sealed class ApprovalStepDTO
{
    public int Level { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecidedByName { get; set; }
    public ApprovalDecision? Decision { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>คำขออนุมัติ (maker-checker ผู้ยื่นอนุมัติเองไม่ได้)</summary>
public sealed class ApprovalRequestDTO
{
    public long Id { get; set; }
    public ApprovalType Type { get; set; }
    public long EntityId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int LevelsRequired { get; set; } = 1;
    public int LevelsApproved { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    /// <summary>ข้อมูลที่ใช้ตอนอนุมัติ (เช่น MergeRequest)</summary>
    public string? PayloadJson { get; set; }
    /// <summary>สิทธิ์ที่ผู้อนุมัติต้องมี</summary>
    public string RequiredPermission { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string? RequestedByName { get; set; }
    public string? RequestComment { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public List<ApprovalStepDTO> Steps { get; set; } = [];
    public long Version { get; set; }
}

/// <summary>ตัดสินคำขอ</summary>
public sealed class ApprovalDecisionRequest
{
    public ApprovalDecision Decision { get; set; }
    [StringLength(1000)] public string? Comment { get; set; }
}

/// <summary>ยกเลิกคำขอ</summary>
public sealed class ApprovalCancelRequest
{
    [StringLength(1000)] public string? Comment { get; set; }
}

/// <summary>ค้นหาคำขออนุมัติ</summary>
public sealed class ApprovalSearchCriteria
{
    public ApprovalStatus? Status { get; set; }
    public ApprovalType? Type { get; set; }
    /// <summary>เฉพาะที่ฉันยื่น</summary>
    public bool Mine { get; set; }
    internal string? RequestedBy { get; set; }
}

/// <summary>audit log (hash chain — แก้ไขย้อนหลังแล้วตรวจพบ)</summary>
public sealed class MembershipAuditDTO
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = string.Empty;
    public ActorType ActorType { get; set; }
    public string Action { get; set; } = string.Empty;
    /// <summary>เช่น member:12, campaign:7</summary>
    public string Target { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string? Ip { get; set; }
    public string? BeforeMasked { get; set; }
    public string? AfterMasked { get; set; }
    public string? PrevHash { get; set; }
    public string? Hash { get; set; }
}

/// <summary>ค้นหา audit</summary>
public sealed class AuditSearchCriteria
{
    [StringLength(200)] public string? Actor { get; set; }
    [StringLength(100)] public string? Action { get; set; }
    [StringLength(100)] public string? Target { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    [Range(1, 500)] public int PageSize { get; set; } = 50;
}

/// <summary>ข้อความที่รอส่งออก (transactional outbox)</summary>
public sealed class OutboxMessageDTO
{
    public long Id { get; set; }
    /// <summary>ไม่ซ้ำ — ปลายทางใช้กันประมวลผลซ้ำ</summary>
    public string EventId { get; set; } = string.Empty;
    /// <summary>เช่น member.registered, points.earned</summary>
    public string Topic { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public bool DeadLettered { get; set; }
    public string? LastError { get; set; }
}

/// <summary>ผลจำของ Idempotency-Key</summary>
public sealed class IdempotencyRecordDTO
{
    public string SourceSystem { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    /// <summary>SHA-256 ของ method + path + body</summary>
    public string RequestHash { get; set; } = string.Empty;
    /// <summary>0 = กำลังทำ</summary>
    public int StatusCode { get; set; }
    public string? ResponseJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>งานเบื้องหลังทั่วไป</summary>
public sealed class JobDTO
{
    public string JobId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public string? Message { get; set; }
}

/// <summary>งานส่งออกรายงาน</summary>
public sealed class ExportJobDTO
{
    public long Id { get; set; }
    public string Report { get; set; } = string.Empty;
    public string Format { get; set; } = "Csv";
    public string FiltersJson { get; set; } = "{}";
    public JobStatus Status { get; set; }
    public int RowCount { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Error { get; set; }
    /// <summary>เนื้อไฟล์ (ไม่ส่งออกทาง API รายการ)</summary>
    [System.Text.Json.Serialization.JsonIgnore] public byte[]? Content { get; set; }
}

/// <summary>ขอส่งออกรายงาน</summary>
public sealed class ExportRequest
{
    /// <summary>members | points | promotions | transactions</summary>
    [Required, RegularExpression("^(members|points|promotions|transactions)$")] public string Report { get; set; } = string.Empty;
    public ReportFilter Filters { get; set; } = new();
    /// <summary>Csv | Xlsx (Xlsx ส่งออกเป็น CSV UTF-8 ที่ Excel เปิดได้)</summary>
    [RegularExpression("^(Csv|Xlsx)$")] public string Format { get; set; } = "Csv";
}

/// <summary>ตัวกรองรายงาน</summary>
public sealed class ReportFilter
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    [StringLength(20)] public string? TierCode { get; set; }
    public MemberStatus? Status { get; set; }
    public long? CampaignId { get; set; }
    [StringLength(30)] public string? BranchCode { get; set; }
}

/// <summary>รายงานแบบตาราง (≤ 5,000 แถว)</summary>
public sealed class ReportDTO
{
    public string Title { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = [];
    public List<List<string>> Rows { get; set; } = [];
    public Dictionary<string, decimal> Totals { get; set; } = [];
    public bool Truncated { get; set; }
}

/// <summary>ตัวเลขหน้าแรก</summary>
public sealed class DashboardDTO
{
    public int TotalMembers { get; set; }
    public int ActiveMembers { get; set; }
    public int NewMembers30d { get; set; }
    public Dictionary<string, int> MembersByTier { get; set; } = [];
    public long PointsOutstanding { get; set; }
    public long PointsEarned30d { get; set; }
    public long PointsRedeemed30d { get; set; }
    public int ActiveCampaigns { get; set; }
    public int PendingApprovals { get; set; }
    public decimal Sales30d { get; set; }
    public int Transactions30d { get; set; }
    public int OutboxBacklog { get; set; }
    public int DeadLetters { get; set; }
}

/// <summary>ส่งข้อความแคมเปญผ่าน MessageHub</summary>
public sealed class CampaignMessageRequest
{
    public long? SegmentId { get; set; }
    public List<long>? MemberIds { get; set; }
    public MarketingChannel Channel { get; set; }
    [Required, StringLength(100)] public string TemplateCode { get; set; } = string.Empty;
    public Dictionary<string, string> Variables { get; set; } = [];
    public DateTimeOffset? ScheduledAt { get; set; }
    /// <summary>แคมเปญของข้อความ (ไม่บังคับ) — ข้ามสมาชิกกลุ่มควบคุมและบันทึกผู้รับสำหรับการวิเคราะห์</summary>
    [StringLength(30)] public string? CampaignCode { get; set; }
}

/// <summary>งานส่งข้อความ</summary>
public sealed class MessageJobDTO
{
    public long Id { get; set; }
    public long? SegmentId { get; set; }
    public MarketingChannel Channel { get; set; }
    public string? CampaignCode { get; set; }
    public string TemplateCode { get; set; } = string.Empty;
    public string VariablesJson { get; set; } = "{}";
    public string? MemberIdsJson { get; set; }
    public JobStatus Status { get; set; }
    public int Recipients { get; set; }
    /// <summary>ข้ามเพราะไม่ยินยอมช่องทางนี้</summary>
    public int Skipped { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>ระบบต้นทางที่เรียก API (POS / integration) — client ใน AuthServer</summary>
public sealed class IntegrationClientDTO
{
    [Required, StringLength(100)] public string ClientId { get; set; } = string.Empty;
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    [Range(1, 10_000)] public int RateLimitPerSecond { get; set; } = 50;
    /// <summary>แจ้งเตือนเมื่อ error ≥ % ใน 5 นาที</summary>
    [Range(1, 100)] public int AlertOnErrorRate { get; set; } = 20;
    public DateTimeOffset? LastSeenAt { get; set; }
}

/// <summary>บทบาท/สิทธิ์ที่แนะนำ (ใช้ตั้งค่าใน AuthServer)</summary>
public sealed class RoleTemplateDTO
{
    public string Role { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = [];
}
