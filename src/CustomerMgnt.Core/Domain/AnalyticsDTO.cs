namespace CustomerMgnt.Core.Domain;

/// <summary>ปัจจัยที่มีผลต่อคะแนน (อธิบายผลของ ML ให้คนเข้าใจได้)</summary>
/// <param name="Feature">ชื่อ feature</param>
/// <param name="Value">ค่าของ feature</param>
/// <param name="Contribution">ผลต่อคะแนน (+ เพิ่ม, − ลด) ในหน่วย log-odds</param>
public sealed record FactorDTO(string Feature, double Value, double Contribution);

/// <summary>คะแนน Lead (Predictive lead scoring)</summary>
/// <param name="OpportunityId">โอกาสการขาย</param>
/// <param name="CustomerId">ลูกค้า</param>
/// <param name="Title">ชื่อโอกาส</param>
/// <param name="Score">คะแนน 0..100 (ความน่าจะเป็นที่จะปิดการขาย)</param>
/// <param name="Grade">A/B/C/D</param>
/// <param name="TopFactors">ปัจจัยหลัก</param>
public sealed record LeadScoreDTO(long OpportunityId, long CustomerId, string Title, double Score, string Grade, IReadOnlyList<FactorDTO> TopFactors);

/// <summary>ค่าพยากรณ์ 1 งวด</summary>
/// <param name="Period">งวด (วันแรกของเดือน)</param>
/// <param name="Forecast">ค่าพยากรณ์</param>
/// <param name="Lower">ขอบล่าง (~80%)</param>
/// <param name="Upper">ขอบบน (~80%)</param>
/// <param name="PipelineContribution">ส่วนที่มาจาก pipeline ที่คาดว่าจะปิดในงวดนี้</param>
public sealed record ForecastPointDTO(DateOnly Period, double Forecast, double Lower, double Upper, double PipelineContribution);

/// <summary>ผลพยากรณ์ยอดขาย / งบประมาณ</summary>
/// <param name="Method">วิธี เช่น "Holt-Winters additive + weighted pipeline"</param>
/// <param name="History">ข้อมูลจริงย้อนหลัง (งวด → ค่า)</param>
/// <param name="Points">ค่าพยากรณ์</param>
/// <param name="SeasonalIndex">ดัชนีฤดูกาลรายเดือน (1..12 → ค่า, 1.0 = เฉลี่ย)</param>
/// <param name="Mape">ความคลาดเคลื่อนเฉลี่ย (in-sample MAPE %) — ยิ่งต่ำยิ่งแม่น</param>
public sealed record ForecastDTO(string Method, IReadOnlyList<ForecastPointDTO> History, IReadOnlyList<ForecastPointDTO> Points, IReadOnlyDictionary<int, double> SeasonalIndex, double Mape);

/// <summary>การกระทำถัดไปที่แนะนำ</summary>
/// <param name="CustomerId">ลูกค้า</param>
/// <param name="Action">สิ่งที่ควรทำ เช่น "โทรหา", "ส่งข้อเสนอ"</param>
/// <param name="Reason">เหตุผล</param>
/// <param name="Priority">ความเร่งด่วน 0..100</param>
/// <param name="SuggestedAt">ควรทำเมื่อ (เวลาที่ลูกค้ามักตอบสนอง)</param>
/// <param name="Channel">ช่องทางที่แนะนำ</param>
/// <param name="Offer">ข้อเสนอ/สินค้าที่แนะนำ (ถ้ามี)</param>
public sealed record NextBestActionDTO(long CustomerId, string Action, string Reason, int Priority, DateTimeOffset SuggestedAt, ChannelKind Channel, string? Offer);

/// <summary>ผลวิเคราะห์อารมณ์</summary>
/// <param name="Score">คะแนน -1..1</param>
/// <param name="Label">ป้าย</param>
/// <param name="Confidence">ความมั่นใจ 0..1</param>
/// <param name="Signals">คำ/สัญญาณที่พบ</param>
/// <param name="NeedsAttention">ควรแจ้งทีมงานให้รีบดูแล</param>
public sealed record SentimentDTO(double Score, SentimentLabel Label, double Confidence, IReadOnlyList<string> Signals, bool NeedsAttention);

/// <summary>กลุ่มลูกค้า 1 กลุ่ม (Dynamic segmentation)</summary>
/// <param name="Name">ชื่อกลุ่มที่ตั้งจากลักษณะเด่น เช่น "Champions"</param>
/// <param name="Size">จำนวนลูกค้า</param>
/// <param name="Centroid">ค่ากลางของ feature (ชื่อ → ค่า)</param>
/// <param name="CustomerIds">ลูกค้าในกลุ่ม (สูงสุด 1,000 รายการแรก)</param>
public sealed record SegmentDTO(string Name, int Size, IReadOnlyDictionary<string, double> Centroid, IReadOnlyList<long> CustomerIds);

