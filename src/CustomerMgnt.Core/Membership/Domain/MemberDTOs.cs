using System.ComponentModel.DataAnnotations;

namespace CustomerMgnt.Core.Membership.Domain;

/// <summary>
/// แถวสมาชิกที่เก็บในฐานข้อมูล (ตาราง <c>mbr_member</c>) — ข้อมูลส่วนบุคคลเก็บแบบเข้ารหัส (envelope) + blind index สำหรับค้นหา
/// ไม่ส่งออกทาง API ตรง ๆ: service แปลงเป็น <see cref="MemberDTO"/> (mask) เสมอ
/// </summary>
public sealed class MemberEntity
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>เลขสมาชิก เช่น M2600001</summary>
    public string MemberNo { get; set; } = string.Empty;
    /// <summary>ชื่อ</summary>
    public string FirstName { get; set; } = string.Empty;
    /// <summary>นามสกุล</summary>
    public string LastName { get; set; } = string.Empty;
    /// <summary>เบอร์โทร (envelope JSON)</summary>
    public string? PhoneEnvelope { get; set; }
    /// <summary>blind index ของเบอร์ (HMAC hex)</summary>
    public string? PhoneHash { get; set; }
    /// <summary>เบอร์แบบ mask เช่น 081-xxx-5678 (แสดงโดยไม่ต้องถอดรหัส)</summary>
    public string? PhoneMasked { get; set; }
    /// <summary>อีเมล (envelope JSON)</summary>
    public string? EmailEnvelope { get; set; }
    /// <summary>blind index ของอีเมล</summary>
    public string? EmailHash { get; set; }
    /// <summary>อีเมลแบบ mask</summary>
    public string? EmailMasked { get; set; }
    /// <summary>วันเกิด (envelope JSON)</summary>
    public string? BirthDateEnvelope { get; set; }
    /// <summary>เดือน-วันเกิด (MM-dd) สำหรับแคมเปญวันเกิดและ mask</summary>
    public string? BirthMonthDay { get; set; }
    /// <summary>ปีเกิด (ใช้คำนวณอายุใน segment)</summary>
    public int? BirthYear { get; set; }
    /// <summary>เพศ</summary>
    public Gender Gender { get; set; }
    /// <summary>จังหวัด</summary>
    public string? Province { get; set; }
    /// <summary>สถานะ</summary>
    public MemberStatus Status { get; set; }
    /// <summary>เหตุผลของสถานะล่าสุด</summary>
    public string? StatusReason { get; set; }
    /// <summary>ช่องทางที่สมัคร</summary>
    public RegisteredChannel RegisteredChannel { get; set; }
    /// <summary>Tier ปัจจุบัน</summary>
    public string? TierCode { get; set; }
    /// <summary>ได้ Tier นี้เมื่อ</summary>
    public DateTimeOffset? TierSince { get; set; }
    /// <summary>วันตรวจคง/ลด Tier</summary>
    public DateTimeOffset? TierReviewAt { get; set; }
    /// <summary>ลูกค้าใน CustomerMgnt (null = รอระบบสร้าง)</summary>
    public string? CustomerCode { get; set; }
    /// <summary><c>sub</c> ของ token AuthServer ที่ผูกกับสมาชิก (Portal)</summary>
    public string? AuthSubject { get; set; }
    /// <summary>ยินยอมรับข่าวสาร</summary>
    public bool MarketingConsent { get; set; }
    /// <summary>ช่องทางที่ยินยอม (คั่นด้วย ,)</summary>
    public string? MarketingChannels { get; set; }
    /// <summary>ซื้อล่าสุด</summary>
    public DateTimeOffset? LastPurchaseAt { get; set; }
    /// <summary>แคมเปญที่พาสมัคร</summary>
    public string? SourceCampaignCode { get; set; }
    /// <summary>สร้างเมื่อ</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>แก้ไขล่าสุด</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Optimistic concurrency token</summary>
    public long Version { get; set; }
    /// <summary>คะแนนที่ใช้ได้ (เติมโดยการค้นหาเท่านั้น ไม่ได้เก็บในตารางนี้)</summary>
    public int PointsAvailable { get; set; }

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public MemberEntity Clone() => (MemberEntity)MemberwiseClone();
}

/// <summary>Tier ของสมาชิกใน <see cref="MemberDTO"/></summary>
public sealed class MemberTierInfoDTO
{
    /// <summary>รหัส</summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>ชื่อ</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>ได้ Tier เมื่อ</summary>
    public DateTimeOffset? Since { get; set; }
    /// <summary>วันตรวจครบรอบ</summary>
    public DateTimeOffset? ReviewAt { get; set; }
}

