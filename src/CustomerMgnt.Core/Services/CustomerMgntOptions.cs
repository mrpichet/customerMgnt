namespace CustomerMgnt.Core.Services;

/// <summary>ค่าตั้งทางธุรกิจของ CustomerMgnt (section <c>CustomerMgnt</c>)</summary>
public sealed class CustomerMgntOptions
{
    /// <summary>ชื่อ section</summary>
    public const string SectionName = "CustomerMgnt";
    /// <summary>prefix ของรหัสลูกค้าที่ระบบสร้างให้</summary>
    public string CustomerCodePrefix { get; set; } = "CUST-";
    /// <summary>prefix ของเลขที่คำขอ</summary>
    public string RequestNoPrefix { get; set; } = "REQ";
    /// <summary>คะแนนอารมณ์ต่ำกว่านี้ถือว่าลูกค้าไม่พอใจ → แจ้งเตือนทีม</summary>
    public double NegativeSentimentAlertThreshold { get; set; } = -0.35;
    /// <summary>ขอบของช่วงอายุ issue ค้าง (วัน) เช่น [7, 30, 90] → 0-7, 7-30, 30-90, 90+</summary>
    public int[] AgingBucketDays { get; set; } = [7, 30, 90];
    /// <summary>ช่วงเวลาตรวจ reminder ของกิจกรรม (วินาที)</summary>
    public int ReminderScanSeconds { get; set; } = 60;
    /// <summary>ผู้ใช้ MessageHub ที่รับแจ้งเตือนเมื่อ conversation ยังไม่มีผู้รับผิดชอบ</summary>
    public Guid? DefaultAlertUserId { get; set; }
}
