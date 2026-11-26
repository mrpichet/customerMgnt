namespace CustomerMgnt.Core.MemberAnalytics.Engine;

/// <summary>cron 5 ช่อง (นาที ชั่วโมง วันที่ เดือน วันในสัปดาห์) รองรับ <c>*</c>, ตัวเลข, <c>a-b</c>, <c>a,b</c>, <c>*/n</c></summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _weekdays = new bool[7];
    private readonly bool _anyDay;
    private readonly bool _anyWeekday;

    private CronSchedule(string[] f)
    {
        Fill(_minutes, f[0], 0, 59);
        Fill(_hours, f[1], 0, 23);
        _anyDay = f[2] == "*";
        Fill(_days, f[2], 1, 31);
        Fill(_months, f[3], 1, 12);
        _anyWeekday = f[4] == "*";
        Fill(_weekdays, f[4].Replace("7", "0", StringComparison.Ordinal), 0, 6);
    }

    /// <summary>แปลงข้อความ (ผิดรูปแบบ = false)</summary>
    public static bool TryParse(string? text, out CronSchedule? schedule)
    {
        schedule = null;
        string[] f = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5)
        {
            return false;
        }

        try
        {
            schedule = new CronSchedule(f);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>เวลาถัดไปหลัง <paramref name="after"/> ตามเขตเวลา</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTime local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        DateTime limit = local.AddYears(2);
        while (local < limit)
        {
            if (!_months[local.Month] || !DayMatches(local))
            {
                local = local.Date.AddDays(1);
                continue;
            }

            if (!_hours[local.Hour])
            {
                local = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
                continue;
            }

            if (_minutes[local.Minute])
            {
                return new DateTimeOffset(local, zone.GetUtcOffset(local));
            }

            local = local.AddMinutes(1);
        }

        return null;
    }

    private bool DayMatches(DateTime d)
    {
        bool dom = _days[d.Day], dow = _weekdays[(int)d.DayOfWeek];
        return _anyDay && _anyWeekday ? true : _anyDay ? dow : _anyWeekday ? dom : dom || dow;
    }

    private static void Fill(bool[] target, string field, int min, int max)
    {
        foreach (string part in field.Split(','))
        {
            string range = part;
            int step = 1;
            int slash = part.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                step = int.Parse(part[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                range = part[..slash];
                if (step < 1)
                {
                    throw new FormatException("step");
                }
            }

            (int lo, int hi) = range == "*" ? (min, max)
                : range.Contains('-', StringComparison.Ordinal)
                    ? (int.Parse(range.Split('-')[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(range.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture))
                    : (int.Parse(range, System.Globalization.CultureInfo.InvariantCulture), slash >= 0 ? max : int.Parse(range, System.Globalization.CultureInfo.InvariantCulture));
            if (lo < min || hi > max || lo > hi)
            {
                throw new FormatException("range");
            }

            for (int i = lo; i <= hi; i += step)
            {
                target[i] = true;
            }
        }
    }
}
