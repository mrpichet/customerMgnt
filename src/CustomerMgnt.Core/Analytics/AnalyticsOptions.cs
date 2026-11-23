namespace CustomerMgnt.Core.Analytics;

/// <summary>ค่าตั้งของ Analytics/ML (section <c>Analytics</c>)</summary>
public sealed class AnalyticsOptions
{
    /// <summary>ชื่อ section</summary>
    public const string SectionName = "Analytics";
    /// <summary>อายุของโมเดลใน cache ก่อนฝึกใหม่ (นาที)</summary>
    public int ModelTtlMinutes { get; set; } = 360;
    /// <summary>จำนวนตัวอย่างขั้นต่ำต่อคลาสก่อนฝึกจากข้อมูลจริง (น้อยกว่านี้ใช้โมเดลตั้งต้น)</summary>
    public int MinSamplesPerClass { get; set; } = 15;
    /// <summary>จำนวนกลุ่มลูกค้าใน segmentation</summary>
    public int SegmentCount { get; set; } = 5;
    /// <summary>จำนวนลูกค้าสูงสุดที่ประมวลผลต่อรอบ batch</summary>
    public int MaxCustomersPerRun { get; set; } = 200_000;
    /// <summary>รอบการฝึกโมเดลใหม่และจัดกลุ่มลูกค้าอัตโนมัติ (ชั่วโมง, 0 = ปิด)</summary>
    public int RefreshHours { get; set; } = 24;
    /// <summary>หน่วงเวลาก่อนรอบแรกหลังเริ่มระบบ (วินาที) เพื่อไม่แย่งทรัพยากรตอน start</summary>
    public int RefreshInitialDelaySeconds { get; set; } = 120;
    /// <summary>บันทึกผล segmentation กลับลงลูกค้าในรอบอัตโนมัติ</summary>
    public bool PersistSegments { get; set; } = true;
}
