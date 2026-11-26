using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Core.MemberAnalytics.DataAccess;

// Data access ของระบบวิเคราะห์ข้อมูล — 2 implementation: ADO (PostgreSQL / SQL Server) และ InMemory (Development / test)
// - IAnaSourceDAO: อ่านข้อมูลต้นทางจากตาราง mbr_* (implement โดย store ของระบบสมาชิก)
// - IAnaReadModelDAO: read model ana_member_daily / ana_member_snapshot / ana_points_daily / ana_coupon_daily + วันที่ต้องคำนวณใหม่
// - IAnaDocumentDAO: ค่าตั้งและ master data แบบเอกสาร (ana_document)
// - IAnaMessageDAO: ผู้รับและเหตุการณ์ข้อความ (ana_message_recipient / ana_message_event)

/// <summary>อ่านข้อมูลต้นทางของระบบสมาชิกสำหรับคำนวณ read model และ drill-down</summary>
public interface IAnaSourceDAO
{
    /// <summary>รายการขายที่ <c>occurred_at</c> อยู่ในช่วง [from, to) เรียงตาม id</summary>
    Task<IReadOnlyList<SalesTransactionEntity>> ListTransactionsAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct);

    /// <summary>ledger ที่ <c>created_at</c> อยู่ในช่วง [from, to) เรียงตาม id</summary>
    Task<IReadOnlyList<PointLedgerDTO>> ListLedgerAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct);

    /// <summary>ประวัติคูปองในช่วง [from, to) เรียงตาม id</summary>
    Task<IReadOnlyList<CouponEventDTO>> ListCouponEventsAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct);

    /// <summary>คูปองที่สร้างในช่วง [from, to) เรียงตาม id</summary>
    Task<IReadOnlyList<CouponDTO>> ListCouponsCreatedAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct);

    /// <summary>คูปองสถานะ Expired ที่หมดอายุในช่วง [from, to) เรียงตาม id</summary>
    Task<IReadOnlyList<CouponDTO>> ListCouponsExpiredAsync(DateTimeOffset from, DateTimeOffset to, long afterId, int take, CancellationToken ct);

    /// <summary>จำนวนคูปองที่มีสิทธิ์ใช้ในช่วง (สร้างก่อน to, หมดอายุหลัง from, ไม่ถูกยกเลิก/หมดอายุก่อน from) ต่อแคมเปญ</summary>
    Task<IReadOnlyDictionary<long, int>> CountUsableCouponsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>คูปองที่ยังใช้ได้ต่อสมาชิก</summary>
    Task<IReadOnlyDictionary<long, int>> CountAvailableCouponsByMemberAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>สมาชิกทีละหน้าเรียงตาม id (เติม PointsAvailable)</summary>
    Task<IReadOnlyList<MemberEntity>> ListMembersAsync(long afterId, int take, CancellationToken ct);

    /// <summary>ยอด eligible สุทธิ (หักคืน) ของบิลที่ไม่ถูกยกเลิกตั้งแต่เวลา ต่อสมาชิก</summary>
    Task<IReadOnlyDictionary<long, decimal>> SumQualifyingSpendAsync(DateTimeOffset since, CancellationToken ct);

    /// <summary>ซื้อครั้งแรก/ล่าสุด/จำนวนบิลที่นับ (Completed, PartiallyReturned, ไม่ใช่รายการทดสอบ) ต่อสมาชิก</summary>
    Task<IReadOnlyList<MemberPurchaseStatsDTO>> ListPurchaseStatsAsync(CancellationToken ct);
}

/// <summary>สถิติการซื้อของสมาชิก 1 คน</summary>
public sealed class MemberPurchaseStatsDTO
{
    public long MemberId { get; set; }
    public DateTimeOffset FirstAt { get; set; }
    public DateTimeOffset LastAt { get; set; }
    public int Bills { get; set; }
}

/// <summary>ยอดของแคมเปญในวันเดียวของสมาชิก 1 คน</summary>
public sealed class AnaBenefitAggDTO
{
    public int Bills { get; set; }
    public decimal Sales { get; set; }
    public decimal Discount { get; set; }
    public int BonusPoints { get; set; }
}

