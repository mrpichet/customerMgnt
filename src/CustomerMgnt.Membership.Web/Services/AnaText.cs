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
        decimal d when type is "Baht" or "Decimal" => d.ToString("N2", CultureInfo.InvariantCulture),
        decimal d when type is "Percent" => d.ToString("N1", CultureInfo.InvariantCulture) + "%",
        decimal d => d.ToString("#,0.##", CultureInfo.InvariantCulture),
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

/// <summary>เส้นทางของหน้าวิเคราะห์ตามรหัสหน้าจอ (ใช้เปิดตัวกรองที่บันทึก)</summary>
public static class AnaRoutes
{
    private static readonly Dictionary<string, (string Path, string Name)> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AN-01"] = ("analytics", "ภาพรวมผู้บริหาร"), ["AN-02"] = ("analytics/members", "ภาพรวมสมาชิก"), ["AN-03"] = ("analytics/promotions", "ภาพรวมโปรโมชัน"),
        ["AN-04"] = ("analytics/members/activity", "กลุ่มการใช้งาน"), ["AN-05"] = ("analytics/members/rfm", "RFM"), ["AN-06"] = ("analytics/members/cohorts", "Cohort"),
        ["AN-07"] = ("analytics/members/repeat", "การกลับมาซื้อ"), ["AN-08"] = ("analytics/members/value", "มูลค่าการซื้อ"), ["AN-10"] = ("analytics/points", "คะแนน"),
        ["AN-11"] = ("analytics/coupons", "คูปอง"), ["AN-12"] = ("analytics/campaigns", "ผลแคมเปญ"), ["AN-14"] = ("analytics/campaigns/compare", "เปรียบเทียบแคมเปญ"),
        ["AN-15"] = ("analytics/members/sources", "แหล่งที่มา"),
    };

    /// <summary>path ของหน้า (ไม่รู้จัก = null)</summary>
    public static string? Path(string page) => Pages.TryGetValue(page, out var v) ? v.Path : null;

    /// <summary>ชื่อหน้า</summary>
    public static string Name(string page) => Pages.TryGetValue(page, out var v) ? $"{page} {v.Name}" : page;
}

/// <summary>คำอธิบายรอบเวลา cron 5 ช่องแบบอ่านง่าย (รูปแบบที่ใช้บ่อย)</summary>
public static class AnaSchedule
{
    private static readonly string[] Days = ["อาทิตย์", "จันทร์", "อังคาร", "พุธ", "พฤหัสบดี", "ศุกร์", "เสาร์"];

    /// <summary>รอบเวลาที่ใช้บ่อย</summary>
    public static IReadOnlyList<(string Cron, string Text)> Presets { get; } =
    [
        ("0 8 * * *", "ทุกวัน 08:00"), ("0 8 * * 1", "ทุกวันจันทร์ 08:00"), ("0 8 * * 1-5", "วันจันทร์–ศุกร์ 08:00"), ("0 8 1 * *", "วันที่ 1 ของทุกเดือน 08:00"),
    ];

    /// <summary>คำอธิบาย (รูปแบบซับซ้อน = แสดง cron เดิม)</summary>
    public static string Describe(string? cron)
    {
        string[] p = (cron ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 5 || !int.TryParse(p[0], out int mi) || !int.TryParse(p[1], out int h) || p[3] != "*")
        {
            return cron ?? string.Empty;
        }

        string time = $"{h:00}:{mi:00}";
        return (p[2], p[4]) switch
        {
            ("*", "*") => $"ทุกวัน {time}",
            ("*", "1-5") => $"วันจันทร์–ศุกร์ {time}",
            ("*", var d) when int.TryParse(d, out int w) && w is >= 0 and <= 7 => $"ทุกวัน{Days[w % 7]} {time}",
            (var dm, "*") when int.TryParse(dm, out int day) => $"วันที่ {day} ของทุกเดือน {time}",
            _ => cron!,
        };
    }
}

/// <summary>ข้อความของกฎ/เหตุการณ์แจ้งเตือน</summary>
public static class AnaAlertText
{
    /// <summary>ชื่อพารามิเตอร์</summary>
    public static string ParamLabel(string key) => key switch
    {
        "windowDays" => "ช่วงที่ดู (วัน)",
        "threshold" => "เกณฑ์อัตราการใช้ (%)",
        "campaign" => "รหัสแคมเปญ (ว่าง = ทุกแคมเปญ)",
        "pct" => "เกณฑ์ (%)",
        "minCount" => "จำนวนรายการขั้นต่ำ",
        "minutes" => "ไม่มีข้อมูลนานเกิน (นาที)",
        "multiplier" => "มากกว่าค่าเฉลี่ย 7 วัน (เท่า)",
        _ => key,
    };

    /// <summary>สรุปเงื่อนไขของกฎ</summary>
    public static string Params(AlertRuleDTO r) =>
        string.Join(", ", r.Params.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{ParamLabel(p.Key)} {p.Value}"));

    /// <summary>ขอบเขตของเหตุการณ์</summary>
    public static string Scope(string key)
    {
        int i = key.IndexOf(':', StringComparison.Ordinal);
        if (i < 0) return key == "all" ? "ทั้งหมด" : key;
        string v = key[(i + 1)..];
        return key[..i] switch
        {
            "campaign" => $"แคมเปญ {v}",
            "branch" => $"สาขา {v}",
            "day" => $"วันที่ {v}",
            _ => key,
        };
    }

    /// <summary>ลิงก์ดูรายละเอียด (ไม่มี = null)</summary>
    public static string? Link(string? drilldown) => drilldown switch
    {
        null or "" => null,
        _ when drilldown.StartsWith("campaign.", StringComparison.Ordinal) => $"analytics/campaigns/{Uri.EscapeDataString(drilldown["campaign.".Length..])}",
        _ => $"analytics/drilldown?metric={Uri.EscapeDataString(drilldown)}&back=analytics/alerts",
    };
}
