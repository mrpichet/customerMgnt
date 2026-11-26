using System.ComponentModel.DataAnnotations;

namespace CustomerMgnt.Core.Membership.Domain;

/// <summary>ค่ารางวัลของแคมเปญ (ใช้ field ตาม <see cref="RewardType"/>)</summary>
public sealed class CampaignRewardDTO
{
    /// <summary>PercentDiscount: %</summary>
    [Range(0, 100)] public decimal? Percent { get; set; }
    /// <summary>AmountDiscount / SpendThreshold: บาท</summary>
    [Range(0, 100_000_000)] public decimal? Amount { get; set; }
    /// <summary>เพดานส่วนลด (บาท)</summary>
    [Range(0, 100_000_000)] public decimal? Cap { get; set; }
    /// <summary>BuyXGetY: X</summary>
    [Range(1, 1000)] public int? BuyQty { get; set; }
    /// <summary>BuyXGetY: Y</summary>
    [Range(1, 1000)] public int? GetQty { get; set; }
    /// <summary>SpendThreshold: ยอดขั้นต่ำ</summary>
    [Range(0, 100_000_000)] public decimal? Threshold { get; set; }
    /// <summary>BonusPoints: คะแนน</summary>
    [Range(1, 10_000_000)] public int? BonusPoints { get; set; }
    /// <summary>PointMultiplier: ตัวคูณ (เช่น 2 = ได้ 2 เท่า)</summary>
    [Range(1, 20)] public decimal? Multiplier { get; set; }
    /// <summary>IssueCoupon: ชุดคูปองที่ออกให้</summary>
    public long? CouponBatchId { get; set; }

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public CampaignRewardDTO Clone() => (CampaignRewardDTO)MemberwiseClone();
}

/// <summary>เงื่อนไขการใช้แคมเปญ</summary>
public sealed class CampaignConditionsDTO
{
    [Range(0, 100_000_000)] public decimal MinSpend { get; set; }
    public List<string> ItemCodes { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public List<string> Branches { get; set; } = [];
    public List<SalesChannel> Channels { get; set; } = [];
    /// <summary>วันในสัปดาห์ (ว่าง = ทุกวัน)</summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = [];
    public TimeOnly? TimeFrom { get; set; }
    public TimeOnly? TimeTo { get; set; }
    /// <summary>ใช้เฉพาะเดือนเกิดของสมาชิก</summary>
    public bool BirthdayMonthOnly { get; set; }

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public CampaignConditionsDTO Clone() => new()
    {
        MinSpend = MinSpend, ItemCodes = [.. ItemCodes], Categories = [.. Categories], Branches = [.. Branches], Channels = [.. Channels],
        DaysOfWeek = [.. DaysOfWeek], TimeFrom = TimeFrom, TimeTo = TimeTo, BirthdayMonthOnly = BirthdayMonthOnly,
    };

    /// <summary>สินค้าเข้าเงื่อนไขหรือไม่ (ไม่กำหนดสินค้า/หมวด = ทุกรายการ)</summary>
    /// <param name="itemCode">รหัสสินค้า</param>
    /// <param name="category">หมวด</param>
    /// <returns>true ถ้าเข้าเงื่อนไข</returns>
    public bool MatchesItem(string itemCode, string? category) =>
        (ItemCodes.Count == 0 && Categories.Count == 0)
        || ItemCodes.Contains(itemCode, StringComparer.OrdinalIgnoreCase)
        || (category is not null && Categories.Contains(category, StringComparer.OrdinalIgnoreCase));
}

/// <summary>กลุ่มเป้าหมาย</summary>
public sealed class CampaignTargetsDTO
{
    public bool AllMembers { get; set; } = true;
    public List<string> TierCodes { get; set; } = [];
    public List<long> SegmentIds { get; set; } = [];

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public CampaignTargetsDTO Clone() => new() { AllMembers = AllMembers, TierCodes = [.. TierCodes], SegmentIds = [.. SegmentIds] };
}

/// <summary>จำกัดสิทธิและงบ (null = ไม่จำกัด)</summary>
public sealed class CampaignLimitsDTO
{
    [Range(1, 1_000_000)] public int? PerMemberTotal { get; set; }
    [Range(1, 1000)] public int? PerMemberPerDay { get; set; }
    [Range(1, 100_000_000)] public int? TotalUses { get; set; }
    /// <summary>งบประมาณ (บาท สำหรับส่วนลด / คะแนน × มูลค่าคะแนน สำหรับคะแนนพิเศษ)</summary>
    [Range(0, 10_000_000_000)] public decimal? Budget { get; set; }

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public CampaignLimitsDTO Clone() => (CampaignLimitsDTO)MemberwiseClone();
}

/// <summary>การใช้แคมเปญสะสม</summary>
public sealed class CampaignUsageDTO
{
    public int Uses { get; set; }
    public int Reserved { get; set; }
    public decimal BudgetUsed { get; set; }
    public decimal BudgetReserved { get; set; }
}

/// <summary>แคมเปญ / โปรโมชัน</summary>
public sealed class CampaignDTO
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public RewardType RewardType { get; set; }
    public CampaignRewardDTO Reward { get; set; } = new();
    public PromotionLevel Level { get; set; } = PromotionLevel.Bill;
    /// <summary>ต้องใช้คูปองของแคมเปญนี้</summary>
    public bool RequiresCoupon { get; set; }
    public CampaignConditionsDTO Conditions { get; set; } = new();
    public CampaignTargetsDTO Targets { get; set; } = new();
    public StackMode StackMode { get; set; } = StackMode.Exclusive;
    public string? ExclusionGroup { get; set; }
    /// <summary>น้อย = สำคัญกว่า (ใช้ตัดสินเมื่อส่วนลดเท่ากัน)</summary>
    public int Priority { get; set; } = 100;
    public CampaignLimitsDTO Limits { get; set; } = new();
    /// <summary>% กลุ่มควบคุม (0 = ไม่มี) — สมาชิกกลุ่มนี้ไม่ได้รับสิทธิ์และข้อความของแคมเปญ</summary>
    public int HoldoutPct { get; set; }
    public CampaignStatus Status { get; set; }
    public CampaignUsageDTO Usage { get; set; } = new();
    public long? ApprovalRequestId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }

    /// <summary>สำเนาลึก</summary>
    /// <returns>object ใหม่</returns>
    public CampaignDTO Clone()
    {
        var c = (CampaignDTO)MemberwiseClone();
        c.Reward = Reward.Clone();
        c.Conditions = Conditions.Clone();
        c.Targets = Targets.Clone();
        c.Limits = Limits.Clone();
        c.Usage = new CampaignUsageDTO { Uses = Usage.Uses, Reserved = Usage.Reserved, BudgetUsed = Usage.BudgetUsed, BudgetReserved = Usage.BudgetReserved };
        return c;
    }

    /// <summary>แคมเปญเป็นส่วนลดเงิน (ไม่ใช่คะแนน/คูปอง)</summary>
    public bool IsDiscount => RewardType is RewardType.PercentDiscount or RewardType.AmountDiscount or RewardType.BuyXGetY or RewardType.SpendThreshold;
}

