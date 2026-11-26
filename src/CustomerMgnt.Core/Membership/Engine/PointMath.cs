using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Core.Membership.Engine;

/// <summary>
/// สูตรคะแนนตาม spec หัวข้อ 2 (pure C#, ไม่มี I/O) — ทุกสูตรปัดคะแนนลง (floor) และเงินปัด 2 ตำแหน่งแบบ AwayFromZero
/// </summary>
public static class PointMath
{
    /// <summary>ปัดเงิน 2 ตำแหน่ง</summary>
    /// <param name="value">ค่า</param>
    /// <returns>ค่าที่ปัดแล้ว</returns>
    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>ปัดลงเป็นจำนวนเต็ม (รองรับค่าติดลบแบบ floor จริง)</summary>
    /// <param name="value">ค่า</param>
    /// <returns>จำนวนเต็ม</returns>
    public static int Floor(decimal value) => (int)Math.Floor(value);

    /// <summary>
    /// คะแนนฐาน (spec 2.2): <c>eligible &lt; minSpend ? 0 : floor(floor(eligible / spendPerPoint) × multiplier)</c>
    /// </summary>
    /// <param name="eligible">ยอดที่ได้คะแนน (หลังส่วนลด ไม่รวมส่วนที่จ่ายด้วยคะแนน)</param>
    /// <param name="spendPerPoint">กี่บาทได้ 1 คะแนน</param>
    /// <param name="minSpend">ยอดขั้นต่ำ</param>
    /// <param name="tierMultiplier">ตัวคูณของ Tier</param>
    /// <returns>คะแนน</returns>
    public static int BasePoints(decimal eligible, decimal spendPerPoint, decimal minSpend, decimal tierMultiplier)
    {
        if (eligible <= 0 || spendPerPoint <= 0 || eligible < minSpend)
        {
            return 0;
        }

        return Floor(Floor(eligible / spendPerPoint) * tierMultiplier);
    }

    /// <summary>คะแนนพิเศษจากตัวคูณของแคมเปญ: <c>floor(eligibleₖ / spendPerPoint) × (multiplierₖ − 1)</c></summary>
    /// <param name="eligibleK">ยอดที่เข้าเงื่อนไขแคมเปญ</param>
    /// <param name="spendPerPoint">กี่บาทได้ 1 คะแนน</param>
    /// <param name="multiplier">ตัวคูณของแคมเปญ</param>
    /// <returns>คะแนน</returns>
    public static int MultiplierBonus(decimal eligibleK, decimal spendPerPoint, decimal multiplier) =>
        eligibleK <= 0 || spendPerPoint <= 0 || multiplier <= 1 ? 0 : Floor(Floor(eligibleK / spendPerPoint) * (multiplier - 1));

    /// <summary>คะแนนรวม = min(base + Σ bonus, maxPerTransaction)</summary>
    /// <param name="basePoints">คะแนนฐาน</param>
    /// <param name="bonuses">คะแนนพิเศษ</param>
    /// <param name="maxPerTransaction">เพดาน (null = ไม่จำกัด)</param>
    /// <returns>คะแนน</returns>
    public static int TotalEarn(int basePoints, int bonuses, int? maxPerTransaction)
    {
        int total = Math.Max(basePoints + bonuses, 0);
        return maxPerTransaction is int cap ? Math.Min(total, cap) : total;
    }

