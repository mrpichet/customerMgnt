using System.ComponentModel.DataAnnotations;

namespace CustomerMgnt.Core.Membership.Domain;

/// <summary>
/// กฎธุรกิจที่ System Admin ตั้งได้ (หน้า A37) — ค่าเริ่มต้นตาม spec สูตรคำนวณ หัวข้อ 1 (ยืนยันแล้ว 2026-11-26)
/// เก็บเป็นแถวเดียวพร้อม <see cref="Version"/> (optimistic lock)
/// </summary>
public sealed class MembershipRulesDTO
{
    // ---- 1. เบอร์/อีเมลซ้ำ ----
    /// <summary>เบอร์โทรใช้ได้กับสมาชิกที่ยังไม่ Closed คนเดียว</summary>
    public bool UniquePhone { get; set; } = true;
    /// <summary>อีเมลใช้ได้กับสมาชิกที่ยังไม่ Closed คนเดียว</summary>
    public bool UniqueEmail { get; set; } = true;

    // ---- 2. การยืนยันสมาชิก ----
    /// <summary>อายุ OTP (นาที)</summary>
    [Range(1, 30)] public int OtpTtlMinutes { get; set; } = 5;
    /// <summary>ใส่ OTP ผิดได้กี่ครั้ง</summary>
    [Range(1, 10)] public int OtpMaxAttempts { get; set; } = 5;
    /// <summary>ขอ OTP ได้กี่ครั้งต่อเบอร์ใน 10 นาที</summary>
    [Range(1, 20)] public int OtpPerPhonePer10Minutes { get; set; } = 3;

    // ---- 3. ฐานคำนวณคะแนน (NetAfterDiscount: ยอดหลังส่วนลด ไม่รวมส่วนที่จ่ายด้วยคะแนน) ----
    /// <summary>รหัสสินค้าที่ไม่ได้คะแนน (non-earning) เช่น บัตรของขวัญ</summary>
    public List<string> NonEarningItemCodes { get; set; } = ["GIFTCARD"];

    // ---- 4. คะแนนหมดอายุ ----
    /// <summary>lot หมดอายุสิ้นเดือนของ (เดือนที่ได้รับ + ค่านี้)</summary>
    [Range(1, 120)] public int ExpiryMonths { get; set; } = 24;
    /// <summary>แจ้งเตือนก่อนหมดอายุ (วัน)</summary>
    [Range(0, 365)] public int ExpiryNotifyDays { get; set; } = 30;

    // ---- 5. คืนสินค้า ----
    /// <summary>ย้อนคะแนนแล้วติดลบได้ (เป็น debt หักจากคะแนนครั้งต่อไป)</summary>
    public bool ReturnAllowNegativeBalance { get; set; } = true;
    /// <summary>คืนทั้งบิลแล้วคูปองที่ยังไม่หมดอายุกลับเป็น Available</summary>
    public bool ReturnRestoreCouponOnFullReturn { get; set; } = true;

    // ---- 6. การใช้ซ้อน ----
    /// <summary>คูปองต่อบิล</summary>
    [Range(0, 10)] public int MaxCouponsPerBill { get; set; } = 1;
    /// <summary>ใช้คูปองคู่กับการจ่ายด้วยคะแนนได้</summary>
    public bool CouponWithPoints { get; set; } = true;

    // ---- 7. จำกัดสิทธิ ----
    /// <summary>ค่าเริ่มต้นของแคมเปญใหม่: ครั้งต่อสมาชิกต่อวัน</summary>
    [Range(0, 1000)] public int DefaultPerMemberPerDay { get; set; } = 1;

    // ---- 8. POS ----
    /// <summary>ส่งรายการขายย้อนหลังได้ภายในกี่วัน</summary>
    [Range(0, 90)] public int PosLateSubmitDays { get; set; } = 7;
    /// <summary>อายุ reservation (นาที)</summary>
    [Range(1, 120)] public int ReserveTtlMinutes { get; set; } = 15;

    // ---- 9. อนุมัติแคมเปญ ----
    /// <summary>จำนวนขั้นอนุมัติปกติ</summary>
    [Range(1, 2)] public int ApprovalLevels { get; set; } = 1;
    /// <summary>งบเกินค่านี้ต้องอนุมัติ 2 ขั้น (บาท)</summary>
    [Range(0, 1_000_000_000)] public decimal SecondLevelBudget { get; set; } = 1_000_000m;

    // ---- 10. ปรับคะแนน ----
    /// <summary>ปรับเกินกี่คะแนนต่อครั้งต้องอนุมัติ</summary>
    [Range(0, 1_000_000)] public int AdjustMaxPerRequest { get; set; } = 1000;
    /// <summary>ปรับรวมต่อวันต่อเจ้าหน้าที่เกินเท่าไรต้องอนุมัติ</summary>
    [Range(0, 10_000_000)] public int AdjustMaxPerUserPerDay { get; set; } = 5000;

    // ---- การแลกคะแนน (spec 2.5) ----
    /// <summary>มูลค่า 1 คะแนน (บาท)</summary>
    [Range(0.01, 100)] public decimal PointValue { get; set; } = 0.25m;
    /// <summary>จ่ายด้วยคะแนนได้สูงสุดกี่ % ของยอดบิล</summary>
    [Range(0, 100)] public int MaxPointsPayPercent { get; set; } = 50;

    // ---- Tier ----
    /// <summary>ช่วงนับยอดซื้อสำหรับ Tier (เดือน)</summary>
    [Range(1, 36)] public int TierWindowMonths { get; set; } = 12;

    /// <summary>เขตเวลาธุรกิจ (สิ้นเดือน, วันหมดอายุ, จำกัดสิทธิรายวัน)</summary>
    [StringLength(64)] public string TimeZoneId { get; set; } = "Asia/Bangkok";

    /// <summary>Optimistic concurrency token</summary>
    public long Version { get; set; }

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public MembershipRulesDTO Clone()
    {
        var c = (MembershipRulesDTO)MemberwiseClone();
        c.NonEarningItemCodes = [.. NonEarningItemCodes];
        return c;
    }
}
