using System.ComponentModel.DataAnnotations;
using Common.Core.Domain;
using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Core.MemberAnalytics.Domain;

// DTO ของระบบวิเคราะห์ข้อมูลสมาชิกและโปรโมชัน (Claude.MD ส่วนที่ 3, §28–§43)

/// <summary>ช่วงเปรียบเทียบ</summary>
public enum CompareMode { None, PreviousPeriod, SamePeriodLastYear }

/// <summary>ความละเอียดของกราฟแนวเวลา</summary>
public enum Granularity { Day, Week, Month }

/// <summary>หน่วยของตัวเลข</summary>
public enum MetricUnit { Count, Baht, Percent, Points, Days, Ratio }

/// <summary>ชนิดของตัวเลข</summary>
public enum MetricKind { Fact, Computed, Estimate }

/// <summary>วิธีนับธุรกรรมของแคมเปญ (สูตรข้อ 4)</summary>
public enum Attribution { InPeriod, BenefitUsed, Influenced }

/// <summary>จุดเริ่มของ cohort</summary>
public enum CohortBy { Registration, FirstPurchase, SourceCampaign }

/// <summary>ค่าที่แสดงใน cohort</summary>
public enum CohortMeasure { Retention, CumulativeSales }

/// <summary>มิติแยกยอดซื้อ (AN-08)</summary>
public enum ValueBreakdown { Tier, Branch, Channel, Category, Brand }

/// <summary>มิติแยกของรายงานแคมเปญ</summary>
public enum CampaignDimension { Branch, Channel, Tier, Category, Brand, Day }

/// <summary>ระดับรายละเอียดของ drill-down</summary>
public enum DrillLevel { Member, Transaction }

/// <summary>ที่มาของงานส่งออก</summary>
public enum ExportSource { Report, Dashboard, Drilldown }

/// <summary>เหตุการณ์ของ funnel</summary>
public enum FunnelStep { MessageReceived, MessageOpened, MessageClicked, PortalView, CouponReceived, Registered, Purchased, RepeatPurchased }

/// <summary>ชนิดกฎแจ้งเตือน</summary>
public enum AlertType { CouponUsageLow, CouponUsageHigh, CampaignBudgetNear, PointsAdjustmentAbnormal, NoPosData, NewMembersDrop, RedemptionSpike }

/// <summary>สถานะเหตุการณ์แจ้งเตือน</summary>
public enum AlertState { Fired, Suppressed, Resolved, Failed }

/// <summary>ช่องทางส่งแจ้งเตือน</summary>
public enum AlertChannel { Email, InApp }

/// <summary>ชนิดเหตุการณ์ข้อความที่ ingest</summary>
public enum MessageEventType { Sent, Delivered, Opened, Clicked, PortalView }

/// <summary>ชนิดต้นทุนแคมเปญ</summary>
public enum CampaignCostType { Media, Messaging, Printing, Other }

/// <summary>ผลของรายการ ingest</summary>
public enum IngestItemStatus { Accepted, Stale, Unknown, Rejected }

/// <summary>ชนิดผู้ถูกกำหนดขอบเขต/ผู้ถูกแชร์</summary>
public enum ScopeSubjectType { User, Role }

/// <summary>ช่วงเวลาของรายงานตั้งเวลา</summary>
public enum ScheduleRange { PreviousWeek, PreviousMonth, MonthToDate, Last7Days, Last30Days }

/// <summary>รูปแบบการแสดงรายงาน</summary>
public enum ReportDisplay { Table, Line, Bar, Pie }

// ====================================================================== ตัวกรองและซอง response

/// <summary>ตัวกรองร่วม (§28.1) — รับจาก query string</summary>
public sealed class AnalyticsFilterDTO
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public CompareMode Compare { get; set; } = CompareMode.PreviousPeriod;
    public Granularity? Granularity { get; set; }
    public List<string> Branch { get; set; } = [];
    public List<SalesChannel> Channel { get; set; } = [];
    public List<string> Brand { get; set; } = [];
    public List<string> Category { get; set; } = [];
    public List<string> Tier { get; set; } = [];
    public List<RegisteredChannel> RegisteredChannel { get; set; } = [];
    public List<string> SourceCampaign { get; set; } = [];
    public List<string> Campaign { get; set; } = [];
    public Guid? SavedFilterId { get; set; }

    /// <summary>สำเนา</summary>
    public AnalyticsFilterDTO Clone() => new()
    {
        From = From, To = To, Compare = Compare, Granularity = Granularity, Branch = [.. Branch], Channel = [.. Channel], Brand = [.. Brand], Category = [.. Category],
        Tier = [.. Tier], RegisteredChannel = [.. RegisteredChannel], SourceCampaign = [.. SourceCampaign], Campaign = [.. Campaign], SavedFilterId = SavedFilterId,
    };
}

