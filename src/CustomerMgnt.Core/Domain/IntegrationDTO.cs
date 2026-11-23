namespace CustomerMgnt.Core.Domain;

// Read model ของข้อมูลจากระบบอื่น — มีเฉพาะ field ที่ CustomerMgnt ใช้ (JSON field อื่นถูกละไว้ ทำให้ทนต่อการเพิ่ม field ฝั่งโน้น)

/// <summary>Issue จาก IssueTracking (ย่อ)</summary>
public sealed class ExternalIssueDTO
{
    /// <summary>รหัส issue</summary>
    public long Id { get; set; }
    /// <summary>project ใน IssueTracking</summary>
    public long ProjectId { get; set; }
    /// <summary>key ของ project</summary>
    public string? ProjectKey { get; set; }
    /// <summary>หัวข้อ</summary>
    public string Subject { get; set; } = string.Empty;
    /// <summary>สถานะ</summary>
    public string? StatusName { get; set; }
    /// <summary>ปิดแล้วหรือยัง</summary>
    public bool IsClosed { get; set; }
    /// <summary>Priority (Low..Immediate)</summary>
    public string? Priority { get; set; }
    /// <summary>รหัสลูกค้า (เพิ่มใน IssueTracking สำหรับ SLA)</summary>
    public string? CustomerCode { get; set; }
    /// <summary>รหัส severity ของลูกค้า</summary>
    public string? SeverityCode { get; set; }
    /// <summary>อันดับ severity มาตรฐาน 1..5</summary>
    public int? SeverityRank { get; set; }
    /// <summary>ต้องตอบภายใน</summary>
    public DateTimeOffset? ResponseDueAt { get; set; }
    /// <summary>ต้องแก้เสร็จภายใน</summary>
    public DateTimeOffset? ResolutionDueAt { get; set; }
    /// <summary>ตอบครั้งแรกเมื่อ</summary>
    public DateTimeOffset? FirstRespondedAt { get; set; }
    /// <summary>ผู้รับผิดชอบ</summary>
    public string? AssigneeName { get; set; }
    /// <summary>สร้างเมื่อ</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>ปิดเมื่อ</summary>
    public DateTimeOffset? ClosedAt { get; set; }
}

/// <summary>ช่วงอายุ (aging) ของ issue ค้าง</summary>
/// <param name="Label">ชื่อช่วง เช่น "0-7 วัน"</param>
/// <param name="MinDays">วันต่ำสุด (รวม)</param>
/// <param name="MaxDays">วันสูงสุด (ไม่รวม, null = ไม่จำกัด)</param>
/// <param name="BySeverity">จำนวนแยกตาม severity</param>
/// <param name="Total">รวม</param>
public sealed record AgingBucketDTO(string Label, int MinDays, int? MaxDays, IReadOnlyDictionary<string, int> BySeverity, int Total);

/// <summary>สรุป issue ค้างของลูกค้า แยก Aging × Severity พร้อมสถานะ SLA</summary>
/// <param name="CustomerCode">รหัสลูกค้า (null = ทุกลูกค้าที่ผู้ใช้เห็น)</param>
/// <param name="OpenIssues">จำนวน issue ค้าง</param>
/// <param name="Buckets">ตาราง aging × severity</param>
/// <param name="SlaBreached">จำนวนที่ผิด SLA แล้ว</param>
/// <param name="SlaAtRisk">จำนวนที่ใกล้ผิด SLA</param>
/// <param name="AverageAgeDays">อายุเฉลี่ย (วัน)</param>
/// <param name="Oldest">issue ที่ค้างนานที่สุด (สูงสุด 10)</param>
public sealed record IssueAgingSummaryDTO(string? CustomerCode, int OpenIssues, IReadOnlyList<AgingBucketDTO> Buckets, int SlaBreached, int SlaAtRisk, double AverageAgeDays, IReadOnlyList<ExternalIssueDTO> Oldest);

