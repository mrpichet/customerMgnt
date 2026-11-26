using System.Globalization;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Membership.Web.Services;

/// <summary>ข้อความและรูปแบบตัวเลขของหน้าจอวิเคราะห์ข้อมูล (AN-01..AN-28)</summary>
public static class AnaText
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["membersTotal"] = "สมาชิกทั้งหมด", ["membersNew"] = "สมาชิกใหม่", ["membersActive"] = "สมาชิกที่ใช้งาน", ["memberSales"] = "ยอดขายสมาชิก",
        ["memberSalesShare"] = "สัดส่วนยอดขายสมาชิก", ["avgTicket"] = "ยอดเฉลี่ยต่อบิล", ["purchaseFrequency"] = "ความถี่การซื้อ", ["repeatRate"] = "อัตราซื้อซ้ำ",
        ["pointsEarned"] = "คะแนนที่ได้รับ", ["pointsRedeemed"] = "คะแนนที่ใช้", ["couponsRedeemed"] = "คูปองที่ใช้", ["activeCampaigns"] = "แคมเปญที่ทำงาน",
        ["promotionDiscount"] = "ส่วนลดจากโปรโมชัน", ["outstanding"] = "คะแนนคงเหลือ", ["liabilityBaht"] = "ภาระคะแนน (บาท)", ["redemptionRate"] = "อัตราการใช้คะแนน",
        ["couponsIssued"] = "คูปองที่ออก", ["usageRate"] = "อัตราการใช้คูปอง", ["salesWithCoupon"] = "ยอดขายที่ใช้คูปอง", ["avgDaysToRedeem"] = "วันเฉลี่ยก่อนใช้",
        ["participants"] = "ผู้เข้าร่วม", ["eligibleMembers"] = "สมาชิกเป้าหมาย", ["participationRate"] = "อัตราการเข้าร่วม", ["transactions"] = "จำนวนบิล",
        ["campaignSales"] = "ยอดขายแคมเปญ", ["sales"] = "ยอดขายแคมเปญ", ["discount"] = "ส่วนลด", ["pointsGiven"] = "คะแนนที่แจก", ["newMembers"] = "สมาชิกใหม่จากแคมเปญ",
        ["messaged"] = "ส่งข้อความ", ["budgetUsedPct"] = "ใช้งบประมาณ", ["earned"] = "คะแนนที่ได้รับ", ["redeemed"] = "คะแนนที่ใช้", ["expired"] = "คะแนนหมดอายุ",
        ["adjusted"] = "คะแนนปรับปรุง", ["expiringSoon"] = "ใกล้หมดอายุ",
    };

    /// <summary>ชื่อ metric / KPI (ไม่รู้จัก = รหัสเดิม)</summary>
    public static string Label(string key)
    {
        string k = key.Contains('.', StringComparison.Ordinal) ? key[(key.LastIndexOf('.') + 1)..] : key;
        return Labels.TryGetValue(key, out string? v) || Labels.TryGetValue(k, out v) ? v : key;
    }

    /// <summary>เหตุผลที่ข้อมูลไม่พอ</summary>
    public static string Reason(string? code) => code switch
    {
        AnaFormula.ZeroDenominator => "ตัวหารเป็นศูนย์",
        AnaFormula.GroupTooSmall => "กลุ่มเล็กเกินไป",
        AnaFormula.MissingSource => "ยังไม่มีข้อมูลต้นทาง",
        AnaFormula.CostCoverageLow => "ข้อมูลต้นทุนไม่ครบ",
        AnaFormula.PeriodIncomplete => "ช่วงเวลายังไม่ครบ",
        AnaFormula.NoControlGroup => "ไม่มีกลุ่มควบคุม",
        AnaFormula.ScopeEmpty => "ไม่มีข้อมูลในขอบเขตของคุณ",
        "permission_required" => "ไม่มีสิทธิ์ดู",
        null or "" => string.Empty,
        _ => code,
    };

    /// <summary>ค่าตามหน่วย</summary>
    public static string Value(decimal? v, MetricUnit unit) => v is not decimal d ? "-" : unit switch
    {
        MetricUnit.Baht => d.ToString("N2", CultureInfo.InvariantCulture),
        MetricUnit.Percent => d.ToString("N1", CultureInfo.InvariantCulture) + "%",
        MetricUnit.Ratio => d.ToString("N2", CultureInfo.InvariantCulture),
        MetricUnit.Days => d.ToString("N1", CultureInfo.InvariantCulture) + " วัน",
        _ => d.ToString("N0", CultureInfo.InvariantCulture),
    };

    public static string Num(decimal? v) => v is decimal d ? d.ToString("N2", CultureInfo.InvariantCulture) : "-";

    public static string Int(long? v) => v is long d ? d.ToString("N0", CultureInfo.InvariantCulture) : "-";

    public static string Pct(decimal? v) => v is decimal d ? d.ToString("N1", CultureInfo.InvariantCulture) + "%" : "-";

    public static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>ค่าในตาราง drill-down / รายงานตามชนิดคอลัมน์</summary>
    public static string Cell(object? v, string type) => v switch
    {
        null => "-",
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } n when type is "Baht" or "Decimal" => n.GetDecimal().ToString("N2", CultureInfo.InvariantCulture),
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } n when type is "Percent" => n.GetDecimal().ToString("N1", CultureInfo.InvariantCulture) + "%",
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } n => n.GetDecimal().ToString("#,0.##", CultureInfo.InvariantCulture),
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } s when type is "DateTime" or "Date" && DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            => type == "Date" ? dt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : dt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Null } => "-",
        System.Text.Json.JsonElement e => e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() ?? "-" : e.ToString(),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "-",
    };

    public static string Compare(CompareMode m) => m switch
    {
        CompareMode.None => "ไม่เปรียบเทียบ",
        CompareMode.PreviousPeriod => "ช่วงก่อนหน้า",
        _ => "ช่วงเดียวกันปีก่อน",
    };

    public static string Channel(SalesChannel c) => c switch
    {
        SalesChannel.Pos => "หน้าร้าน",
        SalesChannel.Online => "ออนไลน์",
        _ => c.ToString(),
    };

    public static string Attribution(Attribution a) => a switch
    {
        Core.MemberAnalytics.Domain.Attribution.InPeriod => "ซื้อในช่วงแคมเปญ",
        Core.MemberAnalytics.Domain.Attribution.BenefitUsed => "ใช้สิทธิ์แคมเปญ",
        _ => "ได้รับข้อความแล้วซื้อ",
    };

    public static string Step(FunnelStep s) => s switch
    {
        FunnelStep.MessageReceived => "ได้รับข้อความ",
        FunnelStep.MessageOpened => "เปิดอ่าน",
        FunnelStep.MessageClicked => "คลิก",
        FunnelStep.PortalView => "ดูโปรโมชันใน Portal",
        FunnelStep.CouponReceived => "ได้รับคูปอง",
        FunnelStep.Registered => "สมัครสมาชิก",
        FunnelStep.Purchased => "ซื้อ",
        _ => "ซื้อซ้ำ",
    };

    public static string Alert(AlertType t) => t switch
    {
        AlertType.CouponUsageLow => "คูปองถูกใช้น้อยผิดปกติ",
        AlertType.CouponUsageHigh => "คูปองถูกใช้มากผิดปกติ",
        AlertType.CampaignBudgetNear => "งบแคมเปญใกล้หมด",
        AlertType.PointsAdjustmentAbnormal => "ปรับคะแนนผิดปกติ",
        AlertType.NoPosData => "ไม่มีข้อมูลจาก POS",
        AlertType.NewMembersDrop => "สมาชิกใหม่ลดลง",
        _ => "การใช้สิทธิ์พุ่งสูง",
    };

    public static string State(AlertState s) => s switch
    {
        AlertState.Fired => "แจ้งเตือน",
        AlertState.Suppressed => "ระงับ (cooldown)",
        AlertState.Resolved => "กลับสู่ปกติ",
        _ => "ส่งไม่สำเร็จ",
    };

    public static string Range(ScheduleRange r) => r switch
    {
        ScheduleRange.PreviousWeek => "สัปดาห์ก่อน",
        ScheduleRange.PreviousMonth => "เดือนก่อน",
        ScheduleRange.MonthToDate => "ต้นเดือนถึงปัจจุบัน",
        ScheduleRange.Last7Days => "7 วันล่าสุด",
        _ => "30 วันล่าสุด",
    };

    public static string Display(ReportDisplay d) => d switch
    {
        ReportDisplay.Table => "ตาราง",
        ReportDisplay.Line => "กราฟเส้น",
        ReportDisplay.Bar => "กราฟแท่ง",
        _ => "กราฟวงกลม",
    };

    public static string Table(string t) => t switch
    {
        AnaTablesText.Sales => "ยอดขายสมาชิกรายวัน",
        AnaTablesText.Members => "ภาพรวมสมาชิก",
        AnaTablesText.Points => "คะแนนรายวัน",
        AnaTablesText.Coupons => "คูปองรายวัน",
        _ => t,
    };
}

/// <summary>ชื่อตาราง read model (ตรงกับ AnaTables ใน Core)</summary>
public static class AnaTablesText
{
    public const string Sales = "ana_member_daily";
    public const string Members = "ana_member_snapshot";
    public const string Points = "ana_points_daily";
    public const string Coupons = "ana_coupon_daily";

    public static IReadOnlyList<string> All { get; } = [Sales, Members, Points, Coupons];
}
