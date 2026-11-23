using System.Text.Json.Serialization;

namespace CustomerMgnt.Core.Domain;

/// <summary>
/// ลูกค้า 1 ราย — เป็นได้ทั้งบุคคลธรรมดา (<see cref="CustomerKind.Person"/>) และนิติบุคคล (<see cref="CustomerKind.Juristic"/>)
/// ใช้ตารางเดียว (party model) เพื่อให้ความสัมพันธ์บุคคล↔นิติบุคคล, แชต, กิจกรรม, SLA อ้างอิงได้ด้วย Id เดียว
/// </summary>
public sealed class CustomerDTO
{
    /// <summary>รหัสภายใน</summary>
    public long Id { get; set; }
    /// <summary>รหัสลูกค้าที่ใช้ข้ามระบบ เช่น <c>CUST-000123</c> (IssueTracking/ProjectMgnt อ้างถึงลูกค้าด้วยรหัสนี้)</summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>บุคคล หรือ นิติบุคคล</summary>
    public CustomerKind Kind { get; set; }
    /// <summary>ชื่อที่แสดง (บุคคล = ชื่อ-นามสกุล, นิติบุคคล = ชื่อบริษัท)</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>ชื่อจดทะเบียน (นิติบุคคล)</summary>
    public string? LegalName { get; set; }
    /// <summary>ชื่อ (บุคคล)</summary>
    public string? FirstName { get; set; }
    /// <summary>นามสกุล (บุคคล)</summary>
    public string? LastName { get; set; }
    /// <summary>เลขผู้เสียภาษี/บัตรประชาชนแบบปิดบัง เช่น <c>*********1234</c> (ค่าจริงเข้ารหัส ML-KEM ไว้ในฐานข้อมูล)</summary>
    public string? TaxIdMasked { get; set; }
    /// <summary>อีเมลหลัก</summary>
    public string? Email { get; set; }
    /// <summary>โทรศัพท์หลัก</summary>
    public string? Phone { get; set; }
    /// <summary>อุตสาหกรรม</summary>
    public string? Industry { get; set; }
    /// <summary>จำนวนพนักงาน (ขนาดธุรกิจ ใช้ใน Lead scoring)</summary>
    public int? EmployeeCount { get; set; }
    /// <summary>รายได้ต่อปี (บาท)</summary>
    public decimal? AnnualRevenue { get; set; }
    /// <summary>เว็บไซต์</summary>
    public string? Website { get; set; }
    /// <summary>ที่อยู่</summary>
    public string? Address { get; set; }
    /// <summary>จังหวัด</summary>
    public string? Province { get; set; }
    /// <summary>ประเทศ (ISO 3166-1 alpha-2)</summary>
    public string Country { get; set; } = "TH";
    /// <summary>เขตเวลา IANA เช่น <c>Asia/Bangkok</c> (ใช้คำนวณ SLA และเวลาส่งข้อความ)</summary>
    public string TimeZoneId { get; set; } = "Asia/Bangkok";
    /// <summary>สถานะ</summary>
    public CustomerStatus Status { get; set; } = CustomerStatus.Prospect;
    /// <summary>ผู้ดูแลลูกค้า (subject ของผู้ใช้ใน AuthServer)</summary>
    public string? OwnerSubject { get; set; }
    /// <summary>ป้ายกำกับ คั่นด้วย comma</summary>
    public string? Tags { get; set; }
    /// <summary>กลุ่มลูกค้าล่าสุดที่ระบบจัดให้อัตโนมัติ (Dynamic segmentation)</summary>
    public string? Segment { get; set; }
    /// <summary>ลูกค้ายินยอมรับข้อความการตลาด (PDPA)</summary>
    public bool MarketingConsent { get; set; }
    /// <summary>เวลาสร้าง (UTC)</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>เวลาแก้ไขล่าสุด (UTC)</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Optimistic concurrency token — ส่งค่าที่อ่านล่าสุดกลับมาตอนแก้ไข</summary>
    public long Version { get; set; }

    /// <summary>เลขผู้เสียภาษีที่เข้ารหัสแล้ว (JSON ของ EnvelopeDTO) — ไม่ส่งออกทาง API</summary>
    [JsonIgnore] public string? TaxIdEnvelope { get; set; }
    /// <summary>Blind index (HMAC-SHA256) ของเลขผู้เสียภาษีสำหรับตรวจซ้ำโดยไม่ต้องถอดรหัส — ไม่ส่งออกทาง API</summary>
    [JsonIgnore] public string? TaxIdHash { get; set; }
}

