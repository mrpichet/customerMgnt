using System.ComponentModel.DataAnnotations;
using CustomerMgnt.Core.Domain;

namespace CustomerMgnt.Api.Controllers;

// Request contract ที่ใช้เฉพาะชั้น HTTP (แยกจาก DTO ภายใน เพื่อกัน over-posting) — DataAnnotations ตรวจอัตโนมัติโดย [ApiController]

/// <summary>แก้ไขห้องสนทนา</summary>
/// <param name="AssigneeSubject">ผู้รับผิดชอบ</param>
/// <param name="CustomerId">ผูกกับลูกค้า</param>
/// <param name="Status">สถานะ</param>
public sealed record ConversationUpdateRequest([StringLength(128)] string? AssigneeSubject, long? CustomerId, ConversationStatus? Status);

/// <summary>สิ้นสุดความสัมพันธ์</summary>
/// <param name="ValidTo">วันสุดท้าย</param>
public sealed record EndRelationshipRequest([Required] DateOnly ValidTo);

/// <summary>ผูกลูกค้ากับรายการในระบบอื่น</summary>
/// <param name="System">ระบบ</param>
/// <param name="EntityType">project / issue / meeting / requirement-project</param>
/// <param name="ExternalId">รหัสในระบบนั้น</param>
/// <param name="Title">ชื่อ</param>
public sealed record ExternalLinkRequest(ExternalSystem System, [Required, StringLength(40)] string EntityType, [Required, StringLength(64)] string ExternalId, [StringLength(300)] string? Title);

/// <summary>แก้โปรไฟล์ของตัวเอง</summary>
/// <param name="DisplayName">ชื่อแสดง</param>
/// <param name="MessageHubUserId">user id ใน MessageHub</param>
public sealed record ProfileRequest([Required, StringLength(200)] string DisplayName, Guid? MessageHubUserId);

/// <summary>เข้าสู่ระบบ (ส่งต่อไป AuthServer)</summary>
/// <param name="Login">ชื่อผู้ใช้</param>
/// <param name="Password">รหัสผ่าน</param>
/// <param name="OtpCode">รหัส OTP 6 หลัก (เมื่อได้ 401 code = mfa_required)</param>
public sealed record LoginRequest([Required, StringLength(256)] string Login, [Required, StringLength(512)] string Password, [StringLength(8)] string? OtpCode = null);

/// <summary>วิเคราะห์อารมณ์</summary>
/// <param name="Text">ข้อความ</param>
public sealed record SentimentRequest([Required, StringLength(8000, MinimumLength = 1)] string Text);

/// <summary>นำเข้าเหตุการณ์เป็นชุด</summary>
/// <param name="Events">เหตุการณ์</param>
public sealed record IngestEventsRequest([Required, MinLength(1), MaxLength(10_000)] List<EngagementEventRequest> Events);

/// <summary>รายการแคตตาล็อก</summary>
/// <param name="Name">ชื่อ</param>
/// <param name="Kind">ประเภท</param>
/// <param name="Category">หมวด</param>
/// <param name="Price">ราคา</param>
/// <param name="IsActive">ยังขายอยู่</param>
public sealed record CatalogItemRequest([Required, StringLength(200)] string Name, CatalogItemKind Kind, [StringLength(100)] string? Category, [Range(0, 1_000_000_000)] decimal Price, bool IsActive = true);
