using System.Text.Json;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Tests.Membership;

/// <summary>กฎสถานะสมาชิก/แคมเปญ, Tier และเงื่อนไขกลุ่มเป้าหมาย</summary>
[TestClass]
public sealed class MembershipRulesTests
{
    [TestMethod]
    [DataRow("081-234-5678", "0812345678")]
    [DataRow("+66 81 234 5678", "0812345678")]
    [DataRow("02 123 4567", "021234567")]
    public void NormalizePhone_ThaiFormats(string input, string expected) => Assert.AreEqual(expected, MemberRules.NormalizePhone(input));

    [TestMethod]
    [DataRow("08123x5678")]
    [DataRow("12345")]
    [DataRow("1812345678")]
    public void NormalizePhone_Invalid_Throws(string input) => Assert.ThrowsExactly<ValidationException>(() => MemberRules.NormalizePhone(input));

    [TestMethod]
    public void NormalizeAndMaskEmail()
    {
        Assert.AreEqual("somchai@example.com", MemberRules.NormalizeEmail("  SomChai@Example.com "));
        Assert.IsNull(MemberRules.NormalizeEmail(" "));
        Assert.AreEqual("s***@example.com", MemberRules.MaskEmail("somchai@example.com"));
        Assert.AreEqual("0xx-xxx-5678", MemberRules.MaskPhone("5678"));
    }

    [TestMethod]
    public void Age_CountsBirthdayInYear()
    {
        Assert.AreEqual(30, MemberRules.Age(1996, "11-26", new DateOnly(2026, 11, 26)));
        Assert.AreEqual(29, MemberRules.Age(1996, "11-27", new DateOnly(2026, 11, 26)));
        Assert.IsNull(MemberRules.Age(null, "01-01", new DateOnly(2026, 1, 1)));
    }

    [TestMethod]
    public void MemberStatus_Transitions()
    {
        Assert.IsTrue(MemberRules.CanTransition(MemberStatus.PendingVerification, MemberStatus.Active));
        Assert.IsTrue(MemberRules.CanTransition(MemberStatus.Suspended, MemberStatus.Active));
        Assert.IsFalse(MemberRules.CanTransition(MemberStatus.Closed, MemberStatus.Active));
        Assert.IsFalse(MemberRules.CanTransition(MemberStatus.PendingVerification, MemberStatus.Suspended));
    }

    [TestMethod]
    public void CampaignStatus_Transitions()
    {
        Assert.IsTrue(CampaignRules.CanTransition(CampaignStatus.Draft, CampaignStatus.PendingApproval));
        Assert.IsTrue(CampaignRules.CanTransition(CampaignStatus.Active, CampaignStatus.Paused));
        Assert.IsFalse(CampaignRules.CanTransition(CampaignStatus.Draft, CampaignStatus.Active));
        Assert.IsFalse(CampaignRules.CanTransition(CampaignStatus.Ended, CampaignStatus.Active));
    }

    [TestMethod]
    public void Campaign_ApprovalLevels_SecondLevelOverBudget()
    {
        var rules = new MembershipRulesDTO { ApprovalLevels = 1, SecondLevelBudget = 1_000_000m };
        Assert.AreEqual(1, CampaignRules.ApprovalLevels(new CampaignDTO { Limits = new CampaignLimitsDTO { Budget = 200_000m } }, rules));
        Assert.AreEqual(2, CampaignRules.ApprovalLevels(new CampaignDTO { Limits = new CampaignLimitsDTO { Budget = 1_500_000m } }, rules));
    }

    [TestMethod]
    public void Campaign_RequiresReapproval_OnlyWhenTermsChange()
    {
        var before = new CampaignDTO { Name = "A", RewardType = RewardType.PercentDiscount, Reward = new CampaignRewardDTO { Percent = 10 } };
        CampaignDTO renamed = before.Clone();
        renamed.Name = "B";
        CampaignDTO moreDiscount = before.Clone();
        moreDiscount.Reward.Percent = 20;

        Assert.IsFalse(CampaignRules.RequiresReapproval(before, renamed));
        Assert.IsTrue(CampaignRules.RequiresReapproval(before, moreDiscount));
    }

