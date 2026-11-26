using System.ComponentModel.DataAnnotations;

namespace CustomerMgnt.Core.Membership.Domain;

/// <summary>ข้อมูลสมาชิกที่แคชเชียร์ต้องใช้ (น้อยที่สุด)</summary>
public sealed class PosMemberDTO
{
    public string MemberNo { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public MemberStatus Status { get; set; }
    public string? TierCode { get; set; }
    public int PointsAvailable { get; set; }
    public decimal PointValue { get; set; }
    public int MaxPointsPayPercent { get; set; }
    public List<PosCouponDTO> Coupons { get; set; } = [];
}

/// <summary>คูปองที่สมาชิกใช้ได้</summary>
public sealed class PosCouponDTO
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>รายการสินค้าในตะกร้า</summary>
public sealed class QuoteLineRequest
{
    [Range(1, 10_000)] public int LineNo { get; set; }
    [Required, StringLength(50)] public string ItemCode { get; set; } = string.Empty;
    [StringLength(50)] public string? Category { get; set; }
    [Range(0.001, 1_000_000)] public decimal Qty { get; set; }
    [Range(0, 100_000_000)] public decimal UnitPrice { get; set; }
    [Range(0, 100_000_000)] public decimal LineDiscount { get; set; }
}

/// <summary>ตะกร้าที่ POS ส่งมาคำนวณ</summary>
public sealed class QuoteRequest
{
    [Required, StringLength(20)] public string MemberNo { get; set; } = string.Empty;
    [StringLength(30)] public string? BranchCode { get; set; }
    public SalesChannel Channel { get; set; } = SalesChannel.Pos;
    public DateTimeOffset? OccurredAt { get; set; }
    [Required, MinLength(1), MaxLength(500)] public List<QuoteLineRequest> Lines { get; set; } = [];
    /// <summary>ส่วนลดท้ายบิลของร้าน (ไม่ใช่โปรโมชันของระบบสมาชิก)</summary>
    [Range(0, 100_000_000)] public decimal BillDiscount { get; set; }
    [MaxLength(10)] public List<string> CouponCodes { get; set; } = [];
    [Range(0, 10_000_000)] public int PointsToRedeem { get; set; }
    /// <summary>reserve: quote ที่แคชเชียร์แสดงลูกค้า (ถ้าผลต่าง = 409 quote_changed)</summary>
    [StringLength(80)] public string? QuoteId { get; set; }
    /// <summary>simulate: แคมเปญที่ต้องการทดสอบ (รวม Draft ได้)</summary>
    public List<long>? CampaignIds { get; set; }
}

/// <summary>สิทธิประโยชน์ที่ได้ในบิล</summary>
public sealed class BenefitDTO
{
    public long CampaignId { get; set; }
    public string CampaignCode { get; set; } = string.Empty;
    public RewardType Kind { get; set; }
    /// <summary>ส่วนลด (บาท)</summary>
    public decimal Amount { get; set; }
    /// <summary>คะแนนพิเศษ</summary>
    public int BonusPoints { get; set; }
    public List<int> LineNos { get; set; } = [];
    public string? CouponCode { get; set; }
    /// <summary>ชุดคูปองที่จะออกให้หลัง commit (IssueCoupon)</summary>
    public long? IssueCouponBatchId { get; set; }
}

/// <summary>คูปองที่ใช้ไม่ได้</summary>
public sealed class RejectedCouponDTO
{
    public string Code { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>ผลรายบรรทัด</summary>
public sealed class QuoteLineResultDTO
{
    public int LineNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? Category { get; set; }
    public decimal Qty { get; set; }
    /// <summary>unitPrice × qty − lineDiscount</summary>
    public decimal LineNet { get; set; }
    /// <summary>ส่วนลดโปรโมชันระดับรายการ</summary>
    public decimal PromotionDiscount { get; set; }
    /// <summary>ส่วนลดระดับบิลที่กระจายลงบรรทัดนี้ (ร้าน + โปรโมชันบิล + คูปอง)</summary>
    public decimal AllocatedBillDiscount { get; set; }
    /// <summary>ยอดที่จ่ายจริงของบรรทัด (ใช้คำนวณคะแนนและการคืน)</summary>
    public decimal LinePaid { get; set; }
    /// <summary>ได้คะแนน (ไม่ใช่ non-earning และเข้ากฎ)</summary>
    public bool Eligible { get; set; }
}

/// <summary>ผลคำนวณตะกร้า</summary>
public sealed class QuoteDTO
{
    public string QuoteId { get; set; } = string.Empty;
    public string MemberNo { get; set; } = string.Empty;
    public decimal Subtotal { get; set; }
    public decimal LineDiscounts { get; set; }
    public decimal PromotionDiscount { get; set; }
    public decimal CouponDiscount { get; set; }
    public decimal BillDiscount { get; set; }
    public int PointsRedeemed { get; set; }
    public decimal PointsTender { get; set; }
    public decimal NetPayable { get; set; }
    /// <summary>ฐานคะแนน (B ในสูตรคืนสินค้า)</summary>
    public decimal Eligible { get; set; }
    public int PointsToEarn { get; set; }
    public int BasePoints { get; set; }
    public List<BenefitDTO> Benefits { get; set; } = [];
    public List<RejectedCouponDTO> RejectedCoupons { get; set; } = [];
    public List<QuoteLineResultDTO> Lines { get; set; } = [];
    /// <summary>คะแนนที่ขอใช้ถูกลดลง (เกินยอดหรือเกิน %)</summary>
    public string? PointsAdjustedReason { get; set; }
    public string? EarnRuleCode { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>ลายนิ้วมือของผลคำนวณ (เทียบว่า quote เปลี่ยนหรือไม่)</summary>
    /// <returns>ข้อความสั้น</returns>
    public string Fingerprint() =>
        FormattableString.Invariant($"{NetPayable:F2}|{PromotionDiscount:F2}|{CouponDiscount:F2}|{PointsRedeemed}|{PointsToEarn}|{string.Join(',', Benefits.Select(b => b.CampaignId + ":" + b.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ":" + b.BonusPoints))}");
}

/// <summary>การจอง checkout</summary>
public sealed class ReservationDTO
{
    public string ReservationId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public QuoteDTO Quote { get; set; } = new();
}

/// <summary>แถว reservation (ตาราง <c>mbr_reservation</c>)</summary>
public sealed class ReservationEntity
{
    public string Id { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public long MemberId { get; set; }
    public ReservationStatus Status { get; set; }
    /// <summary>QuoteRequest JSON</summary>
    public string RequestJson { get; set; } = string.Empty;
    /// <summary>QuoteDTO JSON</summary>
    public string QuoteJson { get; set; } = string.Empty;
    public int PointsReserved { get; set; }
    /// <summary>คูปองที่จอง (คั่นด้วย ,)</summary>
    public string? CouponCodes { get; set; }
    /// <summary>ตัวนับแคมเปญที่จอง (JSON ของ CampaignCounterDelta[])</summary>
    public string? CountersJson { get; set; }
    public string? TransactionId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? CloseReason { get; set; }
    public long Version { get; set; }

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public ReservationEntity Clone() => (ReservationEntity)MemberwiseClone();
}

/// <summary>ยืนยันการขาย</summary>
public sealed class CommitRequest
{
    [Required, StringLength(100)] public string TransactionId { get; set; } = string.Empty;
    [StringLength(100)] public string? ReceiptNo { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    [Range(0, 100_000_000)] public decimal PaidAmount { get; set; }
}

/// <summary>ปล่อยการจอง</summary>
public sealed class ReleaseRequest
{
    [StringLength(500)] public string? Reason { get; set; }
}

/// <summary>ผล commit</summary>
public sealed class CheckoutResultDTO
{
    public string TransactionId { get; set; } = string.Empty;
    public string MemberNo { get; set; } = string.Empty;
    public int PointsEarned { get; set; }
    public int PointsRedeemed { get; set; }
    public PointBalanceDTO Balance { get; set; } = new();
    public List<string> CouponsRedeemed { get; set; } = [];
    public List<string> CouponsIssued { get; set; } = [];
    /// <summary>Tier ใหม่ (null = ไม่เปลี่ยน)</summary>
    public string? TierChanged { get; set; }
}

/// <summary>บันทึกการขายที่ไม่ผ่าน checkout (เช่น POS offline) — ได้คะแนนอย่างเดียว</summary>
public sealed class SalesTransactionRequest
{
    [Required, StringLength(20)] public string MemberNo { get; set; } = string.Empty;
    [StringLength(30)] public string? BranchCode { get; set; }
    public SalesChannel Channel { get; set; } = SalesChannel.Pos;
    [Required, MinLength(1), MaxLength(500)] public List<QuoteLineRequest> Lines { get; set; } = [];
    [Range(0, 100_000_000)] public decimal BillDiscount { get; set; }
    [Required, StringLength(100)] public string TransactionId { get; set; } = string.Empty;
    [StringLength(100)] public string? ReceiptNo { get; set; }
    public DateTimeOffset PaidAt { get; set; }
    [Range(0, 100_000_000)] public decimal PaidAmount { get; set; }
    public bool Offline { get; set; } = true;
}

/// <summary>บรรทัดของรายการขายที่เก็บไว้ (ใช้คำนวณการคืน)</summary>
public sealed class TransactionLineDTO
{
    public int LineNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? Category { get; set; }
    public decimal Qty { get; set; }
    public decimal LinePaid { get; set; }
    public bool Eligible { get; set; }
    public decimal ReturnedQty { get; set; }
}

/// <summary>lot ที่ถูกตัดตอนจ่ายด้วยคะแนน (คืนสินค้าแล้วสร้าง lot ใหม่ตามวันหมดอายุเดิม)</summary>
public sealed class ConsumedLotDTO
{
    public long LotId { get; set; }
    public int Points { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>รายการขาย (ตาราง <c>mbr_transaction</c>) — unique (source_system, transaction_id)</summary>
public sealed class SalesTransactionEntity
{
    public long Id { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string? ReceiptNo { get; set; }
    public long MemberId { get; set; }
    public string MemberNo { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public SalesChannel Channel { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public int PointsRedeemed { get; set; }
    public decimal PointsTender { get; set; }
    public decimal NetPaid { get; set; }
    /// <summary>B</summary>
    public decimal Eligible { get; set; }
    /// <summary>E</summary>
    public int PointsEarned { get; set; }
    /// <summary>R สะสม</summary>
    public decimal ReturnedEligible { get; set; }
    /// <summary>ยอดที่จ่ายจริงที่คืนแล้วสะสม (ใช้คืนคะแนนที่จ่าย)</summary>
    public decimal ReturnedPaid { get; set; }
    public int PointsReversed { get; set; }
    public int PointsRestored { get; set; }
    public TransactionStatus Status { get; set; }
    public bool Offline { get; set; }
    public string? ReservationId { get; set; }
    public string LinesJson { get; set; } = "[]";
    public string BenefitsJson { get; set; } = "[]";
    public string? CouponCodes { get; set; }
    public string ConsumedLotsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }

    /// <summary>สำเนา</summary>
    /// <returns>object ใหม่</returns>
    public SalesTransactionEntity Clone() => (SalesTransactionEntity)MemberwiseClone();
}

/// <summary>ผลบันทึกการขาย (POS)</summary>
public sealed class TransactionResultDTO
{
    public string TransactionId { get; set; } = string.Empty;
    public string MemberNo { get; set; } = string.Empty;
    public TransactionStatus Status { get; set; }
    public int PointsEarned { get; set; }
    public int PointsRedeemed { get; set; }
    public int PointsReversed { get; set; }
    public int PointsRestored { get; set; }
    public PointBalanceDTO? Balance { get; set; }
    /// <summary>true = เคยบันทึกแล้ว (ส่งซ้ำ)</summary>
    public bool AlreadyRecorded { get; set; }
}

/// <summary>แถวในประวัติซื้อ</summary>
public sealed class TransactionSummaryDTO
{
    public long Id { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string? ReceiptNo { get; set; }
    public long MemberId { get; set; }
    public string MemberNo { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public SalesChannel Channel { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public decimal NetPaid { get; set; }
    public decimal DiscountTotal { get; set; }
    public int PointsEarned { get; set; }
    public int PointsRedeemed { get; set; }
    public TransactionStatus Status { get; set; }
    public bool Offline { get; set; }
}

/// <summary>รายละเอียดรายการขาย</summary>
public sealed class TransactionDetailDTO
{
    public TransactionSummaryDTO Summary { get; set; } = new();
    public decimal Subtotal { get; set; }
    public decimal Eligible { get; set; }
    public decimal PointsTender { get; set; }
    public int PointsReversed { get; set; }
    public int PointsRestored { get; set; }
    public List<TransactionLineDTO> Lines { get; set; } = [];
    public List<BenefitDTO> Benefits { get; set; } = [];
    public List<string> Coupons { get; set; } = [];
    public List<SalesReturnDTO> Returns { get; set; } = [];
    public List<PointLedgerDTO> Ledger { get; set; } = [];
}

/// <summary>ค้นหารายการขาย</summary>
public sealed class TransactionSearchCriteria
{
    public long? MemberId { get; set; }
    [StringLength(100)] public string? TransactionId { get; set; }
    [StringLength(100)] public string? SourceSystem { get; set; }
    [StringLength(30)] public string? BranchCode { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public TransactionStatus? Status { get; set; }
    [Range(1, 100_000)] public int Page { get; set; } = 1;
    [Range(1, 500)] public int PageSize { get; set; } = 50;
}

/// <summary>บรรทัดที่คืน</summary>
public sealed class ReturnLineRequest
{
    [Range(1, 10_000)] public int LineNo { get; set; }
    [Range(0.001, 1_000_000)] public decimal Qty { get; set; }
}

/// <summary>คืนสินค้า</summary>
public sealed class ReturnRequest
{
    [Required, StringLength(100)] public string ReturnId { get; set; } = string.Empty;
    public DateTimeOffset? OccurredAt { get; set; }
    /// <summary>ว่าง = คืนทั้งบิล</summary>
    public List<ReturnLineRequest> Lines { get; set; } = [];
    [StringLength(500)] public string? Reason { get; set; }
}

/// <summary>ผลการคืน</summary>
public sealed class ReturnResultDTO
{
    public string ReturnId { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    /// <summary>Completed | PendingOriginal</summary>
    public string Status { get; set; } = "Completed";
    public int PointsReversed { get; set; }
    public int PointsRestored { get; set; }
    public List<string> CouponsRestored { get; set; } = [];
    public PointBalanceDTO? Balance { get; set; }
}

/// <summary>การคืนที่บันทึกแล้ว</summary>
public sealed class SalesReturnDTO
{
    public long Id { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string ReturnId { get; set; } = string.Empty;
    public decimal ReturnedEligible { get; set; }
    public decimal ReturnedPaid { get; set; }
    public int PointsReversed { get; set; }
    public int PointsRestored { get; set; }
    public string LinesJson { get; set; } = "[]";
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>การคืนที่มาก่อนรายการขาย (inbox) — job ประมวลผลซ้ำทุก 5 นาที</summary>
public sealed class PendingReturnEntity
{
    public long Id { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string ReturnId { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public bool Alerted { get; set; }
}
