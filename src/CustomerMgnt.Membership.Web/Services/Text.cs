using System.Globalization;
using CustomerMgnt.Core.Membership.Domain;
using WebCommon.UI.Models;

namespace CustomerMgnt.Membership.Web.Services;

/// <summary>ข้อความภาษาไทยของ enum และรูปแบบตัวเลข/วันที่</summary>
public static class Text
{
    private static readonly CultureInfo Th = CultureInfo.GetCultureInfo("th-TH");

    public static string Member(MemberStatus s) => s switch
    {
        MemberStatus.PendingVerification => "รอยืนยัน",
        MemberStatus.Active => "ใช้งาน",
        MemberStatus.Suspended => "ระงับชั่วคราว",
        MemberStatus.Inactive => "ไม่เคลื่อนไหว",
        MemberStatus.Closed => "ปิดบัญชี",
        _ => s.ToString(),
    };

    public static AlertLevel Level(MemberStatus s) => s switch
    {
        MemberStatus.Active => AlertLevel.Success,
        MemberStatus.PendingVerification => AlertLevel.Info,
        MemberStatus.Suspended => AlertLevel.Warning,
        _ => AlertLevel.Danger,
    };

    public static string Campaign(CampaignStatus s) => s switch
    {
        CampaignStatus.Draft => "ร่าง",
        CampaignStatus.PendingApproval => "รออนุมัติ",
        CampaignStatus.Approved => "อนุมัติแล้ว",
        CampaignStatus.Scheduled => "รอเริ่ม",
        CampaignStatus.Active => "กำลังใช้งาน",
        CampaignStatus.Paused => "หยุดชั่วคราว",
        CampaignStatus.Ended => "สิ้นสุด",
        CampaignStatus.Rejected => "ไม่อนุมัติ",
        CampaignStatus.Cancelled => "ยกเลิก",
        _ => s.ToString(),
    };

    public static AlertLevel Level(CampaignStatus s) => s switch
    {
        CampaignStatus.Active => AlertLevel.Success,
        CampaignStatus.PendingApproval or CampaignStatus.Scheduled or CampaignStatus.Approved => AlertLevel.Info,
        CampaignStatus.Paused or CampaignStatus.Draft => AlertLevel.Warning,
        _ => AlertLevel.Danger,
    };

    public static string Reward(RewardType r) => r switch
    {
        RewardType.PercentDiscount => "ส่วนลด %",
        RewardType.AmountDiscount => "ส่วนลดบาท",
        RewardType.BuyXGetY => "ซื้อ X แถม Y",
        RewardType.SpendThreshold => "ซื้อครบลด",
        RewardType.BonusPoints => "คะแนนพิเศษ",
        RewardType.PointMultiplier => "คะแนนคูณ",
        RewardType.IssueCoupon => "แจกคูปอง",
        _ => r.ToString(),
    };

    public static string Coupon(CouponStatus s) => s switch
    {
        CouponStatus.Available => "ใช้ได้",
        CouponStatus.Reserved => "ถูกจอง",
        CouponStatus.Redeemed => "ใช้แล้ว",
        CouponStatus.Expired => "หมดอายุ",
        CouponStatus.Cancelled => "ยกเลิก",
        CouponStatus.Reversed => "คืนสิทธิ์",
        _ => s.ToString(),
    };

    public static AlertLevel Level(CouponStatus s) => s switch
    {
        CouponStatus.Available or CouponStatus.Reversed => AlertLevel.Success,
        CouponStatus.Reserved => AlertLevel.Info,
        CouponStatus.Redeemed => AlertLevel.Warning,
        _ => AlertLevel.Danger,
    };

    public static string Ledger(LedgerType t) => t switch
    {
        LedgerType.Earn => "ได้รับ",
        LedgerType.Redeem => "แลกใช้",
        LedgerType.Expire => "หมดอายุ",
        LedgerType.Reverse => "คืน/กลับรายการ",
        LedgerType.Adjustment => "ปรับปรุง",
        _ => t.ToString(),
    };

