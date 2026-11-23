using CustomerMgnt.Core.Domain;

namespace CustomerMgnt.Core.Integration;

// สัญญาการเชื่อมต่อระบบภายนอก (ลงท้าย Gateway) — Core เรียกผ่าน interface, CustomerMgnt.Integrations implement ด้วย HttpClient
// Implementation ต้องแปลงข้อผิดพลาดของเครือข่าย/HTTP 5xx เป็น DependencyUnavailableException และ 404 เป็น null/NotFound

/// <summary>mrpichet/issuetracking</summary>
public interface IIssueTrackingGateway
{
    /// <summary>ค้นหา issue ของลูกค้า (ใช้ field <c>customer</c> ที่เพิ่มใน IssueTracking) หรือของ project ที่ระบุ</summary>
    /// <param name="customerCode">รหัสลูกค้า (null = ไม่กรอง)</param>
    /// <param name="projectIds">project ใน IssueTracking (ว่าง = ทุก project ที่ผู้เรียกเห็น)</param>
    /// <param name="openOnly">เฉพาะที่ยังไม่ปิด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ issue</returns>
    Task<IReadOnlyList<ExternalIssueDTO>> SearchIssuesAsync(string? customerCode, IReadOnlyCollection<long> projectIds, bool openOnly, CancellationToken ct);

    /// <summary>สร้าง issue พร้อมรหัสลูกค้าและ severity (IssueTracking คำนวณกำหนดเวลา SLA ผ่าน CustomerMgnt)</summary>
    /// <param name="projectId">project</param>
    /// <param name="issueTypeId">issue type</param>
    /// <param name="subject">หัวข้อ</param>
    /// <param name="description">รายละเอียด</param>
    /// <param name="priority">Low/Normal/High/Urgent/Immediate</param>
    /// <param name="customerCode">รหัสลูกค้า</param>
    /// <param name="severityCode">รหัส severity ของลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส issue</returns>
    Task<long> CreateIssueAsync(long projectId, int issueTypeId, string subject, string? description, string priority, string customerCode, string? severityCode, CancellationToken ct);
}

/// <summary>mrpichet/ProjectMgnt</summary>
public interface IProjectMgntGateway
{
    /// <summary>อ่าน project พร้อม KPI (progress/health)</summary>
    /// <param name="projectIds">project</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>project ที่ผู้เรียกมีสิทธิ์เห็น</returns>
    Task<IReadOnlyList<ExternalProjectDTO>> GetProjectsAsync(IReadOnlyCollection<long> projectIds, CancellationToken ct);

    /// <summary>สร้าง project</summary>
    /// <param name="code">รหัส</param>
    /// <param name="name">ชื่อ</param>
    /// <param name="description">รายละเอียด</param>
    /// <param name="methodology">Waterfall/Agile/...</param>
    /// <param name="projectManagerId">PM</param>
    /// <param name="startDate">วันเริ่ม</param>
    /// <param name="budget">งบประมาณ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส project</returns>
    Task<long> CreateProjectAsync(string code, string name, string? description, string methodology, long projectManagerId, DateOnly startDate, decimal budget, CancellationToken ct);
}

/// <summary>mrpichet/requirementMgnt</summary>
public interface IRequirementMgntGateway
{
    /// <summary>สร้าง project (พร้อม template มาตรฐาน)</summary>
    /// <param name="key">key A-Z0-9</param>
    /// <param name="name">ชื่อ</param>
    /// <param name="description">รายละเอียด</param>
    /// <param name="standard">มาตรฐาน เช่น Iso29110</param>
    /// <param name="startDate">เริ่ม</param>
    /// <param name="targetEndDate">สิ้นสุด</param>
    /// <param name="budget">งบ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส project</returns>
    Task<long> CreateProjectAsync(string key, string name, string? description, string standard, DateOnly startDate, DateOnly targetEndDate, decimal budget, CancellationToken ct);

    /// <summary>module แรกของ project (ใช้เป็นที่วาง requirement จาก TOR)</summary>
    /// <param name="projectId">project</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส module หรือ null</returns>
    Task<long?> GetFirstModuleIdAsync(long projectId, CancellationToken ct);

    /// <summary>สร้าง requirement (artifact ชนิด StakeholderRequirement)</summary>
    /// <param name="moduleId">module</param>
    /// <param name="title">หัวข้อ</param>
    /// <param name="content">เนื้อหา (plain text)</param>
    /// <param name="priority">Low/Medium/High/Critical</param>
    /// <param name="attributes">attribute เพิ่มเติม (เช่น TOR item no, customer code)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>(รหัส artifact, key)</returns>
    Task<(long Id, string Key)> CreateRequirementAsync(long moduleId, string title, string content, string priority, IReadOnlyDictionary<string, string> attributes, CancellationToken ct);
}

/// <summary>mrpichet/messagehub</summary>
public interface IMessageHubGateway
{
    /// <summary>สร้างการประชุม (ส่งคำเชิญและตั้ง reminder ให้ด้วย)</summary>
    /// <param name="title">หัวข้อ</param>
    /// <param name="description">รายละเอียด</param>
    /// <param name="organizerId">ผู้จัด (ผู้ใช้ MessageHub)</param>
    /// <param name="startUtc">เริ่ม</param>
    /// <param name="endUtc">จบ</param>
    /// <param name="attendees">ผู้เข้าร่วม</param>
    /// <param name="agenda">วาระ</param>
    /// <param name="reminderMinutes">แจ้งเตือนก่อนกี่นาที</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัสการประชุม</returns>
    Task<Guid> CreateMeetingAsync(string title, string? description, Guid organizerId, DateTimeOffset startUtc, DateTimeOffset endUtc, IReadOnlyList<Guid> attendees, IReadOnlyList<string> agenda, int? reminderMinutes, CancellationToken ct);

    /// <summary>อ่านการประชุมพร้อมวาระ</summary>
    /// <param name="meetingId">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>การประชุม หรือ null</returns>
    Task<ExternalMeetingDTO?> GetMeetingAsync(Guid meetingId, CancellationToken ct);

    /// <summary>ส่งข้อความหาลูกค้าผ่านช่องทาง (LINE, Email, SMS, ...)</summary>
    /// <param name="channel">ช่องทาง</param>
    /// <param name="recipient">ผู้รับ (LINE userId / อีเมล / เบอร์)</param>
    /// <param name="subject">หัวข้อ</param>
    /// <param name="body">ข้อความ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>true เมื่อส่ง/เข้าคิวสำเร็จ, false เมื่อช่องทางนี้ MessageHub ไม่รองรับ</returns>
    Task<bool> SendToCustomerAsync(ChannelKind channel, string recipient, string? subject, string body, CancellationToken ct);

    /// <summary>แจ้งเตือนพนักงาน (ผ่านช่องทางที่ผู้ใช้ตั้งไว้ใน MessageHub)</summary>
    /// <param name="userId">ผู้ใช้ MessageHub</param>
    /// <param name="subject">หัวข้อ</param>
    /// <param name="body">ข้อความ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Task</returns>
    Task NotifyUserAsync(Guid userId, string subject, string body, CancellationToken ct);
}