/// <summary>ตัวกรองที่ใช้จริง (หลังตัดตามขอบเขตและเติมค่าเริ่มต้น)</summary>
public sealed class AppliedFilterDTO
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public CompareMode Compare { get; set; }
    public DateOnly? CompareFrom { get; set; }
    public DateOnly? CompareTo { get; set; }
    public Granularity Granularity { get; set; }
    public List<string> Branch { get; set; } = [];
    public List<SalesChannel> Channel { get; set; } = [];
    public List<string> Brand { get; set; } = [];
    public List<string> Category { get; set; } = [];
    public List<string> Tier { get; set; } = [];
    public List<RegisteredChannel> RegisteredChannel { get; set; } = [];
    public List<string> SourceCampaign { get; set; } = [];
    public List<string> Campaign { get; set; } = [];
}

/// <summary>ซองของทุก response ของหน้าจอวิเคราะห์</summary>
/// <typeparam name="T">ข้อมูล</typeparam>
public sealed class AnalyticsResultDTO<T>
{
    public T Data { get; set; } = default!;
    public AppliedFilterDTO AppliedFilter { get; set; } = new();
    public bool ScopeEmpty { get; set; }
    public bool ScopeClamped { get; set; }
    public DateTimeOffset? DataAsOf { get; set; }
    public bool Stale { get; set; }
}

/// <summary>ตัวเลข 1 ตัว</summary>
public sealed class MetricValueDTO
{
    public string Metric { get; set; } = string.Empty;
    public decimal? Value { get; set; }
    public MetricUnit Unit { get; set; }
    public MetricKind Kind { get; set; }
    public decimal? CompareValue { get; set; }
    public decimal? ChangePct { get; set; }
    public string? InsufficientReason { get; set; }
    public string FormulaVersion { get; set; } = AnaFormula.Version;
    public string? Drilldown { get; set; }
    /// <summary>ขอบล่างของช่วงเชื่อมั่น 95% (เฉพาะ lift)</summary>
    public decimal? CiLow { get; set; }
    public decimal? CiHigh { get; set; }
    public bool? Significant { get; set; }
}

/// <summary>จุดของกราฟ</summary>
public sealed class SeriesPointDTO
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public decimal? Value { get; set; }
    public decimal? CompareValue { get; set; }
    public string? InsufficientReason { get; set; }
}

/// <summary>กราฟแนวเวลา / แยกกลุ่ม</summary>
public sealed class SeriesDTO
{
    public string Metric { get; set; } = string.Empty;
    public string? Label { get; set; }
    public MetricUnit Unit { get; set; }
    public List<SeriesPointDTO> Points { get; set; } = [];
}

/// <summary>คู่ key/value</summary>
public sealed class KeyValueDTO
{
    public string Key { get; set; } = string.Empty;
    public string? Label { get; set; }
    public decimal Value { get; set; }
}

/// <summary>ค่าคงที่ของสูตร</summary>
public static class AnaFormula
{
    /// <summary>เลขชุดสูตร — เปลี่ยนสูตรต้องเพิ่มเลข</summary>
    public const string Version = "ana-2026.1";

    public const string ZeroDenominator = "zero_denominator";
    public const string GroupTooSmall = "group_too_small";
    public const string MissingSource = "missing_source";
    public const string CostCoverageLow = "cost_coverage_low";
    public const string PeriodIncomplete = "period_incomplete";
    public const string NoControlGroup = "no_control_group";
    public const string ScopeEmpty = "scope_empty";
}

// ====================================================================== dashboard

public sealed class ExecutiveDashboardDTO
{
    public Dictionary<string, MetricValueDTO> Kpis { get; set; } = [];
    public SeriesDTO SalesTrend { get; set; } = new();
    public SeriesDTO MembersTrend { get; set; } = new();
    public List<KeyValueDTO> TopBranches { get; set; } = [];
    public List<AlertEventDTO> Alerts { get; set; } = [];
}

public sealed class ActivityGroupCountDTO
{
    public string Key { get; set; } = string.Empty;
    public MetricValueDTO Value { get; set; } = new();
}

public sealed class MemberDashboardDTO
{
    public List<SeriesDTO> NewByRegisteredChannel { get; set; } = [];
    public List<KeyValueDTO> ByStatus { get; set; } = [];
    public List<KeyValueDTO> ByTier { get; set; } = [];
    public List<ActivityGroupCountDTO> ActivityGroups { get; set; } = [];
    public MetricValueDTO NearTierUpgrade { get; set; } = new();
    public MetricValueDTO TierAtRisk { get; set; } = new();
    public MetricValueDTO VerificationRate { get; set; } = new();
}

