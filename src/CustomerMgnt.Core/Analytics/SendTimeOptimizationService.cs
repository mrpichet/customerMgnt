using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;

namespace CustomerMgnt.Core.Analytics;

/// <summary>
/// หาเวลาส่งข้อความที่ลูกค้ามีโอกาสเปิดอ่านสูงสุด (Send-Time Optimization):
/// <list type="number">
/// <item>นับการเปิด/คลิกย้อนหลัง 365 วัน ในตาราง 7 วัน × 24 ชั่วโมง ตามเขตเวลาของลูกค้า</item>
/// <item>รวมกับ prior แบบ Bayesian (รูปแบบเวลาทำการทั่วไปของไทย) เพื่อให้ลูกค้าที่ข้อมูลน้อยยังได้คำตอบที่สมเหตุสมผล</item>
/// <item>ทำ smoothing ชั่วโมงข้างเคียง แล้วเลือกช่องที่ความน่าจะเป็นสูงสุด และคำนวณเวลาส่งครั้งถัดไป</item>
/// </list>
/// </summary>
public sealed class SendTimeOptimizationService
{
    /// <summary>น้ำหนักของ prior (เทียบเท่าจำนวนการเปิดสมมติ)</summary>
    private const double PriorStrength = 8;

    private readonly ICustomerDAO _customers;
    private readonly IEngagementDAO _engagement;
    private readonly TimeProvider _clock;

    /// <summary>สร้างบริการ</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="engagement">DAO พฤติกรรม</param>
    /// <param name="clock">นาฬิกา</param>
    public SendTimeOptimizationService(ICustomerDAO customers, IEngagementDAO engagement, TimeProvider clock)
    {
        _customers = customers;
        _engagement = engagement;
        _clock = clock;
    }

    /// <summary>หาเวลาส่งที่ดีที่สุดของลูกค้าในช่องทางที่กำหนด</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="channel">ช่องทาง (Email ใช้สัญญาณ EmailOpen/EmailClick; ช่องทางอื่นใช้ MessageOpen)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>วัน/ชั่วโมงที่ดีที่สุด เวลาส่งครั้งถัดไป และอัตราเปิดที่คาด</returns>
    /// <exception cref="NotFoundException">ไม่พบลูกค้า</exception>
    public async Task<SendTimeDTO> GetBestTimeAsync(UserContextDTO user, long customerId, ChannelKind channel = ChannelKind.Email, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้า
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        CustomerDTO customer = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");

        // Step 2: เหตุการณ์ที่เกี่ยวข้องย้อนหลัง 1 ปี
        EngagementKind[] kinds = [EngagementKind.EmailSent, EngagementKind.EmailOpen, EngagementKind.EmailClick, EngagementKind.MessageOpen];
        IReadOnlyList<EngagementEventDTO> events = await _engagement.ListAsync([customerId], _clock.GetUtcNow().AddDays(-365), kinds, ct).ConfigureAwait(false);

        // Step 3: คำนวณ
        return Compute(customer, channel, events, _clock.GetUtcNow());
    }