/// <summary>Project จาก ProjectMgnt (ย่อ) พร้อม KPI</summary>
public sealed class ExternalProjectDTO
{
    /// <summary>รหัส project</summary>
    public long Id { get; set; }
    /// <summary>รหัสย่อ</summary>
    public string? Code { get; set; }
    /// <summary>ชื่อ</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>phase ของ ProjectMgnt (Initiation, Planning, Execution, ..., Closed)</summary>
    public string? Phase { get; set; }
    /// <summary>ความคืบหน้าจริง %</summary>
    public double? ActualProgress { get; set; }
    /// <summary>ความคืบหน้าตามแผน %</summary>
    public double? PlannedProgress { get; set; }
    /// <summary>สุขภาพ Green/Amber/Red</summary>
    public string? Health { get; set; }
    /// <summary>วันเริ่ม</summary>
    public DateOnly? StartDate { get; set; }
    /// <summary>คาดว่าจะ go-live</summary>
    public DateOnly? ForecastEndDate { get; set; }

    /// <summary>ปิดแล้วหรือยัง</summary>
    public bool IsClosed => string.Equals(Phase, "Closed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>สรุปโครงการของลูกค้า (Active / Closed + progress)</summary>
/// <param name="CustomerCode">รหัสลูกค้า</param>
/// <param name="Active">โครงการที่ยังทำอยู่</param>
/// <param name="Closed">โครงการที่ปิดแล้ว</param>
/// <param name="AverageProgress">ความคืบหน้าเฉลี่ยของโครงการที่ active</param>
/// <param name="AtRisk">จำนวนโครงการ health = Red/Amber</param>
public sealed record ProjectPortfolioDTO(string CustomerCode, IReadOnlyList<ExternalProjectDTO> Active, IReadOnlyList<ExternalProjectDTO> Closed, double AverageProgress, int AtRisk);

/// <summary>การประชุมจาก MessageHub (ย่อ)</summary>
public sealed class ExternalMeetingDTO
{
    /// <summary>รหัส</summary>
    public Guid Id { get; set; }
    /// <summary>หัวข้อ</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>เริ่ม (UTC)</summary>
    public DateTimeOffset StartUtc { get; set; }
    /// <summary>จบ (UTC)</summary>
    public DateTimeOffset EndUtc { get; set; }
    /// <summary>สถานะ Draft/Scheduled/Completed/Cancelled</summary>
    public string? Status { get; set; }
    /// <summary>วาระ</summary>
    public List<ExternalAgendaItemDTO> Agenda { get; set; } = [];
}

/// <summary>วาระการประชุม (ย่อ)</summary>
public sealed class ExternalAgendaItemDTO
{
    /// <summary>รหัส</summary>
    public Guid Id { get; set; }
    /// <summary>หัวข้อ</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>Pending/InProgress/Done/CarriedOver/Dropped</summary>
    public string? Status { get; set; }
    /// <summary>ข้อสรุป</summary>
    public string? Conclusion { get; set; }
    /// <summary>ผู้รับผิดชอบ</summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>ยังค้างอยู่หรือไม่</summary>
    public bool IsPending => Status is null or "Pending" or "InProgress" or "CarriedOver";
}

/// <summary>Agenda และงานค้างของลูกค้า (รวมจาก MessageHub + กิจกรรมใน CustomerMgnt)</summary>
/// <param name="CustomerCode">รหัสลูกค้า</param>
/// <param name="UpcomingMeetings">ประชุมที่จะถึง</param>
/// <param name="PendingAgendaItems">วาระที่ยังไม่ปิดจากการประชุมที่ผ่านมา</param>
/// <param name="OpenActivities">กิจกรรม/งานติดตามที่ยังไม่ทำ (เรียงตามกำหนด)</param>
/// <param name="OverdueCount">จำนวนที่เลยกำหนด</param>
public sealed record CustomerAgendaDTO(string CustomerCode, IReadOnlyList<ExternalMeetingDTO> UpcomingMeetings, IReadOnlyList<ExternalAgendaItemDTO> PendingAgendaItems, IReadOnlyList<ActivityDTO> OpenActivities, int OverdueCount);

/// <summary>ผลการส่งต่อคำขอไปยังระบบอื่น</summary>
/// <param name="RequestId">คำขอ</param>
/// <param name="Links">ลิงก์ที่สร้างขึ้น (project / artifacts / issue)</param>
/// <param name="Signature">ลายเซ็น PQC ของเนื้อหาที่ส่งต่อ</param>
/// <param name="Warnings">ระบบที่ส่งไม่สำเร็จ (ทำซ้ำได้: ระบบที่สำเร็จแล้วจะไม่ถูกสร้างซ้ำ)</param>
public sealed record ForwardResultDTO(long RequestId, IReadOnlyList<ExternalLinkDTO> Links, string Signature, IReadOnlyList<string> Warnings);