public sealed class TopCampaignDTO
{
    public string CampaignCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public CampaignStatus Status { get; set; }
    public int Participants { get; set; }
    public decimal CampaignSales { get; set; }
}

public sealed class BudgetUsageDTO
{
    public string CampaignCode { get; set; } = string.Empty;
    public decimal Budget { get; set; }
    public decimal Used { get; set; }
    public decimal UsedPct { get; set; }
}

public sealed class PromotionDashboardDTO
{
    public List<KeyValueDTO> CampaignsByStatus { get; set; } = [];
    public Dictionary<string, MetricValueDTO> Kpis { get; set; } = [];
    public List<TopCampaignDTO> TopCampaigns { get; set; } = [];
    public List<BudgetUsageDTO> BudgetUsage { get; set; } = [];
}

// ====================================================================== สมาชิก

public sealed class ActivityGroupDTO
{
    public string Code { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public MetricValueDTO Count { get; set; } = new();
    public string Drilldown { get; set; } = string.Empty;
}

public sealed class ActivityGroupsDTO
{
    public List<ActivityGroupDTO> Groups { get; set; } = [];
    public Dictionary<string, int> Thresholds { get; set; } = [];
}

public sealed class RfmCellDTO
{
    public int R { get; set; }
    public int F { get; set; }
    public int Members { get; set; }
    public decimal AvgMonetary { get; set; }
    public string Group { get; set; } = string.Empty;
}

public sealed class RfmGroupDTO
{
    public string Code { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
    public int Members { get; set; }
    public decimal? SalesShare { get; set; }
}

public sealed class RfmBoundariesDTO
{
    public List<decimal> R { get; set; } = [];
    public List<decimal> F { get; set; } = [];
    public List<decimal> M { get; set; } = [];
}

public sealed class RfmDTO
{
    public List<RfmCellDTO> Cells { get; set; } = [];
    public List<RfmGroupDTO> Groups { get; set; } = [];
    public RfmBoundariesDTO Boundaries { get; set; } = new();
    public int MembersScored { get; set; }
    public string? InsufficientReason { get; set; }
}

/// <summary>บันทึกกลุ่ม RFM เป็น Segment</summary>
public sealed class RfmSegmentRequest
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(1000)] public string? Description { get; set; }
    /// <summary>ช่อง "R-F" เช่น "5-5"</summary>
    public List<string> Cells { get; set; } = [];
    public List<string> Groups { get; set; } = [];
    public AnalyticsFilterDTO Filter { get; set; } = new();
}

public sealed class SegmentSnapshotResultDTO
{
    public long SegmentId { get; set; }
    public int MemberCount { get; set; }
    public DateTimeOffset SnapshotAt { get; set; }
}

public sealed class CohortRowDTO
{
    public string Cohort { get; set; } = string.Empty;
    public int Size { get; set; }
    public List<decimal?> Values { get; set; } = [];
}

public sealed class CohortDTO
{
    public CohortBy CohortBy { get; set; }
    public CohortMeasure Measure { get; set; }
    public List<CohortRowDTO> Rows { get; set; } = [];
}

public sealed class RepeatPurchaseDTO
{
    public MetricValueDTO RepeatRate { get; set; } = new();
    public MetricValueDTO FirstPurchaseConversion { get; set; } = new();
    public MetricValueDTO AvgDaysBetweenPurchases { get; set; } = new();
    public MetricValueDTO RepeatWithinNDays { get; set; } = new();
    public List<KeyValueDTO> ByPurchaseCount { get; set; } = [];
    public SeriesDTO Trend { get; set; } = new();
}

public sealed class ValueBandDTO
{
    public string Key { get; set; } = string.Empty;
    public int Members { get; set; }
    public decimal Sales { get; set; }
}

public sealed class ValueBreakdownRowDTO
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Members { get; set; }
    public decimal Sales { get; set; }
    public decimal? AvgTicket { get; set; }
}

public sealed class PurchaseValueDTO
{
    public MetricValueDTO AvgSalesPerMember { get; set; } = new();
    public MetricValueDTO AvgTicket { get; set; } = new();
    public MetricValueDTO ItemsPerTicket { get; set; } = new();
    public List<ValueBandDTO> Distribution { get; set; } = [];
    public List<ValueBreakdownRowDTO> Breakdown { get; set; } = [];
}

