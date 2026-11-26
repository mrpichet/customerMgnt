using Common.Core.Domain;
using CustomerMgnt.Core.Domain;

namespace CustomerMgnt.Core.DataAccess;

// Repository interface (ลงท้าย DAO) — Core กำหนดสัญญา, CustomerMgnt.Data implement ด้วย Dapper (*ADO)
// ทุก method เป็น async + CancellationToken; ADO ต้องใช้ parameter เสมอ (ห้ามต่อ SQL จาก input)
// method ที่แก้ไขข้อมูลพร้อม version คืน false เมื่อ version ไม่ตรง (optimistic concurrency) ให้ service แปลงเป็น 409

/// <summary>ลูกค้า (บุคคล/นิติบุคคล)</summary>
public interface ICustomerDAO
{
    /// <summary>เพิ่มลูกค้า</summary>
    /// <param name="customer">ข้อมูล (Id/Version ถูกตั้งค่าเมื่อสำเร็จ)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัสใหม่</returns>
    Task<long> InsertAsync(CustomerDTO customer, CancellationToken ct);

    /// <summary>แก้ไขแบบตรวจ version</summary>
    /// <param name="customer">ข้อมูลใหม่ (Version = ค่าที่อ่านมา)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>true เมื่อสำเร็จ, false เมื่อ version ไม่ตรง</returns>
    Task<bool> UpdateAsync(CustomerDTO customer, CancellationToken ct);

    /// <summary>อ่านตามรหัส</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้า หรือ null</returns>
    Task<CustomerDTO?> GetAsync(long id, CancellationToken ct);

    /// <summary>อ่านตามรหัสลูกค้า (Code)</summary>
    /// <param name="code">รหัสลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้า หรือ null</returns>
    Task<CustomerDTO?> GetByCodeAsync(string code, CancellationToken ct);

    /// <summary>หาลูกค้าจาก blind index ของเลขผู้เสียภาษี</summary>
    /// <param name="taxIdHash">HMAC ของเลขผู้เสียภาษี</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้า หรือ null</returns>
    Task<CustomerDTO?> FindByTaxIdHashAsync(string taxIdHash, CancellationToken ct);

    /// <summary>ค้นหาแบบแบ่งหน้า</summary>
    /// <param name="criteria">เงื่อนไข</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลลัพธ์</returns>
    Task<PagedResultDTO<CustomerDTO>> SearchAsync(CustomerSearchCriteria criteria, CancellationToken ct);

    /// <summary>อ่านหลายรายการ</summary>
    /// <param name="ids">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้าที่พบ</returns>
    Task<IReadOnlyList<CustomerDTO>> GetManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct);

    /// <summary>อ่านลูกค้าทั้งหมด (สำหรับงาน ML แบบ batch) แบบ keyset paging เป็นชุด</summary>
    /// <param name="afterId">เริ่มหลังรหัสนี้</param>
    /// <param name="take">จำนวนต่อชุด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ชุดข้อมูล (ว่าง = หมดแล้ว)</returns>
    Task<IReadOnlyList<CustomerDTO>> ListBatchAsync(long afterId, int take, CancellationToken ct);

    /// <summary>ตั้งกลุ่มลูกค้าหลายรายในครั้งเดียว (bulk)</summary>
    /// <param name="segments">รหัสลูกค้า → ชื่อกลุ่ม</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Task</returns>
    Task UpdateSegmentsAsync(IReadOnlyDictionary<long, string> segments, CancellationToken ct);
}

/// <summary>ความสัมพันธ์บุคคล↔นิติบุคคล และตัวตนในช่องทาง</summary>
public interface IRelationshipDAO
{
    /// <summary>เพิ่มความสัมพันธ์</summary>
    /// <param name="relationship">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส</returns>
    Task<long> InsertAsync(PartyRelationshipDTO relationship, CancellationToken ct);

    /// <summary>ปิดความสัมพันธ์ (ตั้ง ValidTo)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="validTo">วันสิ้นสุด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>true ถ้าพบ</returns>
    Task<bool> EndAsync(long id, DateOnly validTo, CancellationToken ct);