/// <summary>แถวของ <c>ana_member_daily</c>: ยอดของสมาชิก 1 คนต่อวัน/สาขา/ช่องทาง</summary>
public sealed class AnaMemberDayDTO
{
    public DateOnly Day { get; set; }
    public long MemberId { get; set; }
    /// <summary>"" = ไม่ระบุสาขา</summary>
    public string BranchCode { get; set; } = string.Empty;
    public SalesChannel Channel { get; set; }
    public int Bills { get; set; }
    public decimal Gross { get; set; }
    public decimal Discount { get; set; }
    public decimal Returns { get; set; }
    /// <summary>ยอดขายสุทธิ = Gross − Discount − Returns</summary>
    public decimal Net { get; set; }
    public decimal Eligible { get; set; }
    public decimal PointsTender { get; set; }
    public int PointsEarned { get; set; }
    public decimal Items { get; set; }
    /// <summary>ยอดสุทธิต่อแบรนด์ (ตามสัดส่วนยอดจ่ายของบรรทัด)</summary>
    public Dictionary<string, decimal> Brands { get; set; } = [];
    /// <summary>จำนวนบิลที่มีสินค้าแบรนด์นั้น</summary>
    public Dictionary<string, int> BrandBills { get; set; } = [];
    public Dictionary<string, decimal> Categories { get; set; } = [];
    /// <summary>ต้นทุนสินค้า (เฉพาะบรรทัดที่มีต้นทุน) และยอดสุทธิของบรรทัดที่มีต้นทุน</summary>
    public decimal Cost { get; set; }
    public decimal CostedNet { get; set; }
    /// <summary>สิทธิ์แคมเปญที่ใช้ (รหัสแคมเปญ → ยอด)</summary>
    public Dictionary<string, AnaBenefitAggDTO> Benefits { get; set; } = [];
}