public sealed class RegisteredChannelSourceDTO
{
    public string Key { get; set; } = string.Empty;
    public int NewMembers { get; set; }
    public decimal? VerifiedRate { get; set; }
    public decimal? FirstPurchaseRate { get; set; }
    public decimal? RepeatRate { get; set; }
}

public sealed class CampaignSourceDTO
{
    public string CampaignCode { get; set; } = string.Empty;
    public int NewMembers { get; set; }
    public decimal? FirstPurchaseRate { get; set; }
    public MetricValueDTO CostPerNewMember { get; set; } = new();
}

public sealed class MemberSourceDTO
{
    public List<RegisteredChannelSourceDTO> ByRegisteredChannel { get; set; } = [];
    public List<CampaignSourceDTO> BySourceCampaign { get; set; } = [];
}

// ====================================================================== คะแนน / คูปอง

public sealed class LargeAdjustmentDTO
{
    public long LedgerId { get; set; }
    public string MemberNoMasked { get; set; } = string.Empty;
    public int Points { get; set; }
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = string.Empty;
}

public sealed class PointsAnalyticsDTO
{
    public Dictionary<string, MetricValueDTO> Kpis { get; set; } = [];
    public List<SeriesDTO> FlowTrend { get; set; } = [];
    public List<KeyValueDTO> ExpiringNext { get; set; } = [];
    public List<LargeAdjustmentDTO> LargeAdjustments { get; set; } = [];
}

public sealed class CouponCampaignRowDTO
{
    public string CampaignCode { get; set; } = string.Empty;
    public int Issued { get; set; }
    public int Redeemed { get; set; }
    public decimal? UsageRate { get; set; }
    /// <summary>LowUsage | Abnormal | null</summary>
    public string? Flag { get; set; }
}

public sealed class CouponAnalyticsDTO
{
    public Dictionary<string, MetricValueDTO> Kpis { get; set; } = [];
    public List<SeriesDTO> Trend { get; set; } = [];
    public List<CouponCampaignRowDTO> ByCampaign { get; set; } = [];
}

// ====================================================================== แคมเปญ

public sealed class CampaignAnalyticsCriteria
{
    public List<CampaignStatus> Status { get; set; } = [];
    public List<RewardType> RewardType { get; set; } = [];
    [StringLength(100)] public string? Text { get; set; }
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
}