    /// <summary>อ่านความสัมพันธ์</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อมูล หรือ null</returns>
    Task<PartyRelationshipDTO?> GetAsync(long id, CancellationToken ct);

    /// <summary>ความสัมพันธ์ทั้งหมดของลูกค้า (ทั้งฝั่งบุคคลและฝั่งนิติบุคคล) พร้อมชื่อ</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<PartyRelationshipDTO>> ListByCustomerAsync(long customerId, CancellationToken ct);

    /// <summary>เพิ่มตัวตนในช่องทาง</summary>
    /// <param name="identity">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส</returns>
    Task<long> InsertIdentityAsync(ChannelIdentityDTO identity, CancellationToken ct);

    /// <summary>หาลูกค้าจากตัวตนในช่องทาง</summary>
    /// <param name="channel">ช่องทาง</param>
    /// <param name="externalId">รหัสในช่องทาง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ตัวตน หรือ null</returns>
    Task<ChannelIdentityDTO?> FindIdentityAsync(ChannelKind channel, string externalId, CancellationToken ct);

    /// <summary>ตัวตนทั้งหมดของลูกค้า</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<ChannelIdentityDTO>> ListIdentitiesAsync(long customerId, CancellationToken ct);
}

/// <summary>Omnichannel inbox</summary>
public interface IConversationDAO
{
    /// <summary>หา conversation ที่ยังไม่ปิดของผู้ส่งในช่องทาง</summary>
    /// <param name="channel">ช่องทาง</param>
    /// <param name="externalUserId">ผู้ส่ง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>conversation หรือ null</returns>
    Task<ConversationDTO?> FindOpenAsync(ChannelKind channel, string externalUserId, CancellationToken ct);

    /// <summary>เพิ่ม conversation</summary>
    /// <param name="conversation">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส</returns>
    Task<long> InsertAsync(ConversationDTO conversation, CancellationToken ct);

    /// <summary>บันทึกสถานะ conversation</summary>
    /// <param name="conversation">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Task</returns>
    Task UpdateAsync(ConversationDTO conversation, CancellationToken ct);

    /// <summary>อ่าน conversation</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อมูล หรือ null</returns>
    Task<ConversationDTO?> GetAsync(long id, CancellationToken ct);

    /// <summary>รายการ inbox</summary>
    /// <param name="status">สถานะ (null = ทั้งหมด)</param>
    /// <param name="customerId">ลูกค้า (null = ทั้งหมด)</param>
    /// <param name="needsAttentionOnly">เฉพาะที่ลูกค้าไม่พอใจ</param>
    /// <param name="take">จำนวนสูงสุด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ เรียงตามข้อความล่าสุด</returns>
    Task<IReadOnlyList<ConversationDTO>> ListAsync(ConversationStatus? status, long? customerId, bool needsAttentionOnly, int take, CancellationToken ct);

    /// <summary>เพิ่มข้อความ (คืน null เมื่อ ExternalMessageId ซ้ำ = webhook ส่งซ้ำ)</summary>
    /// <param name="message">ข้อความ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส หรือ null เมื่อซ้ำ</returns>
    Task<long?> InsertMessageAsync(ChatMessageDTO message, CancellationToken ct);

    /// <summary>ข้อความของ conversation</summary>
    /// <param name="conversationId">conversation</param>
    /// <param name="take">จำนวนล่าสุด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อความเรียงตามเวลา</returns>
    Task<IReadOnlyList<ChatMessageDTO>> ListMessagesAsync(long conversationId, int take, CancellationToken ct);

    /// <summary>คะแนนอารมณ์ขาเข้าของลูกค้าในช่วงเวลา (ใช้ทำนาย churn)</summary>
    /// <param name="customerIds">ลูกค้า</param>
    /// <param name="since">ตั้งแต่</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้า → (ค่าเฉลี่ย, จำนวนข้อความ)</returns>
    Task<IReadOnlyDictionary<long, (double Average, int Count)>> SentimentByCustomerAsync(IReadOnlyCollection<long> customerIds, DateTimeOffset since, CancellationToken ct);
}