/// <summary>ผลการแบ่งกลุ่ม</summary>
/// <param name="Segments">กลุ่ม</param>
/// <param name="Iterations">จำนวนรอบที่ k-means ใช้</param>
/// <param name="Inertia">ผลรวมระยะทางกำลังสองภายในกลุ่ม (ยิ่งต่ำยิ่งแยกชัด)</param>
/// <param name="ComputedAt">เวลาคำนวณ</param>
public sealed record SegmentationResultDTO(IReadOnlyList<SegmentDTO> Segments, int Iterations, double Inertia, DateTimeOffset ComputedAt);

/// <summary>สินค้า/บริการ/บทความที่แนะนำ</summary>
/// <param name="ItemCode">รหัส</param>
/// <param name="Name">ชื่อ</param>
/// <param name="Kind">ชนิด</param>
/// <param name="Score">คะแนน 0..1</param>
/// <param name="Reason">เหตุผล</param>
public sealed record RecommendationDTO(string ItemCode, string Name, CatalogItemKind Kind, double Score, string Reason);

/// <summary>เวลาส่งข้อความที่เหมาะสมที่สุด</summary>
/// <param name="CustomerId">ลูกค้า</param>
/// <param name="Channel">ช่องทาง</param>
/// <param name="BestDayOfWeek">วันในสัปดาห์</param>
/// <param name="BestHourLocal">ชั่วโมง (เวลาท้องถิ่นของลูกค้า)</param>
/// <param name="NextSendAt">เวลาส่งครั้งถัดไปที่แนะนำ (UTC)</param>
/// <param name="ExpectedOpenRate">อัตราเปิดอ่านที่คาด</param>
/// <param name="Observations">จำนวนเหตุการณ์ที่ใช้คำนวณ</param>
public sealed record SendTimeDTO(long CustomerId, ChannelKind Channel, DayOfWeek BestDayOfWeek, int BestHourLocal, DateTimeOffset NextSendAt, double ExpectedOpenRate, int Observations);

/// <summary>ความเสี่ยงที่ลูกค้าจะเลิกใช้บริการ</summary>
/// <param name="CustomerId">ลูกค้า</param>
/// <param name="CustomerName">ชื่อ</param>
/// <param name="Probability">ความน่าจะเป็น 0..1</param>
/// <param name="Level">Low/Medium/High/Critical</param>
/// <param name="Signals">สัญญาณเตือน</param>
/// <param name="RecommendedAction">สิ่งที่ควรทำ</param>
public sealed record ChurnRiskDTO(long CustomerId, string CustomerName, double Probability, string Level, IReadOnlyList<FactorDTO> Signals, string RecommendedAction);

/// <summary>พยากรณ์การต่อสัญญา MA และโอกาส upsell</summary>
/// <param name="ContractId">สัญญา</param>
/// <param name="CustomerId">ลูกค้า</param>
/// <param name="ContractNo">เลขที่สัญญา</param>
/// <param name="EndDate">วันหมดอายุ</param>
/// <param name="RenewalProbability">โอกาสต่อสัญญา 0..1</param>
/// <param name="ExpectedRenewalValue">มูลค่าที่คาดว่าจะต่อ (ถ่วงด้วยความน่าจะเป็น)</param>
/// <param name="ExpansionProbability">โอกาส upsell/expansion 0..1</param>
/// <param name="SuggestedUpsell">สิ่งที่แนะนำให้เสนอเพิ่ม</param>
/// <param name="Factors">ปัจจัย</param>
public sealed record RenewalForecastDTO(long ContractId, long CustomerId, string ContractNo, DateOnly EndDate, double RenewalProbability, decimal ExpectedRenewalValue, double ExpansionProbability, string? SuggestedUpsell, IReadOnlyList<FactorDTO> Factors);

/// <summary>สรุปการพยากรณ์การต่อสัญญาทั้งหมดในช่วงเวลา</summary>
/// <param name="From">ตั้งแต่</param>
/// <param name="To">ถึง</param>
/// <param name="Contracts">รายสัญญา</param>
/// <param name="TotalAtStake">มูลค่าสัญญาที่จะหมดอายุ</param>
/// <param name="ExpectedRenewal">มูลค่าที่คาดว่าจะต่อ</param>
public sealed record RenewalSummaryDTO(DateOnly From, DateOnly To, IReadOnlyList<RenewalForecastDTO> Contracts, decimal TotalAtStake, decimal ExpectedRenewal);
