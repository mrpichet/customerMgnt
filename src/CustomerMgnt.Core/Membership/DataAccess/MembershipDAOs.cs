using Common.Core.Domain;
using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Core.Membership.DataAccess;

// Data access ของระบบสมาชิก (ตาราง mbr_*) — มี 2 implementation: ADO (PostgreSQL / SQL Server + Dapper) และ InMemory (Development / test)
// กฎร่วม: method ที่แก้ข้อมูลที่มี version คืน false (หรือโยน MembershipConcurrencyException ใน unit of work) เมื่อ version ไม่ตรง

/// <summary>สมาชิก consent และประวัติ Tier</summary>
public interface IMemberDAO
{
    Task<MemberEntity?> GetAsync(long id, CancellationToken ct);
    Task<MemberEntity?> GetByNoAsync(string memberNo, CancellationToken ct);
    Task<MemberEntity?> GetBySubjectAsync(string subject, CancellationToken ct);
    /// <summary>สมาชิกทุกสถานะที่มี blind index ของเบอร์นี้</summary>
    Task<IReadOnlyList<MemberEntity>> FindByPhoneHashAsync(string phoneHash, CancellationToken ct);
    Task<IReadOnlyList<MemberEntity>> FindByEmailHashAsync(string emailHash, CancellationToken ct);
    Task<PagedResultDTO<MemberEntity>> SearchAsync(MemberSearchCriteria criteria, CancellationToken ct);
    /// <summary>เพิ่มสมาชิก + consent + outbox ใน transaction เดียว และออกเลขสมาชิก <c>M{yy}{id:00000}</c></summary>
    Task<MemberEntity> InsertAsync(MemberEntity member, MemberConsentDTO? consent, IReadOnlyList<OutboxMessageDTO> outbox, CancellationToken ct);
    /// <summary>แก้ไข (ตรวจ version แล้ว +1)</summary>
    Task<bool> UpdateAsync(MemberEntity member, CancellationToken ct);
    Task<IReadOnlyList<MemberEntity>> ListTierReviewDueAsync(DateTimeOffset now, int take, CancellationToken ct);
    Task<IReadOnlyList<MemberEntity>> ListWithoutCustomerAsync(int take, CancellationToken ct);
    Task<IReadOnlyList<MemberConsentDTO>> ListConsentsAsync(long memberId, CancellationToken ct);
    Task AddConsentAsync(MemberConsentDTO consent, CancellationToken ct);
    Task<IReadOnlyList<TierHistoryDTO>> ListTierHistoryAsync(long memberId, CancellationToken ct);
    /// <summary>ข้อเท็จจริงสำหรับ segment ทีละหน้า (เรียงตาม id)</summary>
    Task<IReadOnlyList<MemberFactsDTO>> ListFactsAsync(long afterId, int take, DateTimeOffset now, CancellationToken ct);
    Task<MemberStatsDTO> GetStatsAsync(DateTimeOffset since, CancellationToken ct);
}

/// <summary>สถิติสมาชิกสำหรับ dashboard</summary>
public sealed class MemberStatsDTO
{
    public int Total { get; set; }
    public int Active { get; set; }
    public int NewSince { get; set; }
    public Dictionary<string, int> ByTier { get; set; } = [];
}

/// <summary>Tier</summary>
public interface ITierDAO
{
    Task<IReadOnlyList<TierDTO>> ListAsync(CancellationToken ct);
    Task<TierDTO?> GetAsync(long id, CancellationToken ct);
    Task<long> InsertAsync(TierDTO tier, CancellationToken ct);
    Task<bool> UpdateAsync(TierDTO tier, CancellationToken ct);
}