/// <summary>กิจกรรมและการแจ้งเตือน</summary>
public interface IActivityDAO
{
    /// <summary>เพิ่มกิจกรรม</summary>
    /// <param name="activity">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส</returns>
    Task<long> InsertAsync(ActivityDTO activity, CancellationToken ct);

    /// <summary>บันทึกกิจกรรม</summary>
    /// <param name="activity">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Task</returns>
    Task UpdateAsync(ActivityDTO activity, CancellationToken ct);

    /// <summary>อ่านกิจกรรม</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อมูล หรือ null</returns>
    Task<ActivityDTO?> GetAsync(long id, CancellationToken ct);

    /// <summary>กิจกรรมของลูกค้า</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="openOnly">เฉพาะที่ยังไม่ทำ</param>
    /// <param name="take">จำนวนสูงสุด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<ActivityDTO>> ListByCustomerAsync(long customerId, bool openOnly, int take, CancellationToken ct);

    /// <summary>กิจกรรมที่ยังไม่ทำของผู้รับผิดชอบ</summary>
    /// <param name="ownerSubject">ผู้รับผิดชอบ</param>
    /// <param name="until">กำหนดไม่เกินเวลานี้</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการเรียงตามกำหนด</returns>
    Task<IReadOnlyList<ActivityDTO>> ListOpenByOwnerAsync(string ownerSubject, DateTimeOffset until, CancellationToken ct);

    /// <summary>กิจกรรมที่ถึงเวลาแจ้งเตือนแต่ยังไม่แจ้ง</summary>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <param name="take">จำนวนสูงสุด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<ActivityDTO>> ListDueRemindersAsync(DateTimeOffset now, int take, CancellationToken ct);

    /// <summary>ทำเครื่องหมายว่าแจ้งเตือนแล้ว (เฉพาะที่ยังไม่แจ้ง → กันหลาย instance ส่งซ้ำ)</summary>
    /// <param name="id">กิจกรรม</param>
    /// <param name="sentAt">เวลาแจ้ง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>true ถ้า instance นี้เป็นผู้ได้สิทธิ์ส่ง</returns>
    Task<bool> TryMarkReminderSentAsync(long id, DateTimeOffset sentAt, CancellationToken ct);

    /// <summary>วันที่ติดต่อล่าสุดและจำนวนกิจกรรมของลูกค้า (feature ของ ML)</summary>
    /// <param name="customerIds">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้า → (ติดต่อล่าสุด, จำนวนที่ทำแล้วใน 90 วัน)</returns>
    Task<IReadOnlyDictionary<long, (DateTimeOffset? LastContactAt, int Done90Days)>> ContactStatsAsync(IReadOnlyCollection<long> customerIds, CancellationToken ct);
}

/// <summary>คำขอ/TOR</summary>
public interface ICustomerRequestDAO
{
    /// <summary>เพิ่มคำขอพร้อมรายการ TOR (transaction เดียว)</summary>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส</returns>
    Task<long> InsertAsync(CustomerRequestDTO request, CancellationToken ct);

    /// <summary>บันทึกสถานะ/ข้อมูลส่งต่อแบบตรวจ version (รวมถึง RequirementKey ของแต่ละข้อ)</summary>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>false เมื่อ version ไม่ตรง</returns>
    Task<bool> UpdateAsync(CustomerRequestDTO request, CancellationToken ct);

    /// <summary>อ่านคำขอพร้อมรายการ TOR</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อมูล หรือ null</returns>
    Task<CustomerRequestDTO?> GetAsync(long id, CancellationToken ct);

    /// <summary>รายการคำขอ</summary>
    /// <param name="customerId">ลูกค้า (null = ทั้งหมด)</param>
    /// <param name="status">สถานะ (null = ทั้งหมด)</param>
    /// <param name="take">จำนวน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ (ไม่รวม Items)</returns>
    Task<IReadOnlyList<CustomerRequestDTO>> ListAsync(long? customerId, RequestStatus? status, int take, CancellationToken ct);