    [TestMethod]
    public void Campaign_Validate_RejectsBadPeriodAndReward()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.ThrowsExactly<ValidationException>(() => CampaignRules.Validate(new CampaignDTO
        {
            StartAt = now, EndAt = now.AddDays(-1), RewardType = RewardType.PercentDiscount, Reward = new CampaignRewardDTO { Percent = 10 },
        }));
        Assert.ThrowsExactly<ValidationException>(() => CampaignRules.Validate(new CampaignDTO
        {
            StartAt = now, EndAt = now.AddDays(1), RewardType = RewardType.PercentDiscount, Reward = new CampaignRewardDTO { Percent = 0 },
        }));
    }

    [TestMethod]
    public void Tier_Review_UpgradeRetainDowngrade()
    {
        TierDTO[] tiers =
        [
            new() { Code = "SILVER", Rank = 1, UpgradeThreshold = 0, RetainThreshold = 0 },
            new() { Code = "GOLD", Rank = 2, UpgradeThreshold = 20_000, RetainThreshold = 15_000 },
            new() { Code = "PLATINUM", Rank = 3, UpgradeThreshold = 80_000, RetainThreshold = 60_000 },
        ];

        Assert.AreEqual("SILVER", TierRules.Initial(tiers)?.Code);
        Assert.AreEqual(("PLATINUM", "Upgrade"), Code(TierRules.Review(tiers, "SILVER", 90_000)));
        Assert.AreEqual(("GOLD", "Retain"), Code(TierRules.Review(tiers, "GOLD", 15_000)));
        Assert.AreEqual(("SILVER", "Downgrade"), Code(TierRules.Review(tiers, "GOLD", 14_999)));
        Assert.AreEqual(("SILVER", "Upgrade"), Code(TierRules.Review(tiers, null, 0))); // ยังไม่มี Tier = เข้า Tier แรกที่ถึงเกณฑ์
    }

    [TestMethod]
    public void Segment_Matches_AndOr()
    {
        SegmentRuleDTO rule = Group("And",
            Cond("tierCode", "In", "[\"GOLD\",\"PLATINUM\"]"),
            Group("Or", Cond("frequency12m", "Gte", "6"), Cond("spend12m", "Between", "[50000, 100000]")));
        SegmentRuleEvaluator.Validate(rule);

        Assert.IsTrue(SegmentRuleEvaluator.Matches(rule, new MemberFactsDTO { TierCode = "GOLD", Frequency12m = 6 }));
        Assert.IsTrue(SegmentRuleEvaluator.Matches(rule, new MemberFactsDTO { TierCode = "PLATINUM", Frequency12m = 1, Spend12m = 60_000 }));
        Assert.IsFalse(SegmentRuleEvaluator.Matches(rule, new MemberFactsDTO { TierCode = "SILVER", Frequency12m = 10 }));
        Assert.IsFalse(SegmentRuleEvaluator.Matches(rule, new MemberFactsDTO { TierCode = "GOLD", Frequency12m = 2, Spend12m = 10_000 }));
    }

    [TestMethod]
    public void Segment_Validate_RejectsUnknownFieldOperatorAndShape()
    {
        Assert.ThrowsExactly<ValidationException>(() => SegmentRuleEvaluator.Validate(Cond("salary", "Gt", "1")));
        Assert.ThrowsExactly<ValidationException>(() => SegmentRuleEvaluator.Validate(Cond("spend12m", "Like", "1")));
        Assert.ThrowsExactly<ValidationException>(() => SegmentRuleEvaluator.Validate(Cond("tierCode", "In", "\"GOLD\"")));
        Assert.ThrowsExactly<ValidationException>(() => SegmentRuleEvaluator.Validate(Cond("spend12m", "Between", "[1]")));

        SegmentRuleDTO deep = Cond("spend12m", "Gt", "1");
        for (int i = 0; i < 6; i++)
        {
            deep = Group("And", deep);
        }

        Assert.ThrowsExactly<ValidationException>(() => SegmentRuleEvaluator.Validate(deep));
    }

    private static (string?, string) Code((TierDTO? Tier, string Reason) r) => (r.Tier?.Code, r.Reason);

    private static SegmentRuleDTO Group(string op, params SegmentRuleDTO[] items) => new() { Op = op, Items = [.. items] };

    private static SegmentRuleDTO Cond(string field, string op, string json) =>
        new() { Field = field, Operator = op, Value = JsonDocument.Parse(json).RootElement.Clone() };
}
