namespace CustomerMgnt.Core.Domain;

/// <summary>
/// นโยบาย SLA (สัญญาระดับบริการ) — กำหนดแยกตามลูกค้าได้ หรือเป็นนโยบายมาตรฐาน (<see cref="CustomerId"/> = null)
/// ลูกค้าแต่ละรายกำหนด "ระดับ Severity ของตัวเอง" ได้ (ชื่อ/จำนวนระดับ/เวลาตอบ-แก้ ต่างกัน) ผ่าน <see cref="Levels"/>
/// IssueTracking ใช้ข้อมูลชุดนี้คำนวณกำหนดเวลาของ issue ที่ผูกกับลูกค้า (ดู SlaService.CalculateTargets)
/// </summary>
public sealed class SlaPolicyDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>ลูกค้า (null = นโยบายมาตรฐานของบริษัท ใช้เมื่อลูกค้าไม่มีนโยบายเฉพาะ)</summary>
    public long? CustomerId { get; set; }
    /// <summary>ชื่อนโยบาย เช่น "Gold 24x7"</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>คำอธิบาย</summary>
    public string? Description { get; set; }
    /// <summary>มีผลตั้งแต่</summary>
    public DateOnly ValidFrom { get; set; }
    /// <summary>มีผลถึง (null = ไม่มีวันหมดอายุ)</summary>
    public DateOnly? ValidTo { get; set; }
    /// <summary>เขตเวลาที่ใช้นับเวลาทำการ (IANA)</summary>
    public string TimeZoneId { get; set; } = "Asia/Bangkok";
    /// <summary>เวลาเริ่มงาน (นับ SLA แบบเวลาทำการ)</summary>
    public TimeOnly BusinessStart { get; set; } = new(8, 30);
    /// <summary>เวลาเลิกงาน</summary>
    public TimeOnly BusinessEnd { get; set; } = new(17, 30);
    /// <summary>วันทำการแบบ bit mask (bit0 = จันทร์ ... bit6 = อาทิตย์), ค่าเริ่มต้น จันทร์-ศุกร์ = 31</summary>
    public int WorkingDaysMask { get; set; } = 31;
    /// <summary>วันหยุดพิเศษ (คั่นด้วย comma รูปแบบ yyyy-MM-dd)</summary>
    public string? Holidays { get; set; }
    /// <summary>เกณฑ์เตือน "ใกล้ผิด SLA" เป็นสัดส่วนของเวลาที่ใช้ไป (ค่าเริ่มต้น 0.8 = 80%)</summary>
    public double AtRiskThreshold { get; set; } = 0.8;
    /// <summary>ใช้งานอยู่</summary>
    public bool IsActive { get; set; } = true;
    /// <summary>Optimistic concurrency token</summary>
    public long Version { get; set; }
    /// <summary>ระดับความรุนแรงของนโยบายนี้</summary>
    public List<SeverityLevelDTO> Levels { get; set; } = [];

    /// <summary>ตรวจว่านโยบายมีผล ณ วันที่</summary>
    /// <param name="date">วันที่</param>
    /// <returns>true ถ้าใช้งานอยู่และอยู่ในช่วงวันที่</returns>
    public bool IsEffectiveOn(DateOnly date) => IsActive && ValidFrom <= date && (ValidTo is null || date <= ValidTo.Value);
}

/// <summary>
/// ระดับความรุนแรง 1 ระดับในนโยบาย SLA ของลูกค้า
/// <see cref="Rank"/> ทำให้ระดับของลูกค้าต่างรายเปรียบเทียบกันได้ (1 = รุนแรงที่สุด) และแปลงเป็น Priority ของ IssueTracking
/// </summary>
public sealed class SeverityLevelDTO
{
    /// <summary>รหัส</summary>
    public long Id { get; set; }
    /// <summary>นโยบาย</summary>
    public long PolicyId { get; set; }
    /// <summary>รหัสระดับที่ลูกค้าใช้ เช่น <c>S1</c>, <c>P1</c>, <c>CRITICAL</c> (A-Z0-9_- ไม่เกิน 20 ตัว)</summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>ชื่อ เช่น "ระบบล่มทั้งหมด"</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>คำอธิบาย/เกณฑ์การจัดระดับ</summary>
    public string? Description { get; set; }
    /// <summary>อันดับความรุนแรงมาตรฐาน 1..5 (1 = รุนแรงที่สุด)</summary>
    public int Rank { get; set; }
    /// <summary>เวลาตอบกลับครั้งแรก (นาที)</summary>
    public int ResponseMinutes { get; set; }
    /// <summary>เวลาแก้ไขให้เสร็จ (นาที)</summary>
    public int ResolutionMinutes { get; set; }
    /// <summary>true = นับเฉพาะเวลาทำการ, false = นับ 24x7</summary>
    public bool BusinessHoursOnly { get; set; } = true;
    /// <summary>Priority ที่ใช้ใน IssueTracking: Low, Normal, High, Urgent, Immediate</summary>
    public string IssuePriority { get; set; } = "Normal";
}

