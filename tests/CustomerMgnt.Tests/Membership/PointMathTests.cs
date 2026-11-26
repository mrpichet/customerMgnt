using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Tests.Membership;

/// <summary>สูตรคะแนนตาม spec หัวข้อ 2 (ตัวอย่างใน spec ต้องได้ผลตรงทุกตัว)</summary>
[TestClass]
public sealed class PointMathTests
{
    [TestMethod]
    public void BasePoints_SpecExample_Gold_Is132()
    {
        // spec 2.2: eligible = 2,250 − 50 = 2,200 → floor(2,200/25) = 88 → 88 × 1.5 = 132
        Assert.AreEqual(132, PointMath.BasePoints(2200m, 25m, 0m, 1.5m));
    }

    [TestMethod]
    public void BasePoints_FloorsBeforeAndAfterMultiplier()
    {
        Assert.AreEqual(3, PointMath.BasePoints(99.99m, 25m, 0m, 1m));     // floor(3.9996)
        Assert.AreEqual(4, PointMath.BasePoints(75m, 25m, 0m, 1.5m));      // floor(3 × 1.5 = 4.5)
    }

    [TestMethod]
    public void BasePoints_BelowMinSpendOrInvalid_IsZero()
    {
        Assert.AreEqual(0, PointMath.BasePoints(199m, 25m, 200m, 1m));
        Assert.AreEqual(0, PointMath.BasePoints(0m, 25m, 0m, 1m));
        Assert.AreEqual(0, PointMath.BasePoints(500m, 0m, 0m, 1m));
        Assert.AreEqual(8, PointMath.BasePoints(200m, 25m, 200m, 1m));     // เท่ากับขั้นต่ำพอดีได้คะแนน
    }

    [TestMethod]
    public void MultiplierBonus_IsExtraOverBase()
    {
        Assert.AreEqual(40, PointMath.MultiplierBonus(1000m, 25m, 2m));   // 40 × (2 − 1)
        Assert.AreEqual(0, PointMath.MultiplierBonus(1000m, 25m, 1m));
    }

    [TestMethod]
    public void TotalEarn_AppliesCap()
    {
        Assert.AreEqual(150, PointMath.TotalEarn(132, 18, null));
        Assert.AreEqual(100, PointMath.TotalEarn(132, 18, 100));
    }

    [TestMethod]
    public void CumulativeReverse_SpecExample_30Then102()
    {
        // spec 2.6: E = 132, B = 2,200 → คืน 500 = 30, คืนอีก 1,700 (สะสม 2,200) = 102 → รวม 132
        int first = PointMath.CumulativeReverse(132, 500m, 2200m, 0);
        int second = PointMath.CumulativeReverse(132, 2200m, 2200m, first);
        Assert.AreEqual(30, first);
        Assert.AreEqual(102, second);
        Assert.AreEqual(132, first + second);
    }

    [TestMethod]
    public void CumulativeReverse_ManySmallReturns_SumToTotal()
    {
        int reversed = 0;
        for (int k = 1; k <= 7; k++)
        {
            reversed += PointMath.CumulativeReverse(132, Math.Min(k * 333m, 2200m), 2200m, reversed);
        }

        Assert.AreEqual(132, reversed);
    }

    [TestMethod]
    public void Allocate_ProportionalAndLastLineTakesRemainder()
    {
        decimal[] a = PointMath.Allocate(100m, [900m, 1450m]);
        Assert.AreEqual(38.30m, a[0]);
        Assert.AreEqual(61.70m, a[1]);
        Assert.AreEqual(100m, a.Sum());
    }

    [TestMethod]
    public void Allocate_SkipsZeroWeightLines()
    {
        decimal[] a = PointMath.Allocate(10m, [3m, 3m, 3m, 0m]);
        Assert.AreEqual(3.33m, a[0]);
        Assert.AreEqual(3.34m, a[2]);
        Assert.AreEqual(0m, a[3]);
        Assert.AreEqual(10m, a.Sum());
    }

    [TestMethod]
    public void ExpiryFor_EndOfMonthInBusinessZone()
    {
        TimeZoneInfo bkk = PointMath.Zone("Asia/Bangkok");
        DateTimeOffset expires = PointMath.ExpiryFor(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(7)), 24, bkk);
        Assert.AreEqual(new DateTimeOffset(2028, 11, 30, 16, 59, 59, TimeSpan.Zero), expires);
    }

    [TestMethod]
    public void ExpiryFor_UsesLocalDateNotUtcDate()
    {
        // 2026-11-30 18:00Z = 2026-12-01 01:00 เวลาไทย → นับจากธันวาคม
        TimeZoneInfo bkk = PointMath.Zone("Asia/Bangkok");
        DateTimeOffset expires = PointMath.ExpiryFor(new DateTimeOffset(2026, 11, 30, 18, 0, 0, TimeSpan.Zero), 1, bkk);
        Assert.AreEqual(new DateTimeOffset(2027, 1, 31, 16, 59, 59, TimeSpan.Zero), expires);
    }

    [TestMethod]
    public void AllocateFefo_ExpiringFirst_SkipsExpired()
    {
        var now = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
        PointLotDTO expired = Lot(1, 500, now.AddDays(-1), now.AddMonths(-20));
        PointLotDTO late = Lot(2, 100, now.AddMonths(12), now.AddMonths(-1));
        PointLotDTO soon = Lot(3, 80, now.AddDays(10), now.AddMonths(-23));

        (List<(PointLotDTO Lot, int Take)> alloc, int shortfall) = PointMath.AllocateFefo([expired, late, soon], 150, now);

        Assert.AreEqual(0, shortfall);
        Assert.AreEqual(2, alloc.Count);
        Assert.AreEqual((3L, 80), (alloc[0].Lot.Id, alloc[0].Take));
        Assert.AreEqual((2L, 70), (alloc[1].Lot.Id, alloc[1].Take));
    }

    [TestMethod]
    public void AllocateFefo_ReportsShortfall()
    {
        var now = DateTimeOffset.UtcNow;
        (_, int shortfall) = PointMath.AllocateFefo([Lot(1, 50, now.AddDays(5), now)], 80, now);
        Assert.AreEqual(30, shortfall);
    }

    [TestMethod]
    public void Money_RoundsAwayFromZero()
    {
        Assert.AreEqual(0.13m, PointMath.Money(0.125m));
        Assert.AreEqual(2.68m, PointMath.Money(2.675m));
    }

    private static PointLotDTO Lot(long id, int remaining, DateTimeOffset expiresAt, DateTimeOffset earnedAt) =>
        new() { Id = id, Points = remaining, Remaining = remaining, ExpiresAt = expiresAt, EarnedAt = earnedAt };
}