/// <summary>
/// ความสัมพันธ์ระหว่างบุคคลกับนิติบุคคล: อยู่ในตำแหน่งอะไร ตั้งแต่วันไหนถึงวันไหน
/// บุคคล 1 คนมีได้หลายความสัมพันธ์ (หลายนิติบุคคล หรือหลายตำแหน่งในนิติบุคคลเดียวกันต่างช่วงเวลา)
/// </summary>
public sealed class PartyRelationshipDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>บุคคล (<see cref="CustomerKind.Person"/>)</summary>
    public long PersonId { get; set; }
    /// <summary>นิติบุคคล (<see cref="CustomerKind.Juristic"/>)</summary>
    public long OrganizationId { get; set; }
    /// <summary>ตำแหน่ง เช่น CEO, IT Manager, ผู้ประสานงาน</summary>
    public string Position { get; set; } = string.Empty;
    /// <summary>แผนก</summary>
    public string? Department { get; set; }
    /// <summary>เป็นผู้ติดต่อหลักของนิติบุคคล</summary>
    public bool IsPrimaryContact { get; set; }
    /// <summary>เป็นผู้มีอำนาจตัดสินใจ (ใช้ใน Lead scoring / Next best action)</summary>
    public bool IsDecisionMaker { get; set; }
    /// <summary>เริ่มตั้งแต่</summary>
    public DateOnly ValidFrom { get; set; }
    /// <summary>ถึงวันที่ (null = ยังดำรงตำแหน่งอยู่)</summary>
    public DateOnly? ValidTo { get; set; }
    /// <summary>ชื่อบุคคล (read model)</summary>
    public string? PersonName { get; set; }
    /// <summary>ชื่อนิติบุคคล (read model)</summary>
    public string? OrganizationName { get; set; }

    /// <summary>ตรวจว่าความสัมพันธ์มีผล ณ วันที่กำหนด</summary>
    /// <param name="date">วันที่ต้องการตรวจ</param>
    /// <returns>true ถ้า <paramref name="date"/> อยู่ในช่วง [ValidFrom, ValidTo]</returns>
    public bool IsActiveOn(DateOnly date) => ValidFrom <= date && (ValidTo is null || date <= ValidTo.Value);
}

/// <summary>ตัวตนของลูกค้าในแต่ละช่องทาง เช่น LINE userId, อีเมล, Facebook PSID (ใช้จับคู่ข้อความขาเข้ากับลูกค้า)</summary>
public sealed class ChannelIdentityDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>ช่องทาง</summary>
    public ChannelKind Channel { get; set; }
    /// <summary>รหัสผู้ใช้ในช่องทางนั้น (normalize เป็นตัวพิมพ์เล็กสำหรับอีเมล)</summary>
    public string ExternalId { get; set; } = string.Empty;
    /// <summary>ชื่อที่แสดงในช่องทางนั้น</summary>
    public string? DisplayName { get; set; }
    /// <summary>ยืนยันแล้วว่าเป็นลูกค้ารายนี้</summary>
    public bool Verified { get; set; }
}

/// <summary>การเชื่อมลูกค้ากับข้อมูลในระบบอื่น (project ใน ProjectMgnt / IssueTracking / RequirementMgnt)</summary>
public sealed class ExternalLinkDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>ระบบปลายทาง</summary>
    public ExternalSystem System { get; set; }
    /// <summary>ชนิดข้อมูล เช่น <c>project</c>, <c>issue</c>, <c>meeting</c>, <c>artifact</c></summary>
    public string EntityType { get; set; } = "project";
    /// <summary>รหัสในระบบปลายทาง</summary>
    public string ExternalId { get; set; } = string.Empty;
    /// <summary>ชื่อที่แสดง</summary>
    public string? Title { get; set; }
    /// <summary>คำขอที่ทำให้เกิด link นี้ (ถ้ามี)</summary>
    public long? CustomerRequestId { get; set; }
    /// <summary>เวลาสร้าง</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>สรุปลูกค้าแบบ 360 องศา (ข้อมูลหลัก + ความสัมพันธ์ + งาน + คะแนน)</summary>
/// <param name="Customer">ข้อมูลลูกค้า</param>
/// <param name="Relationships">ความสัมพันธ์บุคคล↔นิติบุคคลที่ยังมีผล</param>
/// <param name="Identities">ตัวตนในแต่ละช่องทาง</param>
/// <param name="OpenConversations">จำนวนการสนทนาที่ยังเปิด</param>
/// <param name="OverdueActivities">จำนวนกิจกรรมที่เลยกำหนด</param>
/// <param name="OpenRequests">จำนวนคำขอที่ยังไม่ปิด</param>
/// <param name="OpenOpportunities">โอกาสการขายที่ยังเปิด</param>
/// <param name="Links">ลิงก์ไประบบอื่น</param>
/// <param name="SlaPolicyName">ชื่อ SLA policy ที่มีผล (null = ไม่มี)</param>
public sealed record Customer360DTO(
    CustomerDTO Customer,
    IReadOnlyList<PartyRelationshipDTO> Relationships,
    IReadOnlyList<ChannelIdentityDTO> Identities,
    int OpenConversations,
    int OverdueActivities,
    int OpenRequests,
    IReadOnlyList<OpportunityDTO> OpenOpportunities,
    IReadOnlyList<ExternalLinkDTO> Links,
    string? SlaPolicyName);

/// <summary>โปรไฟล์ผู้ใช้ภายใน (พนักงานขาย/ซัพพอร์ต)</summary>
public sealed class UserProfileDTO
{
    /// <summary>subject ใน AuthServer</summary>
    public string Subject { get; set; } = string.Empty;
    /// <summary>ชื่อที่แสดง</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>รหัสผู้ใช้ใน MessageHub (ใช้ส่งแจ้งเตือน/เป็น organizer ของการประชุม)</summary>
    public Guid? MessageHubUserId { get; set; }
}