    /// <summary>วันหมดอายุของ lot (spec 2.3): สิ้นเดือนของ (earnedAt + months) ตามเขตเวลาธุรกิจ</summary>
    /// <param name="earnedAt">เวลาที่ได้รับ</param>
    /// <param name="months">expiry.months</param>
    /// <param name="zone">เขตเวลา</param>
    /// <returns>23:59:59 วันสุดท้ายของเดือน (เวลาท้องถิ่น) เป็น UTC</returns>
    public static DateTimeOffset ExpiryFor(DateTimeOffset earnedAt, int months, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTime local = TimeZoneInfo.ConvertTime(earnedAt, zone).DateTime;
        var target = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(months);
        DateTime endLocal = target.AddMonths(1).AddSeconds(-1);
        return new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal)).ToUniversalTime();
    }

    /// <summary>
    /// ตัดคะแนนแบบ FEFO (spec 2.4): lot ที่หมดอายุก่อนถูกใช้ก่อน ตามด้วย earnedAt และ id
    /// </summary>
    /// <param name="lots">lot ของสมาชิก</param>
    /// <param name="points">คะแนนที่ต้องตัด (&gt; 0)</param>
    /// <param name="now">เวลาปัจจุบัน (lot ที่หมดอายุแล้วไม่ถูกใช้)</param>
    /// <returns>(การตัดต่อ lot, คะแนนที่ตัดไม่ได้)</returns>
    public static (List<(PointLotDTO Lot, int Take)> Allocations, int Shortfall) AllocateFefo(IEnumerable<PointLotDTO> lots, int points, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lots);
        var result = new List<(PointLotDTO, int)>();
        int need = points;
        foreach (PointLotDTO lot in lots.Where(l => l.Remaining > 0 && l.ExpiresAt > now).OrderBy(l => l.ExpiresAt).ThenBy(l => l.EarnedAt).ThenBy(l => l.Id))
        {
            if (need <= 0)
            {
                break;
            }

            int take = Math.Min(lot.Remaining, need);
            result.Add((lot, take));
            need -= take;
        }

        return (result, Math.Max(need, 0));
    }

    /// <summary>
    /// ย้อนคะแนนตามสัดส่วนสะสม (spec 2.6): <c>reverseₖ = floor(E × Rₖ / B) − Σ reverseⱼ</c> — คืนครบทั้งบิล รวมเท่ากับ E พอดี
    /// </summary>
    /// <param name="total">E (คะแนนที่ได้) หรือ pointsUsed (คะแนนที่จ่าย)</param>
    /// <param name="cumulativeReturned">Rₖ สะสมหลังครั้งนี้</param>
    /// <param name="baseAmount">B</param>
    /// <param name="alreadyReversed">Σ reverseⱼ ก่อนหน้า</param>
    /// <returns>คะแนนที่ต้องย้อนครั้งนี้ (≥ 0)</returns>
    public static int CumulativeReverse(int total, decimal cumulativeReturned, decimal baseAmount, int alreadyReversed)
    {
        if (total <= 0)
        {
            return 0;
        }

        if (baseAmount <= 0 || cumulativeReturned >= baseAmount)
        {
            return Math.Max(total - alreadyReversed, 0);
        }

        int target = Floor(total * cumulativeReturned / baseAmount);
        return Math.Max(target - alreadyReversed, 0);
    }

    /// <summary>
    /// กระจายยอด (ส่วนลดท้ายบิล) ลงรายการตามสัดส่วน (spec 2.1) — บรรทัดสุดท้ายที่มียอดรับเศษ ผลรวมตรงพอดี
    /// </summary>
    /// <param name="amount">ยอดที่กระจาย</param>
    /// <param name="weights">น้ำหนักแต่ละบรรทัด (lineNet)</param>
    /// <returns>ยอดต่อบรรทัด</returns>
    public static decimal[] Allocate(decimal amount, IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var result = new decimal[weights.Count];
        decimal sum = weights.Where(w => w > 0).Sum();
        if (amount == 0 || sum <= 0)
        {
            return result;
        }

        int last = -1;
        for (int i = weights.Count - 1; i >= 0; i--)
        {
            if (weights[i] > 0)
            {
                last = i;
                break;
            }
        }

        decimal allocated = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0)
            {
                continue;
            }

            if (i == last)
            {
                result[i] = amount - allocated;
            }
            else
            {
                result[i] = Money(amount * weights[i] / sum);
                allocated += result[i];
            }
        }

        return result;
    }

    /// <summary>เริ่มต้นวัน (เวลาท้องถิ่น) ของเวลาที่กำหนด</summary>
    /// <param name="at">เวลา</param>
    /// <param name="zone">เขตเวลา</param>
    /// <returns>วันที่ท้องถิ่น</returns>
    public static DateOnly LocalDate(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>หาเขตเวลาจาก IANA id (ไม่พบ = UTC+7)</summary>
    /// <param name="id">IANA id</param>
    /// <returns>เขตเวลา</returns>
    public static TimeZoneInfo Zone(string? id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? "Asia/Bangkok" : id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("UTC+7", TimeSpan.FromHours(7), "UTC+7", "UTC+7");
        }
    }
}
