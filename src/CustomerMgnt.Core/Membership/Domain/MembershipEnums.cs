namespace CustomerMgnt.Core.Membership.Domain;

// enum ทั้งหมดส่งออกเป็นข้อความ (JsonStringEnumConverter) — client ต้อง ignore ค่าที่ไม่รู้จัก (Claude.MD §16)

/// <summary>สถานะสมาชิก (state machine ใน spec หัวข้อ 3.1)</summary>
public enum MemberStatus
{
    /// <summary>สมัครแล้ว รอยืนยัน OTP</summary>
    PendingVerification,
    /// <summary>ใช้งานได้</summary>
    Active,
    /// <summary>ระงับชั่วคราว</summary>
    Suspended,
    /// <summary>ไม่มีกิจกรรม</summary>
    Inactive,
    /// <summary>ปิดบัญชี (สถานะสุดท้าย)</summary>
    Closed,
}

/// <summary>ช่องทางที่สมัคร</summary>
public enum RegisteredChannel
{
    /// <summary>สมาชิกสมัครเองผ่าน Portal</summary>
    Portal,
    /// <summary>เจ้าหน้าที่สมัครแทน</summary>
    BackOffice,
    /// <summary>สมัครผ่าน POS</summary>
    Pos,
    /// <summary>นำเข้าข้อมูล</summary>
    Import,
}

/// <summary>เพศ</summary>
public enum Gender
{
    /// <summary>ไม่ระบุ</summary>
    Unspecified,
    /// <summary>ชาย</summary>
    Male,
    /// <summary>หญิง</summary>
    Female,
    /// <summary>อื่น ๆ</summary>
    Other,
}

/// <summary>ชนิดรายการคะแนน (ledger append-only)</summary>
public enum LedgerType
{
    /// <summary>ได้รับคะแนน</summary>
    Earn,
    /// <summary>ใช้คะแนน</summary>
    Redeem,
    /// <summary>หมดอายุ</summary>
    Expire,
    /// <summary>ย้อนคะแนนจากการคืน/ยกเลิก (ทั้งย้อนคะแนนที่ได้ และคืนคะแนนที่ใช้)</summary>
    Reverse,
    /// <summary>ปรับด้วยมือ</summary>
    Adjustment,
}

/// <summary>สถานะกฎการได้คะแนน</summary>
public enum EarnRuleStatus
{
    /// <summary>ร่าง</summary>
    Draft,
    /// <summary>รออนุมัติ</summary>
    PendingApproval,
    /// <summary>ใช้งาน</summary>
    Active,
    /// <summary>ปิด</summary>
    Inactive,
}

/// <summary>สถานะคำขอปรับคะแนน</summary>
public enum AdjustmentStatus
{
    /// <summary>มีผลแล้ว</summary>
    Applied,
    /// <summary>รออนุมัติ</summary>
    PendingApproval,
    /// <summary>ไม่อนุมัติ/ยกเลิก</summary>
    Rejected,
}

/// <summary>สถานะแคมเปญ (spec หัวข้อ 3.2)</summary>
public enum CampaignStatus
{
    /// <summary>ร่าง</summary>
    Draft,
    /// <summary>รออนุมัติ</summary>
    PendingApproval,
    /// <summary>อนุมัติแล้ว (ระบบเปลี่ยนเป็น Scheduled/Active ทันที)</summary>
    Approved,
    /// <summary>รอถึงวันเริ่ม</summary>
    Scheduled,
    /// <summary>ใช้งาน</summary>
    Active,
    /// <summary>หยุดชั่วคราว</summary>
    Paused,
    /// <summary>หมดเวลา/งบหมด</summary>
    Ended,
    /// <summary>ไม่อนุมัติ</summary>
    Rejected,
    /// <summary>ยกเลิก</summary>
    Cancelled,
}

/// <summary>ชนิดรางวัลของแคมเปญ</summary>
public enum RewardType
{
    /// <summary>ส่วนลด % (มีเพดาน)</summary>
    PercentDiscount,
    /// <summary>ส่วนลดเป็นบาท</summary>
    AmountDiscount,
    /// <summary>ซื้อ X แถม Y</summary>
    BuyXGetY,
    /// <summary>ซื้อครบยอดรับส่วนลด</summary>
    SpendThreshold,
    /// <summary>คะแนนพิเศษแบบบวกคงที่</summary>
    BonusPoints,
    /// <summary>ตัวคูณคะแนน</summary>
    PointMultiplier,
    /// <summary>ออกคูปองให้หลังซื้อ</summary>
    IssueCoupon,
}

/// <summary>ชั้นที่คำนวณโปรโมชัน</summary>
public enum PromotionLevel
{
    /// <summary>รายการสินค้า</summary>
    Line,
    /// <summary>ทั้งบิล</summary>
    Bill,
}