    public static string Txn(TransactionStatus s) => s switch
    {
        TransactionStatus.Completed => "สำเร็จ",
        TransactionStatus.PartiallyReturned => "คืนบางส่วน",
        TransactionStatus.Returned => "คืนทั้งหมด",
        TransactionStatus.Cancelled => "ยกเลิก",
        _ => s.ToString(),
    };

    public static string Approval(CustomerMgnt.Core.Membership.Domain.ApprovalStatus s) => s switch
    {
        CustomerMgnt.Core.Membership.Domain.ApprovalStatus.Pending => "รออนุมัติ",
        CustomerMgnt.Core.Membership.Domain.ApprovalStatus.Approved => "อนุมัติแล้ว",
        CustomerMgnt.Core.Membership.Domain.ApprovalStatus.Rejected => "ไม่อนุมัติ",
        CustomerMgnt.Core.Membership.Domain.ApprovalStatus.Cancelled => "ยกเลิก",
        _ => s.ToString(),
    };

    public static AlertLevel Level(CustomerMgnt.Core.Membership.Domain.ApprovalStatus s) => s switch
    {
        CustomerMgnt.Core.Membership.Domain.ApprovalStatus.Pending => AlertLevel.Info,
        CustomerMgnt.Core.Membership.Domain.ApprovalStatus.Approved => AlertLevel.Success,
        _ => AlertLevel.Danger,
    };

    public static string ApprovalType(ApprovalType t) => t switch
    {
        CustomerMgnt.Core.Membership.Domain.ApprovalType.Campaign => "แคมเปญ",
        CustomerMgnt.Core.Membership.Domain.ApprovalType.Adjustment => "ปรับคะแนน",
        CustomerMgnt.Core.Membership.Domain.ApprovalType.Merge => "รวมบัญชี",
        CustomerMgnt.Core.Membership.Domain.ApprovalType.EarnRule => "กฎการได้คะแนน",
        _ => t.ToString(),
    };

    public static string Adjustment(AdjustmentStatus s) => s switch
    {
        AdjustmentStatus.Applied => "มีผลแล้ว",
        AdjustmentStatus.PendingApproval => "รออนุมัติ",
        AdjustmentStatus.Rejected => "ไม่อนุมัติ",
        _ => s.ToString(),
    };

    public static string Rule(EarnRuleStatus s) => s switch
    {
        EarnRuleStatus.Draft => "ร่าง",
        EarnRuleStatus.PendingApproval => "รออนุมัติ",
        EarnRuleStatus.Active => "ใช้งาน",
        EarnRuleStatus.Inactive => "ไม่ใช้งาน",
        _ => s.ToString(),
    };

    public static string Job(JobStatus s) => s switch
    {
        JobStatus.Queued => "รอคิว",
        JobStatus.Running => "กำลังทำ",
        JobStatus.Done => "เสร็จ",
        JobStatus.Failed => "ล้มเหลว",
        JobStatus.Expired => "หมดอายุ",
        _ => s.ToString(),
    };

    public static AlertLevel Level(JobStatus s) => s switch
    {
        JobStatus.Done => AlertLevel.Success,
        JobStatus.Queued or JobStatus.Running => AlertLevel.Info,
        JobStatus.Expired => AlertLevel.Warning,
        _ => AlertLevel.Danger,
    };

    public static string Privacy(PrivacyRequestType t) => t switch
    {
        PrivacyRequestType.Access => "ขอสำเนาข้อมูล",
        PrivacyRequestType.Rectify => "ขอแก้ไขข้อมูล",
        PrivacyRequestType.Erase => "ขอลบ/ทำให้ไม่ระบุตัวตน",
        PrivacyRequestType.Close => "ขอปิดบัญชี",
        _ => t.ToString(),
    };

    public static string Privacy(PrivacyRequestStatus s) => s switch
    {
        PrivacyRequestStatus.Open => "รอดำเนินการ",
        PrivacyRequestStatus.Completed => "ดำเนินการแล้ว",
        PrivacyRequestStatus.Rejected => "ปฏิเสธ",
        _ => s.ToString(),
    };

