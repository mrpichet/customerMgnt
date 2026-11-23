namespace CustomerMgnt.Core.Domain;

/// <summary>
/// โอกาสการขาย (Lead/Opportunity) ใน Pipeline ตั้งแต่เริ่มทักจนปิดการขาย
/// ลูกค้าที่ยังเป็น Prospect ก็มีโอกาสการขายได้ (lead = opportunity ที่ stage = New/Contacted)
/// </summary>
public sealed class OpportunityDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>ชื่อโอกาส เช่น "ระบบ ERP เฟส 2"</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>ขั้นปัจจุบัน</summary>
    public PipelineStage Stage { get; set; } = PipelineStage.New;
    /// <summary>มูลค่าที่คาดว่าจะได้ (บาท)</summary>
    public decimal Amount { get; set; }
    /// <summary>โอกาสปิดการขายที่เซลส์ประเมิน 0..100 (ถ้าไม่ระบุใช้ค่าเริ่มต้นตาม stage)</summary>
    public int? Probability { get; set; }
    /// <summary>คะแนนจาก ML 0..100 (Predictive lead scoring)</summary>
    public double? Score { get; set; }
    /// <summary>คาดว่าจะปิดการขายวันที่</summary>
    public DateOnly ExpectedCloseDate { get; set; }
    /// <summary>ที่มา (website, referral, event, LINE OA ...)</summary>
    public string? Source { get; set; }
    /// <summary>ผู้รับผิดชอบ</summary>
    public string? OwnerSubject { get; set; }
    /// <summary>เหตุผลที่แพ้ (เมื่อ Lost)</summary>
    public string? LostReason { get; set; }
    /// <summary>วันที่เข้าสู่ stage ปัจจุบัน (ใช้วัด velocity)</summary>
    public DateTimeOffset StageChangedAt { get; set; }
    /// <summary>วันที่ปิด (Won/Lost)</summary>
    public DateTimeOffset? ClosedAt { get; set; }
    /// <summary>เวลาสร้าง</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Optimistic concurrency token</summary>
    public long Version { get; set; }
    /// <summary>ชื่อลูกค้า (read model)</summary>
    public string? CustomerName { get; set; }

    /// <summary>ปิดแล้วหรือยัง</summary>
    public bool IsClosed => Stage is PipelineStage.Won or PipelineStage.Lost;
}

/// <summary>ประวัติการเปลี่ยน stage (ใช้เป็นข้อมูลฝึก lead scoring และวัด conversion)</summary>
public sealed class StageHistoryDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>โอกาสการขาย</summary>
    public long OpportunityId { get; set; }
    /// <summary>จาก stage</summary>
    public PipelineStage FromStage { get; set; }
    /// <summary>ไป stage</summary>
    public PipelineStage ToStage { get; set; }
    /// <summary>ผู้เปลี่ยน</summary>
    public string ChangedBy { get; set; } = string.Empty;
    /// <summary>เวลา</summary>
    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>ภาพรวม Pipeline หนึ่ง stage</summary>
/// <param name="Stage">stage</param>
/// <param name="Count">จำนวนโอกาส</param>
/// <param name="Amount">มูลค่ารวม</param>
/// <param name="WeightedAmount">มูลค่าถ่วงด้วยความน่าจะเป็น</param>
/// <param name="AverageDaysInStage">จำนวนวันเฉลี่ยที่อยู่ใน stage นี้</param>
public sealed record PipelineStageSummaryDTO(PipelineStage Stage, int Count, decimal Amount, decimal WeightedAmount, double AverageDaysInStage);

/// <summary>ภาพรวม Pipeline</summary>
/// <param name="Stages">ราย stage</param>
/// <param name="TotalOpenAmount">มูลค่าโอกาสที่ยังเปิด</param>
/// <param name="WeightedOpenAmount">มูลค่าถ่วงน้ำหนัก</param>
/// <param name="WinRate">อัตราชนะในอดีต (Won / (Won+Lost))</param>
public sealed record PipelineSummaryDTO(IReadOnlyList<PipelineStageSummaryDTO> Stages, decimal TotalOpenAmount, decimal WeightedOpenAmount, double WinRate);

/// <summary>
/// คำขอของลูกค้า / TOR / Change request / Incident — จุดเริ่มต้นของงานที่ส่งต่อไป
/// ProjectMgnt (โครงการ), RequirementMgnt (requirement จาก TOR) และ IssueTracking (incident พร้อม SLA)
/// </summary>
public sealed class CustomerRequestDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>เลขที่คำขอ เช่น <c>REQ-2026-00012</c></summary>
    public string RequestNo { get; set; } = string.Empty;
    /// <summary>ลูกค้า</summary>
    public long CustomerId { get; set; }
    /// <summary>ผู้ติดต่อฝั่งลูกค้า (บุคคล)</summary>
    public long? ContactPersonId { get; set; }
    /// <summary>ประเภท</summary>
    public RequestKind Kind { get; set; }
    /// <summary>หัวข้อ</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>รายละเอียด</summary>
    public string? Description { get; set; }
    /// <summary>สถานะ</summary>
    public RequestStatus Status { get; set; } = RequestStatus.New;
    /// <summary>รหัสระดับความรุนแรงตาม SLA ของลูกค้ารายนี้ (Incident) เช่น <c>S1</c></summary>
    public string? SeverityCode { get; set; }
    /// <summary>งบประมาณที่ลูกค้าแจ้ง</summary>
    public decimal? Budget { get; set; }
    /// <summary>วันที่ลูกค้าต้องการ</summary>
    public DateOnly? DesiredDate { get; set; }
    /// <summary>ผู้รับเรื่อง</summary>
    public string? OwnerSubject { get; set; }
    /// <summary>รายการใน TOR (แต่ละข้อกลายเป็น requirement ใน RequirementMgnt)</summary>
    public List<TorItemDTO> Items { get; set; } = [];
    /// <summary>ลายเซ็น PQC (ML-DSA-65 + ES256) ของเนื้อหา ณ เวลาส่งต่อ — พิสูจน์ได้ว่า TOR ที่ส่งต่อไม่ถูกแก้</summary>
    public string? ForwardSignature { get; set; }
    /// <summary>เวลาส่งต่อ</summary>
    public DateTimeOffset? ForwardedAt { get; set; }
    /// <summary>เวลาสร้าง</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>เวลาแก้ไขล่าสุด</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Optimistic concurrency token</summary>
    public long Version { get; set; }
}

/// <summary>หนึ่งข้อใน TOR</summary>
public sealed class TorItemDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>คำขอ</summary>
    public long RequestId { get; set; }
    /// <summary>ลำดับ/เลขข้อ เช่น 4.2.1</summary>
    public string ItemNo { get; set; } = string.Empty;
    /// <summary>ข้อความ requirement</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>ความสำคัญ: Low / Medium / High / Critical</summary>
    public string Priority { get; set; } = "Medium";
    /// <summary>บังคับ (must) หรือไม่ (nice to have)</summary>
    public bool Mandatory { get; set; } = true;
    /// <summary>key ของ artifact ใน RequirementMgnt หลังส่งต่อ</summary>
    public string? RequirementKey { get; set; }
}
