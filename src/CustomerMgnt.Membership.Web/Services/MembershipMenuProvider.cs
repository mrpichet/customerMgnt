using WebCommon.UI.Navigation;
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