    public static string Gender(Gender g) => g switch
    {
        CustomerMgnt.Core.Membership.Domain.Gender.Male => "ชาย",
        CustomerMgnt.Core.Membership.Domain.Gender.Female => "หญิง",
        CustomerMgnt.Core.Membership.Domain.Gender.Other => "อื่น ๆ",
        _ => "ไม่ระบุ",
    };

    public static string Channel(MarketingChannel c) => c switch
    {
        MarketingChannel.Sms => "SMS",
        MarketingChannel.Email => "อีเมล",
        MarketingChannel.Line => "LINE",
        _ => c.ToString(),
    };

    public static string Date(DateTimeOffset? d) => d is null ? "-" : d.Value.ToLocalTime().ToString("dd/MM/yyyy", Th);

    public static string DateTime(DateTimeOffset? d) => d is null ? "-" : d.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Th);

    public static string Num(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);

    public static string Int(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>แปลงข้อผิดพลาดเป็นข้อความที่แสดงผู้ใช้ได้ (ไม่แสดงรายละเอียดภายใน)</summary>
    public static string Error(Exception ex) => ex switch
    {
        ApiException a => a.Code switch
        {
            "validation_failed" or "validation_error" => a.Errors is { Count: > 0 } e ? string.Join(" ", e.SelectMany(x => x.Value)) : Fallback(a, "ข้อมูลไม่ถูกต้อง"),
            "version_conflict" => "มีผู้อื่นแก้ไขข้อมูลนี้ก่อนแล้ว กรุณาโหลดใหม่แล้วลองอีกครั้ง",
            "invalid_transition" => "เปลี่ยนสถานะจากสถานะปัจจุบันไม่ได้",
            "duplicate_member" => "เบอร์โทรหรืออีเมลซ้ำกับสมาชิกที่มีอยู่",
            "insufficient_points" => "คะแนนไม่พอ",
            "limit_reached" => "เกินจำนวนสิทธิ์ที่กำหนด",
            "budget_exhausted" => "งบประมาณของแคมเปญหมดแล้ว",
            "self_approval" => "อนุมัติคำขอของตัวเองไม่ได้",
            "access_denied" or "forbidden" => "คุณไม่มีสิทธิ์ทำรายการนี้",
            "not_found" => "ไม่พบข้อมูล",
            "not_registered" => "บัญชีนี้ยังไม่ได้สมัครสมาชิก",
            "otp_invalid" => "รหัส OTP ไม่ถูกต้อง",
            "otp_expired" => "รหัส OTP หมดอายุ กรุณาขอรหัสใหม่",
            "otp_locked" => "กรอก OTP ผิดเกินจำนวนครั้ง กรุณาขอรหัสใหม่",
            "rate_limited" => "ทำรายการถี่เกินไป กรุณารอสักครู่",
            "export_expired" => "ไฟล์หมดอายุแล้ว กรุณาสั่งส่งออกใหม่",
            "unauthorized" => "หมดเวลาเข้าสู่ระบบ กรุณาเข้าสู่ระบบใหม่",
            _ => Fallback(a, "ทำรายการไม่สำเร็จ"),
        },
        HttpRequestException => "ติดต่อเซิร์ฟเวอร์ไม่ได้ กรุณาตรวจสอบเครือข่ายแล้วลองใหม่",
        _ => "เกิดข้อผิดพลาด กรุณาลองใหม่อีกครั้ง",
    };

    private static string Fallback(ApiException a, string text) => a.Status < 500 && !string.IsNullOrWhiteSpace(a.Message) ? $"{text}: {a.Message}" : text;

    /// <summary>แยกรายการด้วย , หรือขึ้นบรรทัด</summary>
    public static List<string> SplitList(string? s) =>
        string.IsNullOrWhiteSpace(s) ? [] : [.. s.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];

    public static string JoinList(IEnumerable<string>? items) => items is null ? string.Empty : string.Join(", ", items);
}