/// <summary>คะแนน: ยอด lot ledger กฎการได้คะแนน คำขอปรับ</summary>
public interface IPointDAO
{
    /// <summary>ยอดสรุป (ยังไม่มีแถว = ยอด 0 version 0)</summary>
    Task<PointAccountDTO> GetAccountAsync(long memberId, CancellationToken ct);
    Task<IReadOnlyList<PointLotDTO>> ListLotsAsync(long memberId, bool activeOnly, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<ExpiringPointsDTO>> ListExpiringAsync(long memberId, DateTimeOffset now, DateTimeOffset until, CancellationToken ct);
    Task<PagedResultDTO<PointLedgerDTO>> SearchLedgerAsync(LedgerCriteria criteria, CancellationToken ct);
    Task<bool> LedgerKeyExistsAsync(string idempotencyKey, CancellationToken ct);
    /// <summary>lot ที่หมดอายุแล้วแต่ยังมีคงเหลือ</summary>
    Task<IReadOnlyList<PointLotDTO>> ListExpiredLotsAsync(DateTimeOffset now, int take, CancellationToken ct);
    /// <summary>คะแนนที่จะหมดอายุในช่วงเวลา (รวมต่อสมาชิก)</summary>
    Task<IReadOnlyList<(long MemberId, int Points, DateTimeOffset ExpiresAt)>> ListExpiringForNoticeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    /// <summary>ผลรวม |points| ที่ผู้ขอปรับแล้ว (Applied + Pending) ตั้งแต่เวลา</summary>
    Task<int> SumAdjustmentsAsync(string requestedBy, DateTimeOffset since, CancellationToken ct);
    Task<PointTotalsDTO> GetTotalsAsync(DateTimeOffset since, CancellationToken ct);

    Task<IReadOnlyList<EarnRuleDTO>> ListRulesAsync(CancellationToken ct);
    Task<EarnRuleDTO?> GetRuleAsync(long id, CancellationToken ct);
    Task<long> InsertRuleAsync(EarnRuleDTO rule, CancellationToken ct);
    Task<bool> UpdateRuleAsync(EarnRuleDTO rule, CancellationToken ct);

    Task<AdjustmentDTO?> GetAdjustmentAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<AdjustmentDTO>> ListAdjustmentsAsync(AdjustmentStatus? status, long? memberId, CancellationToken ct);
    Task<long> InsertAdjustmentAsync(AdjustmentDTO adjustment, CancellationToken ct);
    Task UpdateAdjustmentAsync(AdjustmentDTO adjustment, CancellationToken ct);
}

/// <summary>ยอดคะแนนรวมสำหรับ dashboard</summary>
public sealed class PointTotalsDTO
{
    public long Outstanding { get; set; }
    public long EarnedSince { get; set; }
    public long RedeemedSince { get; set; }
}

/// <summary>แคมเปญและตัวนับสิทธิ/งบ</summary>
public interface ICampaignDAO
{
    Task<PagedResultDTO<CampaignDTO>> SearchAsync(CampaignSearchCriteria criteria, CancellationToken ct);
    Task<CampaignDTO?> GetAsync(long id, CancellationToken ct);
    Task<CampaignDTO?> GetByCodeAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<CampaignDTO>> ListByStatusAsync(IReadOnlyCollection<CampaignStatus> statuses, CancellationToken ct);
    Task<IReadOnlyList<CampaignDTO>> ListByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct);
    Task<long> InsertAsync(CampaignDTO campaign, CancellationToken ct);
    Task<bool> UpdateAsync(CampaignDTO campaign, CancellationToken ct);
    /// <summary>ตัวนับ: scope <c>total</c>, <c>m:{memberId}</c>, <c>d:{memberId}:{yyyy-MM-dd}</c></summary>
    Task<IReadOnlyDictionary<(long CampaignId, string Scope), CampaignUsageDTO>> GetCountersAsync(IReadOnlyCollection<long> campaignIds, IReadOnlyCollection<string> scopes, CancellationToken ct);
}

/// <summary>คูปอง</summary>
public interface ICouponDAO
{
    Task<IReadOnlyList<CouponBatchDTO>> ListBatchesAsync(long? campaignId, CancellationToken ct);
    Task<CouponBatchDTO?> GetBatchAsync(long id, CancellationToken ct);
    Task<long> InsertBatchAsync(CouponBatchDTO batch, CancellationToken ct);
    Task UpdateBatchAsync(CouponBatchDTO batch, CancellationToken ct);
    Task<CouponDTO?> GetByCodeAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<CouponDTO>> GetByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct);
    Task<PagedResultDTO<CouponDTO>> SearchAsync(CouponSearchCriteria criteria, CancellationToken ct);
    /// <summary>คูปองของสมาชิก (ทุกสถานะ หรือเฉพาะสถานะ)</summary>
    Task<IReadOnlyList<CouponDTO>> ListByMemberAsync(long memberId, CouponStatus? status, CancellationToken ct);
    /// <summary>เพิ่มคูปองหลายใบ (รหัสซ้ำ = ข้าม คืนจำนวนที่เพิ่มได้)</summary>
    Task<int> InsertManyAsync(IReadOnlyList<CouponDTO> coupons, CancellationToken ct);
    /// <summary>หยิบคูปองว่างของชุด (ไม่มีเจ้าของ) เพื่อออกให้สมาชิก</summary>
    Task<IReadOnlyList<CouponDTO>> TakeUnassignedAsync(long batchId, int take, CancellationToken ct);
    Task<IReadOnlyList<CouponEventDTO>> ListEventsAsync(string code, CancellationToken ct);
    /// <summary>เปลี่ยนคูปอง Available/Reversed ที่หมดอายุเป็น Expired คืนจำนวน</summary>
    Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>Segment</summary>