    /// <summary>เลขลำดับถัดไปของปี (ใช้สร้าง RequestNo)</summary>
    /// <param name="year">ปี</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลำดับ</returns>
    Task<int> NextSequenceAsync(int year, CancellationToken ct);
}

/// <summary>Pipeline การขาย</summary>
public interface IOpportunityDAO
{
    /// <summary>เพิ่ม</summary>
    /// <param name="opportunity">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส</returns>
    Task<long> InsertAsync(OpportunityDTO opportunity, CancellationToken ct);

    /// <summary>บันทึกแบบตรวจ version พร้อมประวัติ stage (ถ้ามี)</summary>
    /// <param name="opportunity">ข้อมูล</param>
    /// <param name="history">ประวัติ (null = stage ไม่เปลี่ยน)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>false เมื่อ version ไม่ตรง</returns>
    Task<bool> UpdateAsync(OpportunityDTO opportunity, StageHistoryDTO? history, CancellationToken ct);

    /// <summary>อ่าน</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อมูล หรือ null</returns>
    Task<OpportunityDTO?> GetAsync(long id, CancellationToken ct);

    /// <summary>รายการ</summary>
    /// <param name="customerId">ลูกค้า (null = ทั้งหมด)</param>
    /// <param name="openOnly">เฉพาะที่ยังไม่ปิด</param>
    /// <param name="ownerSubject">ผู้รับผิดชอบ (null = ทั้งหมด)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<OpportunityDTO>> ListAsync(long? customerId, bool openOnly, string? ownerSubject, CancellationToken ct);

    /// <summary>โอกาสที่ปิดแล้ว (Won/Lost) ตั้งแต่วันที่ — ข้อมูลฝึกโมเดล</summary>
    /// <param name="since">ตั้งแต่</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<OpportunityDTO>> ListClosedAsync(DateTimeOffset since, CancellationToken ct);

    /// <summary>บันทึกคะแนน ML หลายรายการ (bulk)</summary>
    /// <param name="scores">รหัส → คะแนน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Task</returns>
    Task UpdateScoresAsync(IReadOnlyDictionary<long, double> scores, CancellationToken ct);
}

/// <summary>นโยบาย SLA และระดับ severity</summary>
public interface ISlaDAO
{
    /// <summary>เพิ่มนโยบายพร้อมระดับ (transaction เดียว)</summary>
    /// <param name="policy">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส</returns>
    Task<long> InsertAsync(SlaPolicyDTO policy, CancellationToken ct);

    /// <summary>แก้ไขนโยบายและแทนที่ระดับทั้งหมดแบบตรวจ version</summary>
    /// <param name="policy">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>false เมื่อ version ไม่ตรง</returns>
    Task<bool> UpdateAsync(SlaPolicyDTO policy, CancellationToken ct);

    /// <summary>อ่านนโยบายพร้อมระดับ</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อมูล หรือ null</returns>
    Task<SlaPolicyDTO?> GetAsync(long id, CancellationToken ct);

    /// <summary>นโยบายของลูกค้า (รวมนโยบายมาตรฐานเมื่อ customerId = null)</summary>
    /// <param name="customerId">ลูกค้า (null = นโยบายมาตรฐาน)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการพร้อมระดับ</returns>
    Task<IReadOnlyList<SlaPolicyDTO>> ListByCustomerAsync(long? customerId, CancellationToken ct);
}

/// <summary>ลิงก์ไประบบอื่น</summary>
public interface IExternalLinkDAO
{
    /// <summary>เพิ่มลิงก์ (ซ้ำ = คืนรหัสเดิม)</summary>
    /// <param name="link">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส</returns>
    Task<long> UpsertAsync(ExternalLinkDTO link, CancellationToken ct);

    /// <summary>ลิงก์ของลูกค้า</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="system">ระบบ (null = ทั้งหมด)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<ExternalLinkDTO>> ListAsync(long customerId, ExternalSystem? system, CancellationToken ct);

    /// <summary>ลิงก์ที่เกิดจากคำขอ</summary>
    /// <param name="requestId">คำขอ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<ExternalLinkDTO>> ListByRequestAsync(long requestId, CancellationToken ct);
}