/// <summary>การใช้ซ้อน</summary>
public enum StackMode
{
    /// <summary>ใช้คนเดียวในชั้นนั้น</summary>
    Exclusive,
    /// <summary>ซ้อนได้</summary>
    Stackable,
    /// <summary>ใช้ได้อันเดียวต่อกลุ่ม (<c>exclusionGroup</c>)</summary>
    Group,
}

/// <summary>สถานะคูปอง (spec หัวข้อ 3.3)</summary>
public enum CouponStatus
{
    /// <summary>ใช้ได้</summary>
    Available,
    /// <summary>จองโดย reservation</summary>
    Reserved,
    /// <summary>ใช้แล้ว</summary>
    Redeemed,
    /// <summary>หมดอายุ</summary>
    Expired,
    /// <summary>ยกเลิก</summary>
    Cancelled,
    /// <summary>ย้อนจากการคืนทั้งบิล (ระบบเปลี่ยนเป็น Available ถ้ายังไม่หมดอายุ)</summary>
    Reversed,
}

/// <summary>สถานะ reservation ของ checkout</summary>
public enum ReservationStatus
{
    /// <summary>จองอยู่</summary>
    Reserved,
    /// <summary>ยืนยันแล้ว</summary>
    Committed,
    /// <summary>ปล่อยแล้ว (POS สั่ง หรือหมดเวลา)</summary>
    Released,
}

/// <summary>สถานะรายการขาย</summary>
public enum TransactionStatus
{
    /// <summary>บันทึกแล้ว</summary>
    Completed,
    /// <summary>คืนบางส่วน</summary>
    PartiallyReturned,
    /// <summary>คืนทั้งบิล</summary>
    Returned,
    /// <summary>ยกเลิก</summary>
    Cancelled,
}

/// <summary>ช่องทางขาย</summary>
public enum SalesChannel
{
    /// <summary>หน้าร้าน</summary>
    Pos,
    /// <summary>ออนไลน์</summary>
    Online,
    /// <summary>แอป</summary>
    App,
    /// <summary>อื่น ๆ</summary>
    Other,
}

/// <summary>ชนิดคำขออนุมัติ</summary>
public enum ApprovalType
{
    /// <summary>แคมเปญ</summary>
    Campaign,
    /// <summary>ปรับคะแนน</summary>
    Adjustment,
    /// <summary>รวมบัญชีสมาชิก</summary>
    Merge,
    /// <summary>กฎการได้คะแนน</summary>
    EarnRule,
}

/// <summary>สถานะคำขออนุมัติ</summary>
public enum ApprovalStatus
{
    /// <summary>รอพิจารณา</summary>
    Pending,
    /// <summary>อนุมัติครบทุกขั้น</summary>
    Approved,
    /// <summary>ไม่อนุมัติ</summary>
    Rejected,
    /// <summary>ผู้ยื่นยกเลิก</summary>
    Cancelled,
}

/// <summary>ผลการพิจารณา</summary>
public enum ApprovalDecision
{
    /// <summary>อนุมัติ</summary>
    Approve,
    /// <summary>ไม่อนุมัติ</summary>
    Reject,
}

/// <summary>ชนิดคำขอตาม PDPA</summary>
public enum PrivacyRequestType
{
    /// <summary>ขอสำเนาข้อมูล</summary>
    Access,
    /// <summary>ขอแก้ไข</summary>
    Rectify,
    /// <summary>ขอลบ</summary>
    Erase,
    /// <summary>ขอปิดบัญชี</summary>
    Close,
}

/// <summary>สถานะคำขอ PDPA</summary>
public enum PrivacyRequestStatus
{
    /// <summary>รอดำเนินการ</summary>
    Open,
    /// <summary>เสร็จ</summary>
    Completed,
    /// <summary>ปฏิเสธ</summary>
    Rejected,
}

/// <summary>ช่องทางการตลาดที่ยินยอม</summary>
public enum MarketingChannel
{
    /// <summary>SMS</summary>
    Sms,
    /// <summary>อีเมล</summary>
    Email,
    /// <summary>LINE</summary>
    Line,
}

/// <summary>สถานะงานเบื้องหลัง (export, ส่งข้อความ, ออกคูปอง)</summary>
public enum JobStatus
{
    /// <summary>รอคิว</summary>
    Queued,
    /// <summary>กำลังทำ</summary>
    Running,
    /// <summary>เสร็จ</summary>
    Done,
    /// <summary>ล้มเหลว</summary>
    Failed,
    /// <summary>หมดอายุ (ไฟล์ export)</summary>
    Expired,
}

/// <summary>ชนิดผู้กระทำใน audit</summary>
public enum ActorType
{
    /// <summary>เจ้าหน้าที่</summary>
    User,
    /// <summary>ระบบต้นทาง (client_credentials)</summary>
    Client,
    /// <summary>สมาชิกผ่าน Portal</summary>
    Member,
    /// <summary>งานเบื้องหลัง</summary>
    System,
}
