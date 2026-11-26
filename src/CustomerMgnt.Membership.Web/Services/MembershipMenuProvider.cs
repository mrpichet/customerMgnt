using WebCommon.UI.Navigation;
using A = CustomerMgnt.Core.MemberAnalytics.Services.AnaPermissions;
using P = CustomerMgnt.Core.Membership.Services.MembershipPermissions;

namespace CustomerMgnt.Membership.Web.Services;

/// <summary>เมนู Back Office — แต่ละรายการซ่อนตามสิทธิ์จาก AuthServer (server ตรวจซ้ำทุกครั้ง)</summary>
public sealed class MembershipMenuProvider : IAppMenuProvider
{
    /// <inheritdoc />
    public IReadOnlyList<NavGroup> GetMenu() =>
    [
        new("ภาพรวม",
        [
            new NavItem { Title = "Dashboard", Href = "", Icon = "home", MatchExact = true },
            new NavItem { Title = "งานรออนุมัติ", Href = "approvals", Icon = "check-circle" },
        ]),
        new("สมาชิก",
        [
            new NavItem { Title = "รายการสมาชิก", Href = "members", Icon = "users", Permission = P.MemberRead },
            new NavItem { Title = "รวมบัญชีซ้ำ", Href = "members/merge", Icon = "users", Permission = P.MemberMerge },
            new NavItem { Title = "Tier", Href = "tiers", Icon = "check-square", Permission = P.MemberRead },
            new NavItem { Title = "คำขอ PDPA", Href = "privacy-requests", Icon = "shield", Permission = P.PrivacyManage },
        ]),
        new("คะแนน",
        [
            new NavItem { Title = "กฎการได้คะแนน", Href = "points/rules", Icon = "filter", Permission = P.PointsRead },
            new NavItem { Title = "Point ledger", Href = "points/ledger", Icon = "file-spreadsheet", Permission = P.PointsRead },
            new NavItem { Title = "ปรับคะแนน", Href = "points/adjustments", Icon = "edit", Permission = P.PointsRead },
        ]),
        new("โปรโมชัน",
        [
            new NavItem { Title = "แคมเปญ", Href = "promotions", Icon = "file", Permission = P.PromotionRead },
            new NavItem { Title = "ทดลองคำนวณ", Href = "promotions/simulate", Icon = "bar-chart", Permission = P.PromotionRead },
            new NavItem { Title = "ชุดคูปอง", Href = "coupons", Icon = "inbox", Permission = P.CouponRead, MatchExact = true },
            new NavItem { Title = "ค้นหาคูปอง", Href = "coupons/codes", Icon = "search", Permission = P.CouponRead },
            new NavItem { Title = "Segment", Href = "segments", Icon = "filter", Permission = P.SegmentRead },
            new NavItem { Title = "ส่งข้อความ", Href = "messages", Icon = "send", Permission = P.CampaignMessageSend },
        ]),
        new("ธุรกรรมและรายงาน",
        [
            new NavItem { Title = "ประวัติการซื้อ", Href = "transactions", Icon = "file-spreadsheet", Permission = P.TxnRead },
            new NavItem { Title = "รายงานสมาชิก", Href = "reports/members", Icon = "bar-chart", Permission = P.ReportRead },
            new NavItem { Title = "รายงานคะแนน", Href = "reports/points", Icon = "bar-chart", Permission = P.ReportRead },
            new NavItem { Title = "รายงานโปรโมชัน", Href = "reports/promotions", Icon = "bar-chart", Permission = P.ReportRead },
            new NavItem { Title = "รายงานรายการขาย", Href = "reports/transactions", Icon = "bar-chart", Permission = P.ReportRead },
            new NavItem { Title = "งานส่งออก", Href = "reports/exports", Icon = "download", Permission = P.ReportExport },
        ]),
        new("วิเคราะห์ข้อมูล",
        [
            new NavItem { Title = "ภาพรวมผู้บริหาร", Href = "analytics", Icon = "bar-chart", Permission = A.DashboardRead, MatchExact = true },
            new NavItem { Title = "ภาพรวมสมาชิก", Href = "analytics/members", Icon = "users", Permission = A.MemberRead, MatchExact = true },
            new NavItem { Title = "วิเคราะห์สมาชิก", Href = "analytics/members/activity", Icon = "users", Permission = A.MemberRead },
            new NavItem { Title = "ภาพรวมโปรโมชัน", Href = "analytics/promotions", Icon = "bar-chart", Permission = A.PromotionRead },
            new NavItem { Title = "ผลแคมเปญ", Href = "analytics/campaigns", Icon = "file", Permission = A.PromotionRead },
            new NavItem { Title = "Funnel", Href = "analytics/funnels", Icon = "filter", Permission = A.PromotionRead },
            new NavItem { Title = "คะแนน", Href = "analytics/points", Icon = "bar-chart", Permission = A.DashboardRead },
            new NavItem { Title = "คูปอง", Href = "analytics/coupons", Icon = "inbox", Permission = A.DashboardRead },
            new NavItem { Title = "รายงาน", Href = "analytics/reports", Icon = "file-spreadsheet", Permission = A.DashboardRead },
            new NavItem { Title = "ตั้งเวลาส่งรายงาน", Href = "analytics/schedules", Icon = "clock", Permission = A.ReportBuild },
            new NavItem { Title = "ไฟล์ส่งออก", Href = "analytics/exports", Icon = "download", Permission = A.Export },
            new NavItem { Title = "ตัวกรองที่บันทึก", Href = "analytics/saved-filters", Icon = "filter", Permission = A.DashboardRead },
            new NavItem { Title = "แจ้งเตือน", Href = "analytics/alerts", Icon = "alert-triangle", Permission = A.AlertManage },
            new NavItem { Title = "สถานะข้อมูล", Href = "analytics/data-status", Icon = "clock", Permission = A.DashboardRead },
            new NavItem { Title = "ขอบเขตข้อมูล", Href = "analytics/data-scopes", Icon = "shield", Permission = A.SettingsManage },
            new NavItem { Title = "ตั้งค่าการวิเคราะห์", Href = "analytics/settings", Icon = "layout", Permission = A.SettingsManage },
        ]),
        new("ตั้งค่า",
        [
            new NavItem { Title = "กฎธุรกิจ", Href = "settings/rules", Icon = "layout", Permission = P.SettingsManage },
            new NavItem { Title = "ฉบับความยินยอม", Href = "settings/consents", Icon = "file-text", Permission = P.SettingsManage },
            new NavItem { Title = "ระบบภายนอก", Href = "settings/integrations", Icon = "user", Permission = P.SettingsManage },
            new NavItem { Title = "ผู้ใช้และสิทธิ์", Href = "settings/users", Icon = "key", Permission = P.SettingsManage },
            new NavItem { Title = "Audit log", Href = "audit", Icon = "clock", Permission = P.AuditRead },
        ]),
    ];
}