public interface ISegmentDAO
{
    Task<IReadOnlyList<SegmentDTO>> ListAsync(CancellationToken ct);
    Task<SegmentDTO?> GetAsync(long id, CancellationToken ct);
    Task<long> InsertAsync(SegmentDTO segment, CancellationToken ct);
    Task<bool> UpdateAsync(SegmentDTO segment, CancellationToken ct);
    /// <summary>แทนที่สมาชิกทั้งหมดของ segment</summary>
    Task ReplaceMembersAsync(long segmentId, IReadOnlyList<long> memberIds, DateTimeOffset refreshedAt, CancellationToken ct);
    Task<IReadOnlySet<long>> ListSegmentIdsOfMemberAsync(long memberId, CancellationToken ct);
    Task<PagedResultDTO<long>> PageMemberIdsAsync(long segmentId, int page, int pageSize, CancellationToken ct);
}

/// <summary>Checkout: reservation รายการขาย การคืน</summary>
public interface ICheckoutDAO
{
    Task<ReservationEntity?> GetReservationAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<ReservationEntity>> ListExpiredReservationsAsync(DateTimeOffset now, int take, CancellationToken ct);
    Task<SalesTransactionEntity?> GetTransactionAsync(string sourceSystem, string transactionId, CancellationToken ct);
    Task<SalesTransactionEntity?> GetTransactionByIdAsync(long id, CancellationToken ct);
    Task<PagedResultDTO<SalesTransactionEntity>> SearchTransactionsAsync(TransactionSearchCriteria criteria, CancellationToken ct);
    Task<IReadOnlyList<SalesReturnDTO>> ListReturnsAsync(string sourceSystem, string transactionId, CancellationToken ct);
    /// <summary>ยอด eligible สุทธิ (หักคืน) ของบิลที่ไม่ถูกยกเลิก ตั้งแต่เวลา — ใช้คำนวณ Tier</summary>
    Task<decimal> GetQualifyingSpendAsync(long memberId, DateTimeOffset since, CancellationToken ct);
    Task<SalesTotalsDTO> GetTotalsAsync(DateTimeOffset since, CancellationToken ct);
    Task<long> InsertPendingReturnAsync(PendingReturnEntity pending, CancellationToken ct);
    Task<IReadOnlyList<PendingReturnEntity>> ListPendingReturnsAsync(int take, CancellationToken ct);
    Task UpdatePendingReturnAsync(PendingReturnEntity pending, CancellationToken ct);
}

/// <summary>ยอดขายรวมสำหรับ dashboard</summary>
public sealed class SalesTotalsDTO
{
    public decimal Sales { get; set; }
    public int Transactions { get; set; }
}

/// <summary>คำขออนุมัติ</summary>
public interface IApprovalDAO
{
    Task<long> InsertAsync(ApprovalRequestDTO request, CancellationToken ct);
    Task<ApprovalRequestDTO?> GetAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<ApprovalRequestDTO>> SearchAsync(ApprovalSearchCriteria criteria, CancellationToken ct);
    Task<bool> UpdateAsync(ApprovalRequestDTO request, CancellationToken ct);
}

/// <summary>Audit log (hash chain)</summary>
public interface IMembershipAuditDAO
{
    /// <summary>เพิ่มรายการ (คำนวณ prevHash/hash ภายใน — กันเขียนพร้อมกันด้วย lock)</summary>
    Task AppendAsync(IReadOnlyList<MembershipAuditDTO> entries, CancellationToken ct);
    Task<PagedResultDTO<MembershipAuditDTO>> SearchAsync(AuditSearchCriteria criteria, CancellationToken ct);
}

