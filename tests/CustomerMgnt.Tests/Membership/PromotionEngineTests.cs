using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Tests.Membership;

/// <summary>คำนวณตะกร้า (quote): ส่วนลด → ส่วนลดท้ายบิล → จ่ายด้วยคะแนน → คะแนนที่ได้</summary>
[TestClass]
public sealed class PromotionEngineTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 5, 0, 0, TimeSpan.Zero); // 12:00 เวลาไทย วันพฤหัสบดี

    private static readonly TierDTO Gold = new() { Id = 2, Code = "GOLD", Name = "Gold", Rank = 2, PointMultiplier = 1.5m };

    private static readonly EarnRuleDTO Rule = new() { Id = 1, Code = "STD", SpendPerPoint = 25m, Status = EarnRuleStatus.Active };

    [TestMethod]
    public void SpecExample_Gold2350_BillDiscount100_Points200_Earns132()
    {
        QuoteDTO q = PromotionEngine.Calculate(Context(Basket(billDiscount: 100m, points: 200)));

        Assert.AreEqual(2350m, q.Subtotal);
        Assert.AreEqual(100m, q.BillDiscount);
        Assert.AreEqual(200, q.PointsRedeemed);
        Assert.AreEqual(50m, q.PointsTender);
        Assert.AreEqual(2200m, q.NetPayable);
        Assert.AreEqual(2200m, q.Eligible);
        Assert.AreEqual(132, q.PointsToEarn);
        Assert.AreEqual(100m, q.Lines.Sum(l => l.AllocatedBillDiscount));
    }

    [TestMethod]
    public void PercentDiscount_AppliedBeforeBillDiscountAndPoints()
    {
        CampaignDTO c = Campaign(10, RewardType.PercentDiscount, r => r.Percent = 10);
        QuoteDTO q = PromotionEngine.Calculate(Context(Basket(billDiscount: 100m, points: 200), c));

        Assert.AreEqual(235m, q.PromotionDiscount);
        Assert.AreEqual(1965m, q.NetPayable);              // 2,350 − 235 − 100 − 50
        Assert.AreEqual(117, q.PointsToEarn);               // floor(1,965 / 25) = 78 → 78 × 1.5 = 117
        Assert.AreEqual(1, q.Benefits.Count);
        Assert.AreEqual("C10", q.Benefits[0].CampaignCode);
    }

    [TestMethod]
    public void PercentDiscount_RespectsCap()
    {
        CampaignDTO c = Campaign(10, RewardType.PercentDiscount, r => { r.Percent = 10; r.Cap = 100; });
        QuoteDTO q = PromotionEngine.Calculate(Context(Basket(), c));
        Assert.AreEqual(100m, q.PromotionDiscount);
    }

    [TestMethod]
    public void CampaignOutsidePeriod_OrOtherTier_NotApplied()
    {
        CampaignDTO ended = Campaign(1, RewardType.AmountDiscount, r => r.Amount = 50);
        ended.EndAt = At.AddMinutes(-1);
        CampaignDTO platinumOnly = Campaign(2, RewardType.AmountDiscount, r => r.Amount = 50);
        platinumOnly.Targets = new CampaignTargetsDTO { AllMembers = false, TierCodes = ["PLATINUM"] };

        QuoteDTO q = PromotionEngine.Calculate(Context(Basket(), ended, platinumOnly));

        Assert.AreEqual(0m, q.PromotionDiscount);
        Assert.AreEqual(0, q.Benefits.Count);
    }

    [TestMethod]
    public void PointsTender_LimitedByMaxPointsPayPercent()
    {
        QuoteDTO q = PromotionEngine.Calculate(Context(Basket(points: 10_000), pointsAvailable: 20_000));

        // 50% ของ 2,350 = 1,175 บาท → 4,700 คะแนน
        Assert.AreEqual(4700, q.PointsRedeemed);
        Assert.AreEqual(1175m, q.PointsTender);
        Assert.AreEqual("max_points_pay_percent", q.PointsAdjustedReason);
    }

    [TestMethod]
    public void PointsTender_LimitedByAvailablePoints()
    {
        QuoteDTO q = PromotionEngine.Calculate(Context(Basket(points: 200), pointsAvailable: 120));
        Assert.AreEqual(120, q.PointsRedeemed);
        Assert.AreEqual(30m, q.PointsTender);
        Assert.AreEqual("insufficient_points", q.PointsAdjustedReason);
    }

    [TestMethod]
    public void NonEarningItem_ExcludedFromEarnBase()
    {
        QuoteRequest req = Basket();
        req.Lines.Add(new QuoteLineRequest { LineNo = 3, ItemCode = "GIFTCARD", Qty = 1, UnitPrice = 1000m });
        QuoteDTO q = PromotionEngine.Calculate(Context(req));

        Assert.AreEqual(3350m, q.NetPayable);
        Assert.AreEqual(2350m, q.Eligible);
        Assert.AreEqual(141, q.PointsToEarn);               // floor(2,350 / 25) = 94 → × 1.5 = 141
        Assert.IsFalse(q.Lines.Single(l => l.LineNo == 3).Eligible);
    }

    [TestMethod]
    public void NoEarnRule_EarnsNothing()
    {
        BasketContext ctx = Context(Basket());
        ctx = new BasketContext { Request = ctx.Request, Member = ctx.Member, Tier = Gold, Rules = ctx.Rules, At = At, EarnRule = null };
        Assert.AreEqual(0, PromotionEngine.Calculate(ctx).PointsToEarn);
    }

    [TestMethod]
    public void BonusPointsCampaign_AddsToEarn_AndMaxPerTransactionCaps()
    {
        CampaignDTO bonus = Campaign(5, RewardType.BonusPoints, r => r.BonusPoints = 50);
        QuoteDTO q = PromotionEngine.Calculate(Context(Basket(), bonus));
        Assert.AreEqual(141 + 50, q.PointsToEarn);

        var capped = new EarnRuleDTO { Id = 2, Code = "CAP", SpendPerPoint = 25m, MaxPointsPerTransaction = 100, Status = EarnRuleStatus.Active };
        BasketContext ctx = Context(Basket(), bonus);
        ctx = new BasketContext { Request = ctx.Request, Member = ctx.Member, Tier = Gold, Rules = ctx.Rules, At = At, Campaigns = ctx.Campaigns, EarnRule = capped };
        Assert.AreEqual(100, PromotionEngine.Calculate(ctx).PointsToEarn);
    }

    [TestMethod]
    public void CouponOverLimitPerBill_IsRejected()
    {
        CampaignDTO c1 = Campaign(21, RewardType.AmountDiscount, r => r.Amount = 50);
        c1.RequiresCoupon = true;
        CampaignDTO c2 = Campaign(22, RewardType.AmountDiscount, r => r.Amount = 80);
        c2.RequiresCoupon = true;
        BasketContext baseCtx = Context(Basket());
        var ctx = new BasketContext
        {
            Request = baseCtx.Request, Member = baseCtx.Member, Tier = Gold, Rules = baseCtx.Rules, At = At, EarnRule = Rule,
            Coupons = [new CouponCandidate { Code = "A-1", Campaign = c1 }, new CouponCandidate { Code = "B-2", Campaign = c2 }],
        };

        QuoteDTO q = PromotionEngine.Calculate(ctx);

        Assert.AreEqual(50m, q.CouponDiscount);
        Assert.IsTrue(q.RejectedCoupons.Any(r => r.Code == "B-2" && r.Reason == "coupon_limit_per_bill"));
    }

    private static QuoteRequest Basket(decimal billDiscount = 0m, int points = 0) => new()
    {
        MemberNo = "M2600001",
        BranchCode = "BKK01",
        Channel = SalesChannel.Pos,
        Lines =
        [
            new QuoteLineRequest { LineNo = 1, ItemCode = "SKU-1", Category = "FOOD", Qty = 2, UnitPrice = 450m },
            new QuoteLineRequest { LineNo = 2, ItemCode = "SKU-2", Category = "HOME", Qty = 1, UnitPrice = 1450m },
        ],
        BillDiscount = billDiscount,
        PointsToRedeem = points,
    };

    private static BasketContext Context(QuoteRequest req, params CampaignDTO[] campaigns) => Context(req, 1320, campaigns);

    private static BasketContext Context(QuoteRequest req, int pointsAvailable, params CampaignDTO[] campaigns) => new()
    {
        Request = req,
        Member = new MemberEntity { Id = 1, MemberNo = req.MemberNo, TierCode = "GOLD" },
        Tier = Gold,
        Campaigns = campaigns,
        EarnRule = Rule,
        Rules = new MembershipRulesDTO(),
        PointsAvailable = pointsAvailable,
        At = At,
        Zone = PointMath.Zone("Asia/Bangkok"),
    };

    private static CampaignDTO Campaign(long id, RewardType type, Action<CampaignRewardDTO> reward)
    {
        var c = new CampaignDTO
        {
            Id = id,
            Code = "C" + id,
            Name = "Campaign " + id,
            StartAt = At.AddDays(-1),
            EndAt = At.AddDays(30),
            RewardType = type,
            Level = PromotionLevel.Bill,
            Status = CampaignStatus.Active,
        };
        reward(c.Reward);
        return c;
    }
}