/// <summary>สมาชิก (ข้อมูลส่วนบุคคลแบบ mask เว้นแต่เป็นข้อมูลของตัวเองใน Portal)</summary>
public sealed class MemberDTO
{
    public long Id { get; set; }
    public string MemberNo { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>เบอร์แบบ mask เช่น 081-xxx-5678 (Portal ของตัวเอง = เบอร์เต็ม)</summary>
    public string? PhoneMasked { get; set; }
    public string? EmailMasked { get; set; }
    /// <summary>วันเกิดแบบ mask เช่น xxxx-05-14</summary>
    public string? BirthDateMasked { get; set; }
    public Gender Gender { get; set; }
    public string? Province { get; set; }
    public MemberStatus Status { get; set; }
    public string? StatusReason { get; set; }
    public RegisteredChannel RegisteredChannel { get; set; }
    public MemberTierInfoDTO? Tier { get; set; }
    public PointBalanceDTO? Points { get; set; }
    public string? CustomerCode { get; set; }
    public bool HasPortalAccount { get; set; }
    public bool MarketingConsent { get; set; }
    public string? SourceCampaignCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

/// <summary>แถวในผลค้นหาสมาชิก</summary>
public sealed class MemberSummaryDTO
{
    public long Id { get; set; }
    public string MemberNo { get; set; } = string.Empty;
    /// <summary>ชื่อ + อักษรแรกของนามสกุล เช่น "สมชาย ใ."</summary>
    public string DisplayName { get; set; } = string.Empty;
    public string? PhoneMasked { get; set; }
    public MemberStatus Status { get; set; }
    public string? TierCode { get; set; }
    public int PointsAvailable { get; set; }
    public string? CustomerCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>ข้อมูลส่วนบุคคลเต็ม (ต้องมี member.pii และบันทึก audit)</summary>
public sealed class MemberPiiDTO
{
    public long MemberId { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public DateOnly? BirthDate { get; set; }
}

/// <summary>เงื่อนไขค้นหาสมาชิก (phone/email ค้นหาตรงทั้งค่าด้วย blind index)</summary>
public sealed class MemberSearchCriteria
{
    [StringLength(100)] public string? Text { get; set; }
    [StringLength(20)] public string? MemberNo { get; set; }
    [StringLength(32)] public string? Phone { get; set; }
    [StringLength(320)] public string? Email { get; set; }
    public MemberStatus? Status { get; set; }
    [StringLength(20)] public string? TierCode { get; set; }
    public long? SegmentId { get; set; }
    [StringLength(30)] public string? CustomerCode { get; set; }
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    [Range(1, 200)] public int PageSize { get; set; } = 20;

    /// <summary>blind index ของเบอร์ (service ใส่ให้ — ผู้เรียกตั้งเองไม่ได้)</summary>
    internal string? PhoneHash { get; set; }
    /// <summary>blind index ของอีเมล</summary>
    internal string? EmailHash { get; set; }
}

/// <summary>สมัครสมาชิก (เจ้าหน้าที่ และ Portal ใช้ field ชุดเดียวกัน)</summary>
public sealed class RegisterMemberRequest
{
    [Required, StringLength(100, MinimumLength = 1)] public string FirstName { get; set; } = string.Empty;
    [Required, StringLength(100, MinimumLength = 1)] public string LastName { get; set; } = string.Empty;
    [Required, StringLength(20, MinimumLength = 9)] public string Phone { get; set; } = string.Empty;
    [EmailAddress, StringLength(320)] public string? Email { get; set; }
    public DateOnly? BirthDate { get; set; }
    public Gender Gender { get; set; }
    [StringLength(100)] public string? Province { get; set; }
    /// <summary>ฉบับ consent ที่ยอมรับ (ต้องเป็นฉบับที่เผยแพร่อยู่)</summary>
    public long? ConsentVersionId { get; set; }
    public bool MarketingConsent { get; set; }
    /// <summary>ช่องทางที่ยินยอม (ว่าง + MarketingConsent = ทุกช่องทาง)</summary>
    public List<MarketingChannel>? MarketingChannels { get; set; }
    /// <summary>ผูก Customer ที่มีอยู่ (null = ให้ระบบสร้าง Customer บุคคลใหม่) — Portal ใช้ไม่ได้</summary>
    [StringLength(30)] public string? CustomerCode { get; set; }
    /// <summary>แคมเปญที่พาสมัคร (ไม่บังคับ)</summary>
    [StringLength(50)] public string? SourceCampaignCode { get; set; }
}

/// <summary>แก้ไขสมาชิก — null = ไม่เปลี่ยน</summary>
public sealed class UpdateMemberRequest
{
    [Required] public long? Version { get; set; }
    [StringLength(100)] public string? FirstName { get; set; }
    [StringLength(100)] public string? LastName { get; set; }
    [StringLength(20)] public string? Phone { get; set; }
    [EmailAddress, StringLength(320)] public string? Email { get; set; }
    public DateOnly? BirthDate { get; set; }
    public Gender? Gender { get; set; }
    [StringLength(100)] public string? Province { get; set; }
}

/// <summary>ผลการสมัคร: สมาชิก (PendingVerification) + OTP ที่ส่งไปยังเบอร์</summary>
public sealed class MemberRegistrationResultDTO
{
    public MemberDTO Member { get; set; } = new();
    public OtpChallengeDTO? Challenge { get; set; }
}

/// <summary>ผลการแก้ไข: เปลี่ยนเบอร์ต้องยืนยัน OTP ก่อน (Challenge ไม่ว่าง)</summary>
public sealed class UpdateMemberResultDTO
{
    public MemberDTO Member { get; set; } = new();
    public OtpChallengeDTO? Challenge { get; set; }
}

/// <summary>เปลี่ยนสถานะ</summary>
public sealed class ChangeMemberStatusRequest
{
    [Required] public long? Version { get; set; }
    public MemberStatus Status { get; set; }
    [Required, StringLength(500, MinimumLength = 3)] public string Reason { get; set; } = string.Empty;
}

/// <summary>ผูก / สร้าง Customer</summary>
public sealed class LinkCustomerRequest
{
    [Required] public long? Version { get; set; }
    /// <summary>null = ให้ระบบสร้าง Customer บุคคลใหม่ใน CustomerMgnt</summary>
    [StringLength(30)] public string? CustomerCode { get; set; }
}

/// <summary>ขอดูข้อมูลส่วนบุคคลเต็ม</summary>
public sealed class RevealPiiRequest
{
    [Required, MinLength(1)] public List<string> Fields { get; set; } = [];
    [Required, StringLength(500, MinimumLength = 3)] public string Reason { get; set; } = string.Empty;
}

/// <summary>ยืนยันสมาชิกแทน OTP</summary>
public sealed class VerifyOverrideRequest
{
    [Required, StringLength(500, MinimumLength = 3)] public string Reason { get; set; } = string.Empty;
    [StringLength(100)] public string? DocumentRef { get; set; }
}

/// <summary>ยืนยัน OTP</summary>
public sealed class VerifyOtpRequest
{
    [Required, StringLength(64)] public string ChallengeId { get; set; } = string.Empty;
    [Required, StringLength(8, MinimumLength = 4)] public string Code { get; set; } = string.Empty;
}

/// <summary>OTP ที่ส่งแล้ว (ไม่มีรหัส)</summary>
public sealed class OtpChallengeDTO
{
    public string ChallengeId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>แถว OTP (เก็บเฉพาะ hash ของรหัส)</summary>
public sealed class OtpChallengeEntity
{
    public string Id { get; set; } = string.Empty;
    /// <summary>member:{id} | registration:{id} | phone-change:{id}</summary>
    public string Purpose { get; set; } = string.Empty;
    public long? MemberId { get; set; }
    public string PhoneHash { get; set; } = string.Empty;
    /// <summary>เบอร์ใหม่แบบ envelope (กรณีเปลี่ยนเบอร์)</summary>
    public string? PayloadJson { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

/// <summary>การสมัครจาก Portal ที่รอ OTP (ยังไม่สร้างสมาชิกจนกว่าจะยืนยัน)</summary>
public sealed class PortalRegistrationDTO
{
    public string RegistrationId { get; set; } = string.Empty;
    public string ChallengeId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>ผลตรวจข้อมูลซ้ำ</summary>
public sealed class DuplicateCheckDTO
{
    public bool Exists { get; set; }
    public long? MemberId { get; set; }
    public string? MemberNoMasked { get; set; }
}

/// <summary>ขอตรวจข้อมูลซ้ำ</summary>
public sealed class DuplicateCheckRequest
{
    [StringLength(20)] public string? Phone { get; set; }
    [EmailAddress, StringLength(320)] public string? Email { get; set; }
}

/// <summary>Tier</summary>
public sealed class TierDTO
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>1 = ต่ำสุด</summary>
    public int Rank { get; set; }
    /// <summary>ยอดซื้อในรอบที่ทำให้ขึ้น Tier นี้</summary>
    public decimal UpgradeThreshold { get; set; }
    /// <summary>ยอดซื้อที่ต้องมีเพื่อคง Tier ตอนครบรอบ</summary>
    public decimal RetainThreshold { get; set; }
    public decimal PointMultiplier { get; set; } = 1m;
    public List<string> Benefits { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public long Version { get; set; }
}

/// <summary>สร้าง/แก้ Tier</summary>
public sealed class TierRequest
{
    [Required, RegularExpression("^[A-Z0-9_-]{1,20}$")] public string Code { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Range(1, 100)] public int Rank { get; set; }
    [Range(0, 1_000_000_000)] public decimal UpgradeThreshold { get; set; }
    [Range(0, 1_000_000_000)] public decimal RetainThreshold { get; set; }
    [Range(1, 10)] public decimal PointMultiplier { get; set; } = 1m;
    public List<string> Benefits { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public long? Version { get; set; }
}

/// <summary>ประวัติการเปลี่ยน Tier</summary>
public sealed class TierHistoryDTO
{
    public long Id { get; set; }
    public long MemberId { get; set; }
    public string? FromTier { get; set; }
    public string ToTier { get; set; } = string.Empty;
    /// <summary>Upgrade | Retain | Downgrade | Manual | Initial</summary>
    public string Reason { get; set; } = string.Empty;
    public decimal QualifyingSpend { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>ฉบับของ consent / นโยบายความเป็นส่วนตัว</summary>
public sealed class ConsentVersionDTO
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset EffectiveAt { get; set; }
    public bool IsPublished { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>สร้างฉบับ consent</summary>
public sealed class ConsentVersionRequest
{
    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    [Required, StringLength(100_000)] public string Body { get; set; } = string.Empty;
    public DateTimeOffset EffectiveAt { get; set; }
}

/// <summary>การให้ consent ของสมาชิก (append-only)</summary>
public sealed class MemberConsentDTO
{
    public long Id { get; set; }
    public long MemberId { get; set; }
    public long ConsentVersionId { get; set; }
    public bool Accepted { get; set; }
    public bool MarketingConsent { get; set; }
    public List<MarketingChannel> MarketingChannels { get; set; } = [];
    public string Channel { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

/// <summary>สมาชิกแก้ consent ของตัวเอง</summary>
public sealed class UpdateConsentRequest
{
    [Required] public long? ConsentVersionId { get; set; }
    public bool Accepted { get; set; }
    public bool MarketingConsent { get; set; }
    public List<MarketingChannel> MarketingChannels { get; set; } = [];
}

/// <summary>consent ของสมาชิก + ฉบับปัจจุบัน</summary>
public sealed class MyConsentsDTO
{
    public ConsentVersionDTO? Current { get; set; }
    public List<MemberConsentDTO> History { get; set; } = [];
}

/// <summary>คำขอตาม PDPA</summary>
public sealed class PrivacyRequestDTO
{
    public long Id { get; set; }
    public long MemberId { get; set; }
    public string MemberNo { get; set; } = string.Empty;
    public PrivacyRequestType Type { get; set; }
    public string? Detail { get; set; }
    public PrivacyRequestStatus Status { get; set; }
    public string? Outcome { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    /// <summary>ครบกำหนดตอบ (30 วันตาม PDPA)</summary>
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
}

/// <summary>สมาชิกยื่นคำขอ PDPA</summary>
public sealed class CreatePrivacyRequest
{
    public PrivacyRequestType Type { get; set; }
    [StringLength(2000)] public string? Detail { get; set; }
}

/// <summary>เจ้าหน้าที่ปิดคำขอ PDPA</summary>
public sealed class CompletePrivacyRequest
{
    public PrivacyRequestStatus Outcome { get; set; } = PrivacyRequestStatus.Completed;
    [Required, StringLength(2000, MinimumLength = 3)] public string Note { get; set; } = string.Empty;
}

/// <summary>ขอรวมบัญชี (secondary ถูกปิด ย้ายคะแนน/คูปอง/ประวัติไป primary)</summary>
public sealed class MergeRequest
{
    public long PrimaryId { get; set; }
    public long SecondaryId { get; set; }
    /// <summary>field ที่ให้ใช้ค่าของ secondary (firstName, lastName, email, birthDate, province)</summary>
    public List<string> TakeFromSecondary { get; set; } = [];
    [Required, StringLength(500, MinimumLength = 3)] public string Reason { get; set; } = string.Empty;
}

/// <summary>ตัวอย่างผลการรวมบัญชี</summary>
public sealed class MergePreviewDTO
{
    public MemberDTO Primary { get; set; } = new();
    public MemberDTO Secondary { get; set; } = new();
    public int PointsToMove { get; set; }
    public int CouponsToMove { get; set; }
    public int TransactionsToMove { get; set; }
    public List<string> Warnings { get; set; } = [];
}