public sealed class CampaignAnalyticsSummaryDTO
{
    public string CampaignCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public CampaignStatus Status { get; set; }
    public RewardType RewardType { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public int HoldoutPct { get; set; }
    public int? EligibleMembers { get; set; }
    public int Participants { get; set; }
    public decimal CampaignSales { get; set; }
    public decimal Discount { get; set; }
    public bool HasCost { get; set; }
    public Dictionary<string, MetricValueDTO>? Kpis { get; set; }
}

public sealed class TargetSegmentDTO
{
    public long SegmentId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class CampaignReportDTO
{
    public CampaignAnalyticsSummaryDTO Campaign { get; set; } = new();
    public List<TargetSegmentDTO> TargetSegments { get; set; } = [];
    public Dictionary<string, MetricValueDTO> Kpis { get; set; } = [];
    public List<SeriesDTO> Trend { get; set; } = [];
    public Attribution Attribution { get; set; }
    public int InfluenceWindowDays { get; set; }
}

public sealed class BreakdownRowDTO
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Participants { get; set; }
    public int Transactions { get; set; }
    public decimal Sales { get; set; }
    public decimal Discount { get; set; }
    public decimal? AvgTicket { get; set; }
}

public sealed class CampaignLiftDTO
{
    public bool HasControl { get; set; }
    public int HoldoutPct { get; set; }
    public int Treated { get; set; }
    public int Control { get; set; }
    public MetricValueDTO PurchaseRateTreated { get; set; } = new();
    public MetricValueDTO PurchaseRateControl { get; set; } = new();
    public MetricValueDTO SalesPerMemberTreated { get; set; } = new();
    public MetricValueDTO SalesPerMemberControl { get; set; } = new();
    public MetricValueDTO Lift { get; set; } = new();
    public MetricValueDTO UpliftSales { get; set; } = new();
}

public sealed class CampaignRoiDTO
{
    public MetricValueDTO Discount { get; set; } = new();
    public MetricValueDTO PointsCost { get; set; } = new();
    public MetricValueDTO CampaignCost { get; set; } = new();
    public bool CostIncluded { get; set; }
    public MetricValueDTO GrossProfit { get; set; } = new();
    public MetricValueDTO GrossMarginPct { get; set; } = new();
    public decimal? CostCoveragePct { get; set; }
    public MetricValueDTO Roi { get; set; } = new();
    public MetricValueDTO BreakEvenSales { get; set; } = new();
}

public sealed class CampaignCompareDTO
{
    public List<CampaignAnalyticsSummaryDTO> Campaigns { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

// ====================================================================== drill-down

public sealed class DrilldownQuery
{
    [Required, StringLength(100)] public string Metric { get; set; } = string.Empty;
    public DrillLevel Level { get; set; } = DrillLevel.Member;
    [StringLength(50)] public string? Sort { get; set; }
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    [Range(1, 200)] public int PageSize { get; set; } = 50;
}

public sealed class ColumnDTO
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>String | Date | Count | Baht | Percent | Points</summary>
    public string Type { get; set; } = "String";
}

public sealed class DrilldownPageDTO
{
    public string Metric { get; set; } = string.Empty;
    public DrillLevel Level { get; set; }
    public bool LiveSource { get; set; } = true;
    public List<ColumnDTO> Columns { get; set; } = [];
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public long Total { get; set; }
}

public sealed class DrilldownSegmentRequest
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Metric { get; set; } = string.Empty;
    public AnalyticsFilterDTO Filter { get; set; } = new();
}

// ====================================================================== ตัวกรองที่บันทึก / รายงาน / ตั้งเวลา / ส่งออก

public sealed class SavedFilterDTO
{
    public Guid Id { get; set; }
    [Required, RegularExpression("^AN-[0-9]{2}F?$")] public string Page { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    public AnalyticsFilterDTO Filter { get; set; } = new();
    public bool IsDefault { get; set; }
    public string OwnerSub { get; set; } = string.Empty;
    public long Version { get; set; }
}

public sealed class CatalogMetricDTO
{
    public string Code { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public MetricUnit Unit { get; set; }
    public MetricKind Kind { get; set; }
    public string Requires { get; set; } = string.Empty;
    public List<string> Dimensions { get; set; } = [];
    public string FormulaVersion { get; set; } = AnaFormula.Version;
    public string Definition { get; set; } = string.Empty;
    /// <summary>รหัส drill-down (null = ดูต่อไม่ได้)</summary>
    public string? Drilldown { get; set; }
}

public sealed class CatalogDimensionDTO
{
    public string Code { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class MetricCatalogDTO
{
    public List<CatalogMetricDTO> Metrics { get; set; } = [];
    public List<CatalogDimensionDTO> Dimensions { get; set; } = [];
}

public sealed class ReportSortDTO
{
    [Required, StringLength(100)] public string Key { get; set; } = string.Empty;
    public bool Desc { get; set; }
}

public sealed class ReportShareDTO
{
    public ScopeSubjectType SubjectType { get; set; }
    [Required, StringLength(200)] public string Subject { get; set; } = string.Empty;
}

public sealed class ReportDefinitionDTO
{
    public Guid Id { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(1000)] public string? Description { get; set; }
    public bool Standard { get; set; }
    public string OwnerSub { get; set; } = string.Empty;
    public List<string> Dimensions { get; set; } = [];
    public List<string> Metrics { get; set; } = [];
    public AnalyticsFilterDTO Filter { get; set; } = new();
    public List<ReportSortDTO> Sort { get; set; } = [];
    public ReportDisplay Display { get; set; } = ReportDisplay.Table;
    public List<ReportShareDTO> Shares { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class ReportSummaryDTO
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Standard { get; set; }
    public bool Mine { get; set; }
    public bool Shared { get; set; }
    public string OwnerSub { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ReportResultDTO
{
    public List<ColumnDTO> Columns { get; set; } = [];
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
    public Dictionary<string, decimal?> Totals { get; set; } = [];
    public Dictionary<string, string?> Insufficient { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}

public sealed class ReportCopyRequest
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
}

public sealed class ScheduleRunDTO
{
    public DateTimeOffset At { get; set; }
    public JobStatus Status { get; set; }
    public string? Error { get; set; }
}

public sealed class ReportScheduleDTO
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    /// <summary>cron 5 ช่อง (นาที ชั่วโมง วัน เดือน วันในสัปดาห์)</summary>
    [Required, StringLength(100)] public string Cron { get; set; } = string.Empty;
    [StringLength(64)] public string Timezone { get; set; } = "Asia/Bangkok";
    public ScheduleRange Range { get; set; } = ScheduleRange.PreviousWeek;
    [RegularExpression("^(Xlsx|Csv)$")] public string Format { get; set; } = "Xlsx";
    public List<string> Recipients { get; set; } = [];
    public List<string> FailureRecipients { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? NextRunAt { get; set; }
    public ScheduleRunDTO? LastRun { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string OwnerSub { get; set; } = string.Empty;
    public long Version { get; set; }
}

public sealed class AnaExportRequest
{
    public ExportSource Source { get; set; }
    public Guid? ReportId { get; set; }
    /// <summary>หน้าจอ dashboard (AN-01 / AN-02 / AN-03)</summary>
    [StringLength(10)] public string? Page { get; set; }
    [StringLength(100)] public string? Metric { get; set; }
    public DrillLevel? Level { get; set; }
    public AnalyticsFilterDTO Filter { get; set; } = new();
    [RegularExpression("^(Xlsx|Csv)$")] public string Format { get; set; } = "Xlsx";
    /// <summary>ไม่ mask PII (ต้องมี member.pii)</summary>
    public bool Unmasked { get; set; }
}

/// <summary>งานส่งออกของ analytics (เก็บในตารางงานส่งออกของระบบสมาชิก รายงานขึ้นต้น <c>ana:</c>)</summary>
public sealed class AnaExportJobDTO
{
    public long Id { get; set; }
    public ExportSource Source { get; set; }
    public string Format { get; set; } = "Xlsx";
    public AnaExportRequest Request { get; set; } = new();
    public JobStatus Status { get; set; }
    public int RowCount { get; set; }
    public bool Masked { get; set; } = true;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid? ScheduleId { get; set; }
    public string? Error { get; set; }
}

// ====================================================================== ingest

public sealed class IngestItemResultDTO
{
    public int Index { get; set; }
    public string Key { get; set; } = string.Empty;
    public IngestItemStatus Status { get; set; }
    public string? Error { get; set; }
}

public sealed class IngestResultDTO
{
    public int Accepted { get; set; }
    public int Stale { get; set; }
    public int Unknown { get; set; }
    public int Rejected { get; set; }
    public List<IngestItemResultDTO> Results { get; set; } = [];
    public string? RequestId { get; set; }
}

public sealed class ProductIngestDTO
{
    [Required, StringLength(100)] public string ItemCode { get; set; } = string.Empty;
    [StringLength(300)] public string? Name { get; set; }
    [StringLength(100)] public string? Brand { get; set; }
    [StringLength(100)] public string? Category { get; set; }
    public bool Active { get; set; } = true;
    [Range(0, 100_000_000)] public decimal? UnitCost { get; set; }
    public DateOnly? CostEffectiveFrom { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProductCostDTO
{
    public DateOnly EffectiveFrom { get; set; }
    public decimal UnitCost { get; set; }
}

/// <summary>สินค้าที่เก็บไว้ (รวมประวัติต้นทุน)</summary>
public sealed class AnaProductDTO
{
    public string ItemCode { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Brand { get; set; }
    public string? Category { get; set; }
    public bool Active { get; set; } = true;
    public List<ProductCostDTO> Costs { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BranchIngestDTO
{
    [Required, StringLength(30)] public string BranchCode { get; set; } = string.Empty;
    [StringLength(200)] public string? Name { get; set; }
    [StringLength(50)] public string? Region { get; set; }
    public bool Active { get; set; } = true;
    [RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")] public string? OpenTime { get; set; }
    [RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")] public string? CloseTime { get; set; }
    public List<DayOfWeek> ClosedWeekdays { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CampaignCostIngestDTO
{
    [Required, StringLength(30)] public string CampaignCode { get; set; } = string.Empty;
    public DateOnly CostDate { get; set; }
    public CampaignCostType CostType { get; set; }
    [Range(0, 1_000_000_000)] public decimal Amount { get; set; }
    [StringLength(500)] public string? Note { get; set; }
    [Range(1, int.MaxValue)] public int Revision { get; set; } = 1;
}

public sealed class MessageEventIngestDTO
{
    public Guid EventId { get; set; }
    public MessageEventType Type { get; set; }
    [StringLength(100)] public string? MessageId { get; set; }
    [StringLength(30)] public string? CampaignCode { get; set; }
    [StringLength(30)] public string? MemberNo { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class SalesSummaryIngestDTO
{
    public DateOnly BusinessDate { get; set; }
    [Required, StringLength(30)] public string BranchCode { get; set; } = string.Empty;
    public SalesChannel Channel { get; set; }
    [Range(-1_000_000_000, 1_000_000_000)] public decimal NetSales { get; set; }
    [Range(0, 10_000_000)] public int Transactions { get; set; }
    [Range(1, int.MaxValue)] public int Revision { get; set; } = 1;
}

/// <summary>ผู้รับข้อความของแคมเปญ (บันทึกตอนส่งผ่าน campaign-messages)</summary>
public sealed class MessageRecipientDTO
{
    public string MessageId { get; set; } = string.Empty;
    public string? CampaignCode { get; set; }
    public long MemberId { get; set; }
    public DateTimeOffset SentAt { get; set; }
}

/// <summary>เหตุการณ์ข้อความที่เก็บไว้ (หา member / campaign แล้ว)</summary>
public sealed class MessageEventDTO
{
    public Guid EventId { get; set; }
    public MessageEventType Type { get; set; }
    public string? MessageId { get; set; }
    public string? CampaignCode { get; set; }
    public long MemberId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

// ====================================================================== funnel

public sealed class FunnelDTO
{
    public Guid Id { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [Range(1, 24 * 90)] public int MaxGapHours { get; set; } = 168;
    public List<FunnelStep> Steps { get; set; } = [];
    public long Version { get; set; }
}

public sealed class FunnelStepResultDTO
{
    public FunnelStep Step { get; set; }
    public int? Members { get; set; }
    public decimal? ConversionFromPrev { get; set; }
    public decimal? DropOff { get; set; }
    public decimal? MedianHoursFromPrev { get; set; }
    public string? InsufficientReason { get; set; }
}

public sealed class FunnelResultDTO
{
    public List<FunnelStepResultDTO> Steps { get; set; } = [];
    public MetricValueDTO OverallConversion { get; set; } = new();
}

// ====================================================================== alert

public sealed class AlertScopeDTO
{
    public List<string> Branches { get; set; } = [];
    public List<SalesChannel> Channels { get; set; } = [];
    public List<string> Brands { get; set; } = [];
}

public sealed class AlertRecipientDTO
{
    public AlertChannel Channel { get; set; }
    [Required, StringLength(200)] public string To { get; set; } = string.Empty;
}

public sealed class AlertRuleDTO
{
    public Guid Id { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    public AlertType Type { get; set; }
    public Dictionary<string, string> Params { get; set; } = [];
    public AlertScopeDTO Scope { get; set; } = new();
    public List<AlertRecipientDTO> Recipients { get; set; } = [];
    [Range(0, 60 * 24 * 30)] public int CooldownMinutes { get; set; } = 240;
    public bool Enabled { get; set; } = true;
    public string OwnerSub { get; set; } = string.Empty;
    public long Version { get; set; }
}

public sealed class AlertEventDTO
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public AlertType Type { get; set; }
    public string ScopeKey { get; set; } = string.Empty;
    public decimal? Value { get; set; }
    public decimal Threshold { get; set; }
    public AlertState State { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
    public string? Drilldown { get; set; }
    /// <summary>สาขาของเหตุการณ์ (ใช้กรองตามขอบเขตผู้ดู)</summary>
    public string? BranchCode { get; set; }
}

public sealed class AlertTestResultDTO
{
    public List<AlertEvaluationDTO> Results { get; set; } = [];
}

public sealed class AlertEvaluationDTO
{
    public string ScopeKey { get; set; } = string.Empty;
    public decimal? Value { get; set; }
    public decimal Threshold { get; set; }
    public bool WouldFire { get; set; }
    public string? BranchCode { get; set; }
    public string? Drilldown { get; set; }
}

public sealed class AlertEventCriteria
{
    public Guid? RuleId { get; set; }
    public AlertState? State { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    [Range(1, 200)] public int PageSize { get; set; } = 50;
}

// ====================================================================== ตั้งค่า / ขอบเขต / สถานะข้อมูล

public sealed class RefreshMinutesDTO
{
    [Range(1, 1440)] public int Coupon { get; set; } = 5;
    [Range(1, 1440)] public int Campaign { get; set; } = 15;
    [Range(1, 1440)] public int Sales { get; set; } = 60;
    [Range(1, 1440)] public int Member { get; set; } = 1440;
}

public sealed class AnalyticsSettingsDTO
{
    [Range(1, 3650)] public int ActiveDays { get; set; } = 90;
    [Range(1, 3650)] public int AtRiskDays { get; set; } = 180;
    [Range(1, 365)] public int FirstPurchaseDays { get; set; } = 30;
    [Range(1, 100)] public int RegularMinPurchases { get; set; } = 4;
    [Range(1, 3650)] public int RegularWindowDays { get; set; } = 180;
    [Range(1, 12)] public int RegularMinMonths { get; set; } = 3;
    [Range(1, 365)] public int ExpiringDays { get; set; } = 30;
    [Range(1, 365)] public int TierReviewDays { get; set; } = 30;
    [Range(1, 365)] public int TierRiskDays { get; set; } = 90;
    [Range(1, 100)] public int NearUpgradePct { get; set; } = 80;
    [Range(1, 90)] public int InfluenceWindowDays { get; set; } = 7;
    [Range(1, 100_000)] public int MinGroupSize { get; set; } = 100;
    [Range(1, 100)] public int CostCoverageMinPct { get; set; } = 95;
    public List<decimal> PurchaseValueBands { get; set; } = [500, 1000, 5000, 10000];
    [Range(1, 10_000_000)] public int LargeAdjustmentPoints { get; set; } = 1000;
    [Range(0, 1000)] public decimal PointCostBaht { get; set; } = 0.25m;
    public RefreshMinutesDTO RefreshMinutes { get; set; } = new();
    [Range(1, 10_080)] public int StaleAfterMinutes { get; set; } = 120;
    [Range(1, 731)] public int RecomputeDays { get; set; } = 35;
    [Range(1, 720)] public int ExportRetentionHours { get; set; } = 24;
    [Range(1, 1_000_000)] public int ExportMaxRows { get; set; } = 200_000;
    public List<string> AllowRecipientDomains { get; set; } = [];
    public long Version { get; set; }
}

public sealed class TableStatusDTO
{
    public string Table { get; set; } = string.Empty;
    public DateTimeOffset? LastRunAt { get; set; }
    public JobStatus? LastStatus { get; set; }
    public DateTimeOffset? Watermark { get; set; }
    public int DirtyDays { get; set; }
    public int? LagMinutes { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string? Error { get; set; }
}

public sealed class SourceStatusDTO
{
    public string Source { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public DateTimeOffset? LastReceivedAt { get; set; }
    public int Last24h { get; set; }
    public int Rejected24h { get; set; }
}

public sealed class DataStatusDTO
{
    public List<TableStatusDTO> Tables { get; set; } = [];
    public List<SourceStatusDTO> Sources { get; set; } = [];
    public int OpenIssues { get; set; }
}

public sealed class DataIssueDTO
{
    public long Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string? SampleJson { get; set; }
}

public sealed class RefreshRequest
{
    public List<string> Tables { get; set; } = [];
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
}

public sealed class RefreshRunDTO
{
    public Guid Id { get; set; }
    public List<string> Tables { get; set; } = [];
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public JobStatus Status { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class DataScopeRuleDTO
{
    public Guid Id { get; set; }
    public ScopeSubjectType SubjectType { get; set; }
    [Required, StringLength(200)] public string Subject { get; set; } = string.Empty;
    /// <summary>"*" = ทั้งหมดของมิตินั้น</summary>
    public List<string> Branches { get; set; } = [];
    public List<string> Channels { get; set; } = [];
    public List<string> Brands { get; set; } = [];
    [StringLength(500)] public string? Note { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class ScopeSourceDTO
{
    public ScopeSubjectType SubjectType { get; set; }
    public string Subject { get; set; } = string.Empty;
    public Guid RuleId { get; set; }
}

/// <summary>ขอบเขตที่มีผลของผู้ใช้ 1 คน</summary>
public sealed class EffectiveScopeDTO
{
    public string Sub { get; set; } = string.Empty;
    public bool All { get; set; }
    public List<string> Branches { get; set; } = [];
    public List<string> Channels { get; set; } = [];
    public List<string> Brands { get; set; } = [];
    public List<ScopeSourceDTO> Sources { get; set; } = [];
}

/// <summary>ขอบเขตข้อมูลที่ใช้ตอน query (null ของมิติ = ทั้งหมด)</summary>
public sealed record DataScope(bool Empty, IReadOnlySet<string>? Branches, IReadOnlySet<SalesChannel>? Channels, IReadOnlySet<string>? Brands)
{
    /// <summary>เห็นทั้งหมด</summary>
    public static DataScope All { get; } = new(false, null, null, null);

    /// <summary>ไม่เห็นอะไร</summary>
    public static DataScope None { get; } = new(true, new HashSet<string>(), new HashSet<SalesChannel>(), new HashSet<string>());

    /// <summary>ไม่จำกัดมิติใดเลย</summary>
    public bool IsAll => !Empty && Branches is null && Channels is null && Brands is null;
}

/// <summary>ผลจากการค้นหาแบบแบ่งหน้า</summary>
/// <typeparam name="T">ชนิดแถว</typeparam>
public static class AnaPaged
{
    /// <summary>แบ่งหน้าในหน่วยความจำ</summary>
    public static PagedResultDTO<T> Of<T>(IReadOnlyList<T> all, int page, int pageSize)
    {
        page = Math.Max(page, 1);
        return new PagedResultDTO<T>([.. all.Skip((page - 1) * pageSize).Take(pageSize)], all.Count, page, pageSize);
    }
}