/// <summary>แถวในรายการแคมเปญ</summary>
public sealed class CampaignSummaryDTO
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public RewardType RewardType { get; set; }
    public CampaignStatus Status { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public decimal? Budget { get; set; }
    public decimal BudgetUsed { get; set; }
    public int Uses { get; set; }
}

/// <summary>สร้าง/แก้แคมเปญ</summary>
public sealed class CampaignRequest
{
    [Required, RegularExpression("^[A-Z0-9_-]{2,30}$")] public string Code { get; set; } = string.Empty;
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(2000)] public string? Description { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public RewardType RewardType { get; set; }
    [Required] public CampaignRewardDTO Reward { get; set; } = new();
    public PromotionLevel Level { get; set; } = PromotionLevel.Bill;
    public bool RequiresCoupon { get; set; }
    [Required] public CampaignConditionsDTO Conditions { get; set; } = new();
    [Required] public CampaignTargetsDTO Targets { get; set; } = new();
    public StackMode StackMode { get; set; } = StackMode.Exclusive;
    [StringLength(50)] public string? ExclusionGroup { get; set; }
    [Range(1, 10_000)] public int Priority { get; set; } = 100;
    /// <summary>null = ใช้ค่าเริ่มต้นจากกฎธุรกิจ (1 ครั้งต่อสมาชิกต่อวัน)</summary>
    public CampaignLimitsDTO? Limits { get; set; }
    /// <summary>% กลุ่มควบคุม 0–50 (แก้ได้เฉพาะ Draft)</summary>
    [Range(0, 50)] public int HoldoutPct { get; set; }
    public long? Version { get; set; }
}

/// <summary>คำสั่งเปลี่ยนสถานะแคมเปญ</summary>
public sealed class CampaignActionRequest
{
    [Required] public long? Version { get; set; }
    [StringLength(500)] public string? Reason { get; set; }
}