/// <summary>แถวของ <c>ana_member_snapshot</c>: สถานะของสมาชิก 1 คน ณ เวลาที่คำนวณ</summary>
public sealed class AnaMemberSnapshotDTO
{
    public long MemberId { get; set; }
    public string MemberNo { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? PhoneMasked { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public RegisteredChannel RegisteredChannel { get; set; }
    public string? SourceCampaignCode { get; set; }
    public MemberStatus Status { get; set; }
    public bool Verified { get; set; }
    public string? TierCode { get; set; }
    public DateTimeOffset? TierReviewAt { get; set; }
    public decimal? NextTierThreshold { get; set; }
    public decimal? RetainThreshold { get; set; }
    public decimal Spend12m { get; set; }
    public DateTimeOffset? FirstPurchaseAt { get; set; }
    public DateTimeOffset? LastPurchaseAt { get; set; }
    public int BillsTotal { get; set; }
    public int PointsAvailable { get; set; }
    public int PointsExpiringSoon { get; set; }
    public int UnusedCoupons { get; set; }
    /// <summary>สาขาที่ซื้อบ่อยที่สุด (ใช้ตัดตามขอบเขตสาขาของตัวเลขระดับสมาชิก)</summary>
    public string? HomeBranch { get; set; }
    public SalesChannel? HomeChannel { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }
}

/// <summary>แถวของ <c>ana_points_daily</c></summary>
public sealed class AnaPointsDayDTO
{
    public DateOnly Day { get; set; }
    public LedgerType Type { get; set; }
    public string BranchCode { get; set; } = string.Empty;
    public long Points { get; set; }
    public int Entries { get; set; }
}

/// <summary>เหตุการณ์คูปองที่นับ</summary>
public enum CouponMetricEvent { Issued, Distributed, Redeemed, Reversed, Expired, Cancelled }

/// <summary>แถวของ <c>ana_coupon_daily</c></summary>
public sealed class AnaCouponDayDTO
{
    public DateOnly Day { get; set; }
    public string CampaignCode { get; set; } = string.Empty;
    public long BatchId { get; set; }
    public CouponMetricEvent Event { get; set; }
    public string BranchCode { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>กลุ่มตารางของ read model</summary>
public static class AnaTables
{
    public const string Sales = "ana_member_daily";
    public const string Members = "ana_member_snapshot";
    public const string Points = "ana_points_daily";
    public const string Coupons = "ana_coupon_daily";

    public static IReadOnlyList<string> All { get; } = [Sales, Members, Points, Coupons];
}

/// <summary>read model</summary>
public interface IAnaReadModelDAO
{
    /// <summary>แทนที่ทุกแถวของวันนั้นใน transaction เดียว</summary>
    Task ReplaceMemberDaysAsync(DateOnly day, IReadOnlyList<AnaMemberDayDTO> rows, CancellationToken ct);

    /// <summary>แถวในช่วงวัน (null ของสาขา/ช่องทาง = ทั้งหมด)</summary>
    Task<IReadOnlyList<AnaMemberDayDTO>> ListMemberDaysAsync(DateOnly from, DateOnly to, IReadOnlyCollection<string>? branches, IReadOnlyCollection<SalesChannel>? channels, CancellationToken ct);

    /// <summary>แทนที่ snapshot ทั้งชุด (ลบสมาชิกที่ไม่มีแล้ว)</summary>
    Task ReplaceSnapshotsAsync(IReadOnlyList<AnaMemberSnapshotDTO> rows, CancellationToken ct);

    /// <summary>snapshot ทั้งหมด</summary>
    Task<IReadOnlyList<AnaMemberSnapshotDTO>> ListSnapshotsAsync(CancellationToken ct);

    Task ReplacePointsDayAsync(DateOnly day, IReadOnlyList<AnaPointsDayDTO> rows, CancellationToken ct);

    Task<IReadOnlyList<AnaPointsDayDTO>> ListPointsDaysAsync(DateOnly from, DateOnly to, CancellationToken ct);

    Task ReplaceCouponDayAsync(DateOnly day, IReadOnlyList<AnaCouponDayDTO> rows, CancellationToken ct);

    Task<IReadOnlyList<AnaCouponDayDTO>> ListCouponDaysAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>ทำเครื่องหมายวันที่ต้องคำนวณใหม่ของตาราง</summary>
    Task MarkDirtyAsync(string table, IReadOnlyCollection<DateOnly> days, CancellationToken ct);

    /// <summary>วันที่ต้องคำนวณใหม่</summary>
    Task<IReadOnlyList<DateOnly>> ListDirtyAsync(string table, CancellationToken ct);

    /// <summary>ลบเครื่องหมายเมื่อคำนวณเสร็จ (เฉพาะที่ทำเครื่องหมายก่อนเวลา <paramref name="markedBefore"/>)</summary>
    Task ClearDirtyAsync(string table, IReadOnlyCollection<DateOnly> days, DateTimeOffset markedBefore, CancellationToken ct);
}

/// <summary>เอกสาร 1 ชิ้นใน <c>ana_document</c></summary>
/// <typeparam name="T">ชนิดข้อมูล</typeparam>
public sealed record AnaDocument<T>(string Id, string? Owner, long Version, DateTimeOffset UpdatedAt, T Value);

/// <summary>ชื่อ collection ของเอกสาร</summary>
public static class AnaCollections
{
    public const string DataScope = "data-scope";
    public const string SavedFilter = "saved-filter";
    public const string Report = "report";
    public const string Schedule = "schedule";
    public const string AlertRule = "alert-rule";
    public const string AlertEvent = "alert-event";
    public const string Funnel = "funnel";
    public const string Settings = "settings";
    public const string RefreshRun = "refresh-run";
    public const string TableStatus = "table-status";
    public const string DataIssue = "data-issue";
    public const string SourceStatus = "source-status";
    public const string Branch = "branch";
    public const string Product = "product";
    public const string CampaignCost = "campaign-cost";
    public const string SalesSummary = "sales-summary";
}

/// <summary>ค่าตั้งและ master data แบบเอกสาร (id ไม่ซ้ำภายใน collection, version ตรวจตอนแก้)</summary>
public interface IAnaDocumentDAO
{
    Task<AnaDocument<T>?> GetAsync<T>(string collection, string id, CancellationToken ct) where T : new();

    /// <summary>ทุกเอกสารของ collection (owner = null คือทุกเจ้าของ) เรียงตาม updated_at ใหม่ก่อน</summary>
    Task<IReadOnlyList<AnaDocument<T>>> ListAsync<T>(string collection, string? owner, CancellationToken ct) where T : new();

    /// <summary>เพิ่ม (id ซ้ำ = false) — version เริ่ม 1</summary>
    Task<bool> InsertAsync<T>(string collection, string id, string? owner, T value, DateTimeOffset now, CancellationToken ct);

    /// <summary>แก้เมื่อ version ตรง (+1) — ไม่ตรง/ไม่มี = false</summary>
    Task<bool> UpdateAsync<T>(string collection, string id, string? owner, long expectedVersion, T value, DateTimeOffset now, CancellationToken ct);

    /// <summary>เพิ่มหรือแทนที่โดยไม่ตรวจ version</summary>
    Task UpsertAsync<T>(string collection, string id, string? owner, T value, DateTimeOffset now, CancellationToken ct);

    Task<bool> DeleteAsync(string collection, string id, CancellationToken ct);

    /// <summary>ลบเอกสารที่ updated_at ก่อนเวลา (ใช้ล้างเหตุการณ์เก่า)</summary>
    Task<int> PurgeAsync(string collection, DateTimeOffset before, CancellationToken ct);
}

/// <summary>ผู้รับและเหตุการณ์ข้อความ</summary>
public interface IAnaMessageDAO
{
    /// <summary>บันทึกผู้รับ (messageId ซ้ำ = ข้าม)</summary>
    Task InsertRecipientsAsync(IReadOnlyList<MessageRecipientDTO> recipients, CancellationToken ct);

    Task<MessageRecipientDTO?> GetRecipientAsync(string messageId, CancellationToken ct);

    /// <summary>ผู้รับของแคมเปญ (null = ทุกแคมเปญ) ที่ส่งในช่วง</summary>
    Task<IReadOnlyList<MessageRecipientDTO>> ListRecipientsAsync(string? campaignCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>บันทึกเหตุการณ์ — eventId ซ้ำ = false (ไม่บันทึกซ้ำ)</summary>
    Task<bool> TryInsertEventAsync(MessageEventDTO e, CancellationToken ct);

    /// <summary>เหตุการณ์ในช่วง (null = ทุกแคมเปญ)</summary>
    Task<IReadOnlyList<MessageEventDTO>> ListEventsAsync(string? campaignCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