/// <summary>Outbox</summary>
public interface IOutboxDAO
{
    Task<IReadOnlyList<OutboxMessageDTO>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct);
    Task MarkSentAsync(long id, DateTimeOffset sentAt, CancellationToken ct);
    Task MarkFailedAsync(long id, int attempts, DateTimeOffset nextAttemptAt, string error, bool deadLetter, CancellationToken ct);
    Task EnqueueAsync(IReadOnlyList<OutboxMessageDTO> messages, CancellationToken ct);
    Task<(int Backlog, int DeadLetters)> GetStatsAsync(CancellationToken ct);
}

/// <summary>Idempotency-Key ของ POS / integration</summary>
public interface IIdempotencyDAO
{
    /// <summary>จองคีย์ (StatusCode = 0) — คีย์มีอยู่แล้วคืนแถวเดิมโดยไม่เพิ่ม</summary>
    Task<IdempotencyRecordDTO?> TryBeginAsync(IdempotencyRecordDTO record, CancellationToken ct);
    Task CompleteAsync(string sourceSystem, string key, int statusCode, string? responseJson, CancellationToken ct);
    /// <summary>ลบคีย์ (คำขอล้มด้วย 5xx ให้ลองใหม่ด้วยคีย์เดิมได้)</summary>
    Task DeleteAsync(string sourceSystem, string key, CancellationToken ct);
    Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>ค่าตั้ง: กฎธุรกิจ, consent, ระบบภายนอก</summary>
public interface IMembershipSettingsDAO
{
    /// <summary>กฎธุรกิจ (ยังไม่มีแถว = ค่าเริ่มต้น version 0)</summary>
    Task<MembershipRulesDTO> GetRulesAsync(CancellationToken ct);
    Task<bool> SaveRulesAsync(MembershipRulesDTO rules, CancellationToken ct);
    Task<IReadOnlyList<ConsentVersionDTO>> ListConsentVersionsAsync(CancellationToken ct);
    Task<long> InsertConsentVersionAsync(ConsentVersionDTO version, CancellationToken ct);
    Task UpdateConsentVersionAsync(ConsentVersionDTO version, CancellationToken ct);
    Task<IReadOnlyList<IntegrationClientDTO>> ListIntegrationClientsAsync(CancellationToken ct);
    Task UpsertIntegrationClientAsync(IntegrationClientDTO client, CancellationToken ct);
}

/// <summary>OTP และคำขอ PDPA</summary>
public interface IMemberPrivacyDAO
{
    Task InsertOtpAsync(OtpChallengeEntity challenge, CancellationToken ct);
    Task<OtpChallengeEntity?> GetOtpAsync(string id, CancellationToken ct);
    Task UpdateOtpAsync(OtpChallengeEntity challenge, CancellationToken ct);
    Task<int> CountOtpSinceAsync(string phoneHash, DateTimeOffset since, CancellationToken ct);
    Task<long> InsertPrivacyRequestAsync(PrivacyRequestDTO request, CancellationToken ct);
    Task<PrivacyRequestDTO?> GetPrivacyRequestAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<PrivacyRequestDTO>> ListPrivacyRequestsAsync(long? memberId, PrivacyRequestStatus? status, CancellationToken ct);
    Task UpdatePrivacyRequestAsync(PrivacyRequestDTO request, CancellationToken ct);
}

/// <summary>งานเบื้องหลัง: lease lock, export, ส่งข้อความ</summary>
public interface IMembershipJobDAO
{
    /// <summary>ขอ lease ของงาน (ทำได้ instance เดียวต่อรอบ) — คืน true ถ้าได้</summary>
    Task<bool> TryAcquireLeaseAsync(string job, string holder, DateTimeOffset now, TimeSpan duration, CancellationToken ct);
    Task<long> InsertExportAsync(ExportJobDTO job, CancellationToken ct);
    Task<ExportJobDTO?> GetExportAsync(long id, bool withContent, CancellationToken ct);
    Task<IReadOnlyList<ExportJobDTO>> ListExportsAsync(string? requestedBy, JobStatus? status, CancellationToken ct);
    Task UpdateExportAsync(ExportJobDTO job, CancellationToken ct);
    Task<long> InsertMessageJobAsync(MessageJobDTO job, CancellationToken ct);
    Task<IReadOnlyList<MessageJobDTO>> ListMessageJobsAsync(JobStatus? status, CancellationToken ct);
    Task UpdateMessageJobAsync(MessageJobDTO job, CancellationToken ct);
}

/// <summary>บันทึกการเปลี่ยนแปลงหลายตารางใน transaction เดียว (คะแนน คูปอง ตัวนับ การขาย outbox audit)</summary>
public interface IMembershipUnitOfWork
{
    /// <summary>บันทึกทั้งหมดหรือไม่บันทึกเลย</summary>
    /// <param name="changes">การเปลี่ยนแปลง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Task</returns>
    /// <exception cref="Engine.MembershipConcurrencyException">version ไม่ตรง / idempotency key ซ้ำ / เกินจำกัดสิทธิ</exception>
    Task CommitAsync(MembershipChangeSet changes, CancellationToken ct);
}

/// <summary>เปลี่ยนยอดคะแนนของสมาชิก (ExpectedVersion 0 = เพิ่มแถวใหม่)</summary>
public sealed record AccountChange(PointAccountDTO NewState, long ExpectedVersion);

/// <summary>เปลี่ยนคงเหลือของ lot (ตรวจคงเหลือเดิม)</summary>
public sealed record LotChange(long LotId, int ExpectedRemaining, int NewRemaining);

/// <summary>เปลี่ยน/เพิ่มคูปอง (ExpectedVersion 0 = เพิ่ม)</summary>
public sealed record CouponChange(CouponDTO NewState, long ExpectedVersion);

/// <summary>ปรับตัวนับของแคมเปญ — ตรวจ (uses + reserved) ≤ Limit และ (budgetUsed + budgetReserved) ≤ BudgetLimit เมื่อค่าเพิ่ม</summary>
public sealed class CampaignCounterDelta
{
    public long CampaignId { get; set; }
    public string Scope { get; set; } = "total";
    public int UsesDelta { get; set; }
    public int ReservedDelta { get; set; }
    public decimal BudgetDelta { get; set; }
    public decimal BudgetReservedDelta { get; set; }
    public int? Limit { get; set; }
    public decimal? BudgetLimit { get; set; }
}

/// <summary>ชุดการเปลี่ยนแปลงที่บันทึกพร้อมกัน</summary>
public sealed class MembershipChangeSet
{
    public List<AccountChange> Accounts { get; } = [];
    public List<PointLotDTO> NewLots { get; } = [];
    public List<LotChange> LotChanges { get; } = [];
    public List<PointLedgerDTO> Ledger { get; } = [];
    public List<LotAllocationDTO> Allocations { get; } = [];
    public List<CouponChange> Coupons { get; } = [];
    public List<CouponEventDTO> CouponEvents { get; } = [];
    public List<CampaignCounterDelta> Counters { get; } = [];
    public ReservationEntity? NewReservation { get; set; }
    /// <summary>แก้ reservation (ตรวจ version)</summary>
    public ReservationEntity? ReservationUpdate { get; set; }
    public SalesTransactionEntity? NewTransaction { get; set; }
    /// <summary>แก้รายการขาย (ตรวจ version)</summary>
    public SalesTransactionEntity? TransactionUpdate { get; set; }
    public List<SalesReturnDTO> Returns { get; } = [];
    /// <summary>แก้สมาชิก (ตรวจ version)</summary>
    public List<MemberEntity> MemberUpdates { get; } = [];
    public List<TierHistoryDTO> TierHistory { get; } = [];
    public List<OutboxMessageDTO> Outbox { get; } = [];
    public List<MembershipAuditDTO> Audit { get; } = [];
    public AdjustmentDTO? AdjustmentUpdate { get; set; }

    /// <summary>ไม่มีอะไรต้องบันทึก</summary>
    public bool IsEmpty => Accounts.Count == 0 && NewLots.Count == 0 && LotChanges.Count == 0 && Ledger.Count == 0 && Coupons.Count == 0
        && Counters.Count == 0 && NewReservation is null && ReservationUpdate is null && NewTransaction is null && TransactionUpdate is null
        && Returns.Count == 0 && MemberUpdates.Count == 0 && TierHistory.Count == 0 && Outbox.Count == 0 && Audit.Count == 0 && AdjustmentUpdate is null;
}
