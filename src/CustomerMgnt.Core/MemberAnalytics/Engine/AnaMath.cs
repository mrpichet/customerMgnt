using CustomerMgnt.Core.MemberAnalytics.Domain;

namespace CustomerMgnt.Core.MemberAnalytics.Engine;

/// <summary>เวลาของระบบวิเคราะห์: วันที่ทั้งหมดคิดตามเวลา Asia/Bangkok</summary>
public static class AnaTime
{
    private static readonly Lazy<TimeZoneInfo> ZoneLazy = new(() =>
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Asia/Bangkok", TimeSpan.FromHours(7), "Asia/Bangkok", "Asia/Bangkok");
        }
    });

    /// <summary>เขตเวลา</summary>
    public static TimeZoneInfo Zone => ZoneLazy.Value;

    /// <summary>วันที่ (เวลาไทย) ของเวลา</summary>
    public static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);

    /// <summary>เวลาเริ่มของวัน (เวลาไทย) เป็น DateTimeOffset</summary>
    public static DateTimeOffset Start(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    /// <summary>ช่วง [เริ่มวัน from, เริ่มวันถัดจาก to)</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Range(DateOnly from, DateOnly to) => (Start(from), Start(to.AddDays(1)));

    /// <summary>วันนี้ (เวลาไทย)</summary>
    public static DateOnly Today(TimeProvider clock) => Day(clock.GetUtcNow());

    /// <summary>ทุกวันในช่วง</summary>
    public static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
    {
        for (DateOnly d = from; d <= to; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    /// <summary>รหัสช่วงของกราฟ</summary>
    public static DateOnly Bucket(DateOnly d, Granularity g) => g switch
    {
        Granularity.Week => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)), // เริ่มวันจันทร์
        Granularity.Month => new DateOnly(d.Year, d.Month, 1),
        _ => d,
    };

    private static readonly string[] ThaiMonths = ["ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค."];

    /// <summary>ป้ายของช่วงกราฟ (ภาษาไทย ปี พ.ศ. สำหรับรายเดือน)</summary>
    public static string Label(DateOnly bucket, Granularity g) => g switch
    {
        Granularity.Month => $"{ThaiMonths[bucket.Month - 1]} {(bucket.Year + 543) % 100:00}",
        Granularity.Week => $"สัปดาห์ {bucket.Day} {ThaiMonths[bucket.Month - 1]}",
        _ => $"{bucket.Day} {ThaiMonths[bucket.Month - 1]}",
    };

    /// <summary>รหัสเดือน yyyy-MM</summary>
    public static string MonthKey(DateOnly d) => $"{d.Year:0000}-{d.Month:00}";
}

/// <summary>สูตรคำนวณพื้นฐาน (สูตรข้อ 1–5)</summary>
public static class AnaMath
{
    /// <summary>หารแบบปลอดภัย (ตัวหาร 0 = null)</summary>
    public static decimal? Div(decimal num, decimal den) => den == 0 ? null : num / den;

    /// <summary>ร้อยละ 2 ตำแหน่ง (ตัวหาร 0 = null)</summary>
    public static decimal? Pct(decimal num, decimal den) => den == 0 ? null : Math.Round(num * 100m / den, 2);

    /// <summary>% เปลี่ยนแปลงจากค่าเปรียบเทียบ (ฐาน 0 หรือไม่มี = null)</summary>
    public static decimal? Change(decimal? value, decimal? compare) =>
        value is decimal v && compare is decimal c && c != 0 ? Math.Round((v - c) * 100m / Math.Abs(c), 2) : null;

    /// <summary>ตัวเลข 1 ตัว (คำนวณ % เปลี่ยนแปลงให้)</summary>
    public static MetricValueDTO Metric(string metric, decimal? value, MetricUnit unit, MetricKind kind = MetricKind.Fact, decimal? compare = null,
        string? insufficient = null, string? drilldown = null) => new()
        {
            Metric = metric,
            Value = insufficient is null ? Round(value, unit) : null,
            Unit = unit,
            Kind = kind,
            CompareValue = insufficient is null ? Round(compare, unit) : null,
            ChangePct = insufficient is null ? Change(value, compare) : null,
            InsufficientReason = insufficient ?? (value is null ? AnaFormula.ZeroDenominator : null),
            Drilldown = drilldown,
        };

    /// <summary>ตัวเลขที่คำนวณไม่ได้</summary>
    public static MetricValueDTO Insufficient(string metric, MetricUnit unit, string reason, MetricKind kind = MetricKind.Computed) =>
        new() { Metric = metric, Unit = unit, Kind = kind, InsufficientReason = reason };

    /// <summary>ปัดตามหน่วย (เงิน/ร้อยละ 2 ตำแหน่ง)</summary>
    public static decimal? Round(decimal? v, MetricUnit unit) => v is decimal x ? unit switch
    {
        MetricUnit.Count or MetricUnit.Points => Math.Round(x, 2),
        _ => Math.Round(x, 2),
    } : null;

    /// <summary>ขอบ quintile (4 ค่า) จากค่าที่เรียงแล้ว</summary>
    public static List<decimal> Quintiles(IReadOnlyList<decimal> sorted)
    {
        if (sorted.Count == 0)
        {
            return [];
        }

        return [.. Enumerable.Range(1, 4).Select(i => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * i / 5.0) - 1)])];
    }

    /// <summary>คะแนน 1–5 จากขอบ quintile (ค่ามาก = คะแนนมาก; invert = ค่าน้อยได้คะแนนมาก เช่น recency)</summary>
    public static int Score(decimal value, IReadOnlyList<decimal> bounds, bool invert)
    {
        int above = bounds.Count(b => value > b);
        return Math.Clamp(invert ? 5 - above : 1 + above, 1, 5);
    }

    /// <summary>มัธยฐาน</summary>
    public static decimal? Median(List<decimal> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[(n / 2) - 1] + values[n / 2]) / 2m;
    }

    /// <summary>
    /// ผลต่างอัตราสองกลุ่มพร้อมช่วงเชื่อมั่น 95%: p_t − p_c ± 1.96 × √(p_t(1−p_t)/n_t + p_c(1−p_c)/n_c) — หน่วยเป็นจุดร้อยละ
    /// </summary>
    public static (decimal Diff, decimal Low, decimal High) TwoProportion(int successT, int nT, int successC, int nC)
    {
        double pt = (double)successT / nT;
        double pc = (double)successC / nC;
        double se = Math.Sqrt((pt * (1 - pt) / nT) + (pc * (1 - pc) / nC));
        double diff = pt - pc;
        return ((decimal)Math.Round(diff * 100, 2), (decimal)Math.Round((diff - (1.96 * se)) * 100, 2), (decimal)Math.Round((diff + (1.96 * se)) * 100, 2));
    }

    /// <summary>ช่วงเปรียบเทียบ</summary>
    public static (DateOnly From, DateOnly To)? CompareRange(DateOnly from, DateOnly to, CompareMode mode)
    {
        int days = to.DayNumber - from.DayNumber + 1;
        return mode switch
        {
            CompareMode.PreviousPeriod => (from.AddDays(-days), from.AddDays(-1)),
            CompareMode.SamePeriodLastYear => (from.AddYears(-1), to.AddYears(-1)),
            _ => null,
        };
    }
}