/// <summary>ค้นหาแคมเปญ</summary>
public sealed class CampaignSearchCriteria
{
    public CampaignStatus? Status { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public RewardType? RewardType { get; set; }
    [StringLength(100)] public string? Text { get; set; }
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    [Range(1, 200)] public int PageSize { get; set; } = 20;
}

/// <summary>ชุดคูปอง</summary>
public sealed class CouponBatchDTO
{
    public long Id { get; set; }
    public long CampaignId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int Generated { get; set; }
    public string CodePrefix { get; set; } = string.Empty;
    public int CodeLength { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>ออกให้สมาชิกที่ระบุ (true) หรือใช้ได้ทุกคนที่ถือรหัส</summary>
    public bool PerMember { get; set; }
    /// <summary>แลกด้วยคะแนนใน Portal (null = แลกไม่ได้)</summary>
    public int? PointsCost { get; set; }
    public JobStatus Status { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>สร้างชุดคูปอง</summary>
public sealed class CouponBatchRequest
{
    public long CampaignId { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    /// <summary>จำนวนรหัสที่สร้างล่วงหน้า (0 = สร้างตอนออกให้สมาชิก)</summary>
    [Range(0, 1_000_000)] public int Quantity { get; set; }
    [Required, RegularExpression("^[A-Z0-9]{0,8}$")] public string CodePrefix { get; set; } = string.Empty;
    [Range(8, 16)] public int CodeLength { get; set; } = 10;
    public DateTimeOffset ExpiresAt { get; set; }
    public bool PerMember { get; set; }
    [Range(1, 10_000_000)] public int? PointsCost { get; set; }
}

/// <summary>คูปอง</summary>
public sealed class CouponDTO
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public long BatchId { get; set; }
    public long CampaignId { get; set; }
    public long? MemberId { get; set; }
    public CouponStatus Status { get; set; }
    public string? ReservationId { get; set; }
    public string? RedeemedTransactionId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }
    /// <summary>ชื่อแคมเปญ (เติมตอนแสดงผล)</summary>
    public string? CampaignName { get; set; }
    /// <summary>ข้อมูลสำหรับ QR ที่ Portal แสดงให้แคชเชียร์สแกน (= รหัสคูปอง)</summary>
    public string? QrPayload { get; set; }

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public CouponDTO Clone() => (CouponDTO)MemberwiseClone();
}

/// <summary>ประวัติคูปอง</summary>
public sealed class CouponEventDTO
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public CouponStatus FromStatus { get; set; }
    public CouponStatus ToStatus { get; set; }
    public string? Reference { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

/// <summary>คูปองพร้อมประวัติ</summary>
public sealed class CouponDetailDTO
{
    public CouponDTO Coupon { get; set; } = new();
    public List<CouponEventDTO> History { get; set; } = [];
}

/// <summary>ค้นหาคูปอง</summary>
public sealed class CouponSearchCriteria
{
    [StringLength(40)] public string? Code { get; set; }
    public long? BatchId { get; set; }
    public long? CampaignId { get; set; }
    public long? MemberId { get; set; }
    public CouponStatus? Status { get; set; }
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    [Range(1, 500)] public int PageSize { get; set; } = 50;
}

/// <summary>ออกคูปองให้สมาชิก</summary>
public sealed class IssueCouponsRequest
{
    public long BatchId { get; set; }
    public List<long>? MemberIds { get; set; }
    public long? SegmentId { get; set; }
}

/// <summary>ยกเลิกคูปอง</summary>
public sealed class CancelCouponRequest
{
    [Required, StringLength(500, MinimumLength = 3)] public string Reason { get; set; } = string.Empty;
}

/// <summary>ของรางวัลที่แลกด้วยคะแนนใน Portal (= ชุดคูปองที่มี PointsCost และแคมเปญ Active)</summary>
public sealed class RewardDTO
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int PointsCost { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Affordable { get; set; }
}

/// <summary>เงื่อนไข segment (ต้นไม้ And/Or)</summary>
public sealed class SegmentRuleDTO
{
    /// <summary>And | Or (เฉพาะ node กลุ่ม)</summary>
    public string? Op { get; set; }
    /// <summary>เงื่อนไขย่อย (node กลุ่ม)</summary>
    public List<SegmentRuleDTO>? Items { get; set; }
    /// <summary>field (node ใบ) — ดู SegmentRuleEvaluator.Fields</summary>
    public string? Field { get; set; }
    /// <summary>Eq, Ne, In, NotIn, Gt, Gte, Lt, Lte, Between</summary>
    public string? Operator { get; set; }
    /// <summary>ค่า (ตัวเลข ข้อความ หรือ array)</summary>
    public System.Text.Json.JsonElement? Value { get; set; }
}

/// <summary>Segment</summary>
public sealed class SegmentDTO
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public SegmentRuleDTO Rule { get; set; } = new();
    public bool RefreshDaily { get; set; } = true;
    public int MemberCount { get; set; }
    public DateTimeOffset? RefreshedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public long Version { get; set; }
}

/// <summary>สร้าง/แก้ segment</summary>
public sealed class SegmentRequest
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(2000)] public string? Description { get; set; }
    [Required] public SegmentRuleDTO Rule { get; set; } = new();
    public bool RefreshDaily { get; set; } = true;
    public long? Version { get; set; }
}

/// <summary>ผลดูตัวอย่าง segment</summary>
public sealed class SegmentPreviewDTO
{
    public int Count { get; set; }
    public List<MemberSummaryDTO> Sample { get; set; } = [];
}

/// <summary>ข้อเท็จจริงของสมาชิกที่ segment ใช้ (คำนวณจากข้อมูลสมาชิก + ยอดซื้อ)</summary>
public sealed class MemberFactsDTO
{
    public long MemberId { get; set; }
    public string? TierCode { get; set; }
    public MemberStatus Status { get; set; }
    public int? AgeYears { get; set; }
    public Gender Gender { get; set; }
    public string? Province { get; set; }
    public int PointsAvailable { get; set; }
    public decimal Spend12m { get; set; }
    public int Frequency12m { get; set; }
    public int? RecencyDays { get; set; }
    public int RfmR { get; set; }
    public int RfmF { get; set; }
    public int RfmM { get; set; }
    public bool MarketingConsent { get; set; }
}