/// <summary>ข้อมูลพฤติกรรม, สัญญา และแคตตาล็อก (วัตถุดิบของ Analytics)</summary>
public interface IEngagementDAO
{
    /// <summary>เพิ่มเหตุการณ์จำนวนมาก (bulk insert)</summary>
    /// <param name="events">เหตุการณ์</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>จำนวนที่บันทึก</returns>
    Task<int> InsertManyAsync(IReadOnlyList<EngagementEventDTO> events, CancellationToken ct);

    /// <summary>เหตุการณ์ของลูกค้าหลายราย ตั้งแต่วันที่ (จำกัดชนิดได้)</summary>
    /// <param name="customerIds">ลูกค้า</param>
    /// <param name="since">ตั้งแต่</param>
    /// <param name="kinds">ชนิด (null = ทั้งหมด)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<EngagementEventDTO>> ListAsync(IReadOnlyCollection<long> customerIds, DateTimeOffset since, IReadOnlyCollection<EngagementKind>? kinds, CancellationToken ct);

    /// <summary>ยอดซื้อรวมรายเดือนทั้งบริษัท (หรือของลูกค้ารายเดียว) — ใช้พยากรณ์ยอดขาย/งบประมาณ</summary>
    /// <param name="customerId">ลูกค้า (null = ทั้งหมด)</param>
    /// <param name="since">ตั้งแต่</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>เดือน (วันที่ 1) → ยอด</returns>
    Task<IReadOnlyDictionary<DateOnly, decimal>> MonthlyPurchasesAsync(long? customerId, DateTimeOffset since, CancellationToken ct);

    /// <summary>เพิ่มสัญญา</summary>
    /// <param name="contract">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัส</returns>
    Task<long> InsertContractAsync(ContractDTO contract, CancellationToken ct);

    /// <summary>สัญญาที่สิ้นสุดในช่วง</summary>
    /// <param name="from">ตั้งแต่</param>
    /// <param name="to">ถึง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<ContractDTO>> ListContractsEndingAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>สัญญาทั้งหมดที่จบแล้ว (Renewed/Expired/Cancelled) — ข้อมูลฝึกโมเดลต่อสัญญา</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<ContractDTO>> ListClosedContractsAsync(CancellationToken ct);

    /// <summary>สัญญาของลูกค้า</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<ContractDTO>> ListContractsAsync(long customerId, CancellationToken ct);

    /// <summary>ลูกค้าที่มีสัญญา Active และยังไม่สิ้นสุด ณ วันที่กำหนด ของผลิตภัณฑ์ (หนึ่งแถวต่อสัญญา)</summary>
    /// <param name="productCode">รหัสผลิตภัณฑ์ (ตัวพิมพ์ใหญ่)</param>
    /// <param name="today">วันที่ตรวจ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ เรียงตามรหัสลูกค้า</returns>
    Task<IReadOnlyList<ProductCustomerDTO>> ListActiveCustomersByProductAsync(string productCode, DateOnly today, CancellationToken ct);

    /// <summary>แคตตาล็อกที่ใช้งานอยู่</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    Task<IReadOnlyList<CatalogItemDTO>> ListCatalogAsync(CancellationToken ct);

    /// <summary>เพิ่ม/แก้ไขรายการแคตตาล็อก</summary>
    /// <param name="item">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Task</returns>
    Task UpsertCatalogAsync(CatalogItemDTO item, CancellationToken ct);
}

/// <summary>โปรไฟล์ผู้ใช้ภายใน (map subject ของ AuthServer → ผู้ใช้ใน MessageHub)</summary>
public interface IUserProfileDAO
{
    /// <summary>อ่านโปรไฟล์</summary>
    /// <param name="subject">subject ใน AuthServer</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>โปรไฟล์ หรือ null</returns>
    Task<UserProfileDTO?> GetAsync(string subject, CancellationToken ct);

    /// <summary>เพิ่ม/แก้ไขโปรไฟล์</summary>
    /// <param name="profile">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Task</returns>
    Task UpsertAsync(UserProfileDTO profile, CancellationToken ct);
}