/// <summary>กำหนดเวลา SLA ที่คำนวณได้สำหรับงาน 1 รายการ</summary>
/// <param name="CustomerCode">รหัสลูกค้า</param>
/// <param name="PolicyId">นโยบายที่ใช้</param>
/// <param name="PolicyName">ชื่อนโยบาย</param>
/// <param name="SeverityCode">รหัสระดับ</param>
/// <param name="SeverityName">ชื่อระดับ</param>
/// <param name="SeverityRank">อันดับมาตรฐาน 1..5</param>
/// <param name="IssuePriority">Priority ที่แนะนำให้ IssueTracking</param>
/// <param name="StartedAt">เวลาเริ่มนับ</param>
/// <param name="ResponseDueAt">ต้องตอบภายใน</param>
/// <param name="ResolutionDueAt">ต้องแก้เสร็จภายใน</param>
/// <param name="BusinessHoursOnly">นับเฉพาะเวลาทำการ</param>
public sealed record SlaTargetDTO(
    string CustomerCode,
    long PolicyId,
    string PolicyName,
    string SeverityCode,
    string SeverityName,
    int SeverityRank,
    string IssuePriority,
    DateTimeOffset StartedAt,
    DateTimeOffset ResponseDueAt,
    DateTimeOffset ResolutionDueAt,
    bool BusinessHoursOnly);

/// <summary>ผลประเมินสถานะ SLA ของงาน 1 รายการ ณ เวลาหนึ่ง</summary>
/// <param name="Target">กำหนดเวลา</param>
/// <param name="ResponseState">สถานะการตอบครั้งแรก</param>
/// <param name="ResolutionState">สถานะการแก้ไข</param>
/// <param name="ResolutionElapsedRatio">สัดส่วนเวลาที่ใช้ไปของ resolution (1.0 = ครบกำหนดพอดี)</param>
/// <param name="MinutesToResolutionDue">นาทีที่เหลือก่อนครบกำหนด (ติดลบ = เลยกำหนด)</param>
public sealed record SlaEvaluationDTO(SlaTargetDTO Target, SlaState ResponseState, SlaState ResolutionState, double ResolutionElapsedRatio, double MinutesToResolutionDue);

/// <summary>สรุปการปฏิบัติตาม SLA ของลูกค้า</summary>
/// <param name="CustomerCode">รหัสลูกค้า</param>
/// <param name="Total">จำนวนงานที่ประเมิน</param>
/// <param name="Met">ทำสำเร็จทันเวลา</param>
/// <param name="Breached">ผิด SLA</param>
/// <param name="AtRisk">ใกล้ผิด</param>
/// <param name="OnTrack">อยู่ในเวลา</param>
/// <param name="CompliancePercent">ร้อยละที่ไม่ผิด SLA</param>
/// <param name="BySeverity">แยกตามระดับ (code → จำนวนที่ผิด SLA / ทั้งหมด)</param>
public sealed record SlaComplianceDTO(string CustomerCode, int Total, int Met, int Breached, int AtRisk, int OnTrack, double CompliancePercent, IReadOnlyDictionary<string, SlaBucketDTO> BySeverity);

/// <summary>ตัวนับราย severity</summary>
/// <param name="Total">ทั้งหมด</param>
/// <param name="Breached">ผิด SLA</param>
public sealed record SlaBucketDTO(int Total, int Breached);