    /// <summary>คำนวณเวลาส่งที่ดีที่สุด (pure function — ทดสอบได้)</summary>
    /// <param name="customer">ลูกค้า (ใช้เขตเวลา)</param>
    /// <param name="channel">ช่องทาง</param>
    /// <param name="events">เหตุการณ์ของลูกค้า</param>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <returns>ผล</returns>
    internal static SendTimeDTO Compute(CustomerDTO customer, ChannelKind channel, IReadOnlyList<EngagementEventDTO> events, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(events);
        TimeZoneInfo zone = BusinessCalendar.ResolveZone(customer.TimeZoneId);
        bool email = channel == ChannelKind.Email;

        // Step 1: histogram 7×24 ของการเปิดตามเวลาท้องถิ่นของลูกค้า + นับการส่ง
        double[] counts = new double[7 * 24];
        int opens = 0, sent = 0;
        foreach (EngagementEventDTO e in events)
        {
            bool isOpen = email
                ? e.Kind is EngagementKind.EmailOpen or EngagementKind.EmailClick
                : e.Kind == EngagementKind.MessageOpen && (e.Channel is null || e.Channel == channel);
            if (email && e.Kind == EngagementKind.EmailSent) sent++;
            if (!isOpen) continue;

            DateTimeOffset local = TimeZoneInfo.ConvertTime(e.OccurredAt, zone);
            counts[Cell(local.DayOfWeek, local.Hour)] += e.Kind == EngagementKind.EmailClick ? 1.5 : 1; // คลิก = สนใจมากกว่าเปิด
            opens++;
        }

        // Step 2: posterior = counts + prior × strength แล้ว smoothing ชั่วโมงข้างเคียง (0.25/0.5/0.25)
        double[] prior = Prior();
        double[] posterior = new double[counts.Length];
        for (int i = 0; i < counts.Length; i++) posterior[i] = counts[i] + (prior[i] * PriorStrength);
        double[] smooth = new double[posterior.Length];
        for (int d = 0; d < 7; d++)
        {
            for (int h = 0; h < 24; h++)
            {
                double prev = posterior[(d * 24) + ((h + 23) % 24)], next = posterior[(d * 24) + ((h + 1) % 24)];
                smooth[(d * 24) + h] = (0.25 * prev) + (0.5 * posterior[(d * 24) + h]) + (0.25 * next);
            }
        }

        // Step 3: ช่องที่ดีที่สุด
        int best = 0;
        for (int i = 1; i < smooth.Length; i++)
        {
            if (smooth[i] > smooth[best]) best = i;
        }

        var bestDay = (DayOfWeek)(best / 24);
        int bestHour = best % 24;

        // Step 4: อัตราเปิดที่คาด = อัตราเปิดพื้นฐาน (Beta(1,2) prior) × lift ของช่องเวลานั้น (เพดาน 0.95)
        double baseRate = email ? (opens + 1.0) / (Math.Max(sent, opens) + 3.0) : (opens + 1.0) / (opens + 3.0);
        double lift = smooth[best] / (smooth.Sum() / smooth.Length);
        double expected = Math.Min(0.95, baseRate * Math.Min(lift, 3));

        return new SendTimeDTO(customer.Id, channel, bestDay, bestHour, NextOccurrence(now, zone, bestDay, bestHour), Math.Round(expected, 4), opens);
    }

    /// <summary>
    /// prior: วันทำงานช่วง 09-11 และ 13-15 น. สูงสุด, ช่วงเย็น 19-21 น. รองลงมา, วันหยุดต่ำ, กลางคืนต่ำมาก (รวมเป็น 1)
    /// </summary>
    internal static double[] Prior()
    {
        double[] p = new double[7 * 24];
        for (int d = 0; d < 7; d++)
        {
            bool weekend = (DayOfWeek)d is DayOfWeek.Saturday or DayOfWeek.Sunday;
            for (int h = 0; h < 24; h++)
            {
                double w = h switch
                {
                    >= 9 and <= 11 => 3.0,
                    >= 13 and <= 15 => 2.5,
                    >= 19 and <= 21 => 1.8,
                    >= 7 and <= 18 => 1.2,
                    >= 22 or <= 5 => 0.1,
                    _ => 0.6,
                };
                p[Cell((DayOfWeek)d, h)] = weekend ? w * 0.4 : w;
            }
        }

        double sum = p.Sum();
        for (int i = 0; i < p.Length; i++) p[i] /= sum;
        return p;
    }

    /// <summary>index ของช่องในตาราง 7×24</summary>
    private static int Cell(DayOfWeek day, int hour) => ((int)day * 24) + hour;

    /// <summary>เวลา (UTC offset ของลูกค้า) ครั้งถัดไปที่ตรงกับวัน/ชั่วโมงที่ต้องการ และอยู่หลังเวลาปัจจุบันอย่างน้อย 15 นาที</summary>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <param name="zone">เขตเวลาลูกค้า</param>
    /// <param name="day">วัน</param>
    /// <param name="hour">ชั่วโมงท้องถิ่น</param>
    /// <returns>เวลาส่ง</returns>
    internal static DateTimeOffset NextOccurrence(DateTimeOffset now, TimeZoneInfo zone, DayOfWeek day, int hour)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(now.AddMinutes(15), zone);
        for (int add = 0; add <= 7; add++)
        {
            DateTime date = local.Date.AddDays(add);
            if (date.DayOfWeek != day) continue;
            var candidateLocal = date.AddHours(hour);
            var candidate = new DateTimeOffset(candidateLocal, zone.GetUtcOffset(candidateLocal));
            if (candidate >= local) return candidate;
        }

        DateTime fallback = local.Date.AddDays(7).AddHours(hour);
        return new DateTimeOffset(fallback, zone.GetUtcOffset(fallback));
    }
}
