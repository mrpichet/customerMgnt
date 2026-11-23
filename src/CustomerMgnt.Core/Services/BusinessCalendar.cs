using System.Globalization;
using CustomerMgnt.Core.Domain;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// ปฏิทินเวลาทำการของนโยบาย SLA (Pure C#, immutable, thread-safe)
/// คำนวณ "บวกนาทีทำการ" และ "นับนาทีทำการระหว่างสองเวลา" ตามเขตเวลา วันทำการ และวันหยุดของลูกค้า
/// </summary>
public sealed class BusinessCalendar
{
    private const int MaxDaysScan = 3_700; // กันลูปไม่รู้จบเมื่อปฏิทินผิดพลาด (~10 ปี)
    private readonly TimeZoneInfo _zone;
    private readonly TimeSpan _start;
    private readonly TimeSpan _end;
    private readonly int _workingDaysMask;
    private readonly HashSet<DateOnly> _holidays;

    /// <summary>สร้างปฏิทินจากนโยบาย SLA</summary>
    /// <param name="policy">นโยบาย</param>
    /// <exception cref="Common.Core.Exceptions.ValidationException">เขตเวลาหรือช่วงเวลาทำการไม่ถูกต้อง</exception>
    public BusinessCalendar(SlaPolicyDTO policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Step 1: เขตเวลา (IANA) — ไม่พบ = ข้อมูลนโยบายผิด
        _zone = ResolveZone(policy.TimeZoneId);

        // Step 2: เวลาทำการต้องเป็นช่วงบวกภายในวันเดียว
        _start = policy.BusinessStart.ToTimeSpan();
        _end = policy.BusinessEnd.ToTimeSpan();
        Common.Core.Exceptions.ValidationException.ThrowIf(_end <= _start, "businessEnd", "Business end must be after business start.");
        Common.Core.Exceptions.ValidationException.ThrowIf(policy.WorkingDaysMask is < 1 or > 127, "workingDaysMask", "At least one working day is required.");
        _workingDaysMask = policy.WorkingDaysMask;

        // Step 3: วันหยุดพิเศษ (รูปแบบ yyyy-MM-dd คั่นด้วย comma)
        _holidays = ParseHolidays(policy.Holidays);
    }

