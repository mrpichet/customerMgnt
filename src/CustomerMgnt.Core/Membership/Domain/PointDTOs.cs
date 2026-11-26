using System.ComponentModel.DataAnnotations;

namespace CustomerMgnt.Core.Membership.Domain;

/// <summary>ยอดคะแนนสรุปต่อสมาชิก (ตาราง <c>mbr_point_account</c>) — แก้ด้วย optimistic lock (<see cref="Version"/>)</summary>
public sealed class PointAccountDTO
{
    public long MemberId { get; set; }
    /// <summary>Σ lot.remaining ของ lot ที่ยังไม่หมดอายุ (ไม่หักจอง)</summary>
    public int LotBalance { get; set; }
    /// <summary>คะแนนที่ถูกจองโดย reservation</summary>
    public int Reserved { get; set; }
    /// <summary>คะแนนติดลบจากการคืนสินค้า (หักจากคะแนนครั้งต่อไป)</summary>
    public int Debt { get; set; }
    /// <summary>คะแนนที่ได้รับสะสมทั้งหมด</summary>
    public long LifetimeEarned { get; set; }
    /// <summary>0 = ยังไม่มีแถว</summary>
    public long Version { get; set; }

    /// <summary>คะแนนที่ใช้ได้ = max(lot − reserved − debt, 0)</summary>
    public int Available => Math.Max(LotBalance - Reserved - Debt, 0);

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public PointAccountDTO Clone() => (PointAccountDTO)MemberwiseClone();
}

/// <summary>คะแนนที่จะหมดอายุในวันหนึ่ง</summary>
public sealed class ExpiringPointsDTO
{
    public DateTimeOffset ExpiresAt { get; set; }
    public int Points { get; set; }
}

/// <summary>ยอดคะแนน (API)</summary>
public sealed class PointBalanceDTO
{
    public long MemberId { get; set; }
    public int Available { get; set; }
    public int Reserved { get; set; }
    public int Debt { get; set; }
    public List<ExpiringPointsDTO> ExpiringSoon { get; set; } = [];
    public long LifetimeEarned { get; set; }
    public long Version { get; set; }
}

/// <summary>lot คะแนน (แต่ละครั้งที่ได้รับ)</summary>
public sealed class PointLotDTO
{
    public long Id { get; set; }
    public long MemberId { get; set; }
    public int Points { get; set; }
    public int Remaining { get; set; }
    public DateTimeOffset EarnedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>idempotency key ของ ledger ที่สร้าง lot นี้</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public PointLotDTO Clone() => (PointLotDTO)MemberwiseClone();
}

/// <summary>รายการคะแนน (append-only)</summary>
public sealed class PointLedgerDTO
{
    public long Id { get; set; }
    public long MemberId { get; set; }
    public LedgerType Type { get; set; }
    /// <summary>มีเครื่องหมาย (+ ได้, − ใช้)</summary>
    public int Points { get; set; }
    /// <summary>คะแนนที่ใช้ได้หลังรายการ</summary>
    public int BalanceAfter { get; set; }
    public string? SourceSystem { get; set; }
    public string? TransactionId { get; set; }
    /// <summary>อ้างอิง เช่น rule:2, campaign:7, adjustment:5, lot:9</summary>
    public string? Reference { get; set; }
    public string? Reason { get; set; }
    /// <summary>user:{sub} | client:{clientId} | member:{id} | system</summary>
    public string Actor { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    /// <summary>ไม่ซ้ำทั้งระบบ — ส่งซ้ำไม่บันทึกซ้ำ</summary>
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>การตัดคะแนนจาก lot (FEFO) ของ ledger หนึ่งรายการ</summary>
public sealed class LotAllocationDTO
{
    public string LedgerKey { get; set; } = string.Empty;
    public long LotId { get; set; }
    public int Points { get; set; }
}

/// <summary>ค้นหา ledger</summary>
public sealed class LedgerCriteria
{
    public long? MemberId { get; set; }
    public LedgerType? Type { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    [StringLength(100)] public string? TransactionId { get; set; }
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    [Range(1, 500)] public int PageSize { get; set; } = 50;
}

/// <summary>กฎการได้คะแนน</summary>
public sealed class EarnRuleDTO
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>กี่บาทได้ 1 คะแนน</summary>
    public decimal SpendPerPoint { get; set; } = 25m;
    public decimal MinSpend { get; set; }
    public int? MaxPointsPerTransaction { get; set; }
    public List<string> IncludeCategories { get; set; } = [];
    public List<string> ExcludeItemCodes { get; set; } = [];
    public List<string> Branches { get; set; } = [];
    public List<SalesChannel> Channels { get; set; } = [];
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public EarnRuleStatus Status { get; set; }
    public long? ApprovalRequestId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }

    /// <summary>กฎใช้กับสาขา/ช่องทาง/เวลานี้หรือไม่</summary>
    /// <param name="branch">สาขา</param>
    /// <param name="channel">ช่องทาง</param>
    /// <param name="at">เวลา</param>
    /// <returns>true ถ้าใช้ได้</returns>
    public bool AppliesTo(string? branch, SalesChannel channel, DateTimeOffset at) =>
        Status == EarnRuleStatus.Active && StartAt <= at && (EndAt is null || at < EndAt)
        && (Branches.Count == 0 || (branch is not null && Branches.Contains(branch, StringComparer.OrdinalIgnoreCase)))
        && (Channels.Count == 0 || Channels.Contains(channel));
}

/// <summary>สร้าง/แก้กฎการได้คะแนน</summary>
public sealed class EarnRuleRequest
{
    [Required, RegularExpression("^[A-Z0-9_-]{1,30}$")] public string Code { get; set; } = string.Empty;
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [Range(0.01, 1_000_000)] public decimal SpendPerPoint { get; set; } = 25m;
    [Range(0, 100_000_000)] public decimal MinSpend { get; set; }
    [Range(1, 10_000_000)] public int? MaxPointsPerTransaction { get; set; }
    public List<string> IncludeCategories { get; set; } = [];
    public List<string> ExcludeItemCodes { get; set; } = [];
    public List<string> Branches { get; set; } = [];
    public List<SalesChannel> Channels { get; set; } = [];
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    /// <summary>true = ส่งอนุมัติทันที (ไม่เช่นนั้นเป็น Draft)</summary>
    public bool Submit { get; set; } = true;
    public long? Version { get; set; }
}

/// <summary>ขอปรับคะแนน</summary>
public sealed class AdjustmentRequest
{
    public long MemberId { get; set; }
    /// <summary>บวก = เพิ่ม, ลบ = ลด (≠ 0)</summary>
    [Range(-10_000_000, 10_000_000)] public int Points { get; set; }
    [Required, StringLength(500, MinimumLength = 3)] public string Reason { get; set; } = string.Empty;
    [StringLength(200)] public string? AttachmentRef { get; set; }
}

/// <summary>คำขอปรับคะแนน</summary>
public sealed class AdjustmentDTO
{
    public long Id { get; set; }
    public long MemberId { get; set; }
    public int Points { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? AttachmentRef { get; set; }
    public AdjustmentStatus Status { get; set; }
    public long? ApprovalRequestId { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