    /// <summary>แปลงชื่อเขตเวลาเป็น <see cref="TimeZoneInfo"/></summary>
    /// <param name="timeZoneId">ชื่อ IANA หรือ Windows</param>
    /// <returns>เขตเวลา</returns>
    /// <exception cref="Common.Core.Exceptions.ValidationException">ไม่พบเขตเวลา</exception>
    public static TimeZoneInfo ResolveZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZoneId) ? "Asia/Bangkok" : timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new Common.Core.Exceptions.ValidationException($"Unknown time zone '{timeZoneId}'.");
        }
    }

    /// <summary>แปลงรายการวันหยุดเป็นข้อความเก็บลงฐานข้อมูล</summary>
    /// <param name="holidays">วันหยุด</param>
    /// <returns>ข้อความ yyyy-MM-dd คั่นด้วย comma หรือ null</returns>
    public static string? FormatHolidays(IEnumerable<DateOnly> holidays)
    {
        string joined = string.Join(',', holidays.Distinct().Order().Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>บวกนาที — ถ้า <paramref name="businessHoursOnly"/> นับเฉพาะเวลาทำการ ไม่เช่นนั้นนับ 24x7</summary>
    /// <param name="start">เวลาเริ่ม</param>
    /// <param name="minutes">จำนวนนาที (≥ 0)</param>
    /// <param name="businessHoursOnly">นับเฉพาะเวลาทำการ</param>
    /// <returns>เวลาครบกำหนด (offset เดียวกับ UTC)</returns>
    public DateTimeOffset AddMinutes(DateTimeOffset start, int minutes, bool businessHoursOnly)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minutes);

        // Step 1: 24x7 → บวกตรงๆ
        if (!businessHoursOnly)
        {
            return start.ToUniversalTime().AddMinutes(minutes);
        }

        // Step 2: แปลงเป็นเวลาท้องถิ่นของนโยบาย แล้วเดินทีละวันทำการ
        DateTime local = TimeZoneInfo.ConvertTime(start, _zone).DateTime;
        double remaining = minutes;
        for (int day = 0; day < MaxDaysScan; day++)
        {
            DateOnly date = DateOnly.FromDateTime(local);
            if (IsWorkingDay(date))
            {
                // Step 3: ตัดให้อยู่ในช่วงทำการของวันนั้น
                DateTime dayStart = local.Date + _start;
                DateTime dayEnd = local.Date + _end;
                DateTime cursor = local < dayStart ? dayStart : local;
                if (cursor < dayEnd)
                {
                    double available = (dayEnd - cursor).TotalMinutes;
                    if (remaining <= available)
                    {
                        // Step 4: จบในวันนี้ → แปลงกลับเป็น UTC
                        return ToUtc(cursor.AddMinutes(remaining));
                    }

                    remaining -= available;
                }
            }

            // Step 5: ไปเริ่มต้นวันถัดไป
            local = local.Date.AddDays(1) + _start;
        }

        throw new InvalidOperationException("Business calendar has no working time.");
    }

    /// <summary>นับนาทีระหว่างสองเวลา (เฉพาะเวลาทำการ หรือ 24x7)</summary>
    /// <param name="from">เริ่ม</param>
    /// <param name="to">สิ้นสุด</param>
    /// <param name="businessHoursOnly">นับเฉพาะเวลาทำการ</param>
    /// <returns>จำนวนนาที (0 ถ้า to ≤ from)</returns>
    public double MinutesBetween(DateTimeOffset from, DateTimeOffset to, bool businessHoursOnly)
    {
        // Step 1: กรณีง่าย
        if (to <= from)
        {
            return 0;
        }

        if (!businessHoursOnly)
        {
            return (to - from).TotalMinutes;
        }

        // Step 2: เดินทีละวัน รวมส่วนที่ทับช่วงเวลาทำการ
        DateTime a = TimeZoneInfo.ConvertTime(from, _zone).DateTime;
        DateTime b = TimeZoneInfo.ConvertTime(to, _zone).DateTime;
        double total = 0;
        DateTime day = a.Date;
        for (int i = 0; i < MaxDaysScan && day <= b.Date; i++, day = day.AddDays(1))
        {
            if (!IsWorkingDay(DateOnly.FromDateTime(day)))
            {
                continue;
            }

            DateTime s = Max(day + _start, a);
            DateTime e = Min(day + _end, b);
            if (e > s)
            {
                total += (e - s).TotalMinutes;
            }
        }

        return total;
    }

    /// <summary>เป็นวันทำการหรือไม่</summary>
    /// <param name="date">วันที่ (เวลาท้องถิ่น)</param>
    /// <returns>true ถ้าเป็นวันทำการและไม่ใช่วันหยุดพิเศษ</returns>
    public bool IsWorkingDay(DateOnly date)
    {
        // bit0 = จันทร์ ... bit6 = อาทิตย์
        int bit = ((int)date.DayOfWeek + 6) % 7;
        return (_workingDaysMask & (1 << bit)) != 0 && !_holidays.Contains(date);
    }

    /// <summary>แปลงเวลาท้องถิ่นของนโยบายเป็น UTC (ถ้าตกช่วง DST gap ให้เลื่อนไปข้างหน้า 1 ชั่วโมง)</summary>
    /// <param name="local">เวลาท้องถิ่น</param>
    /// <returns>เวลา UTC</returns>
    private DateTimeOffset ToUtc(DateTime local)
    {
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (_zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, _zone), TimeSpan.Zero);
    }

    /// <summary>แปลงข้อความวันหยุด</summary>
    /// <param name="text">ข้อความ</param>
    /// <returns>ชุดวันหยุด</returns>
    private static HashSet<DateOnly> ParseHolidays(string? text)
    {
        var set = new HashSet<DateOnly>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return set;
        }

        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateOnly.TryParseExact(part, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
            {
                set.Add(d);
            }
        }

        return set;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
