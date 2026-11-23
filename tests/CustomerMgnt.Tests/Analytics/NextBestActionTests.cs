using CustomerMgnt.Core.Analytics;
using CustomerMgnt.Core.Domain;

namespace CustomerMgnt.Tests.Analytics;

[TestClass]
public sealed class NextBestActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 3, 0, 0, TimeSpan.Zero);

    private static NbaSignals Signals(ChurnRiskDTO? churn = null, IReadOnlyList<ContractDTO>? contracts = null, IReadOnlyList<ConversationDTO>? conversations = null,
        DateTimeOffset? lastContact = null, RecommendationDTO? rec = null, bool consent = false) =>
        new(new CustomerDTO { Id = 5, Status = CustomerStatus.Active, MarketingConsent = consent }, churn, [], [], [], conversations ?? [], contracts ?? [], lastContact ?? Now.AddDays(-3), rec, ChannelKind.Email, Now.AddHours(5));

    [TestMethod]
    public void HealthyCustomer_NoAction()
    {
        IReadOnlyList<NextBestActionDTO> a = NextBestActionService.Decide(Signals(), Now);
        Assert.AreEqual("No action needed", a.Single().Action);
    }

    [TestMethod]
    public void UnhappyChat_ComesFirst_ThenRetention()
    {
        var conv = new ConversationDTO { Id = 9, Status = ConversationStatus.Open, NeedsAttention = true, LastSentiment = -0.7, Channel = ChannelKind.Line };
        var churn = new ChurnRiskDTO(5, "X", 0.6, "High", [new FactorDTO("complaints", 3, 0.5)], "call");
        IReadOnlyList<NextBestActionDTO> a = NextBestActionService.Decide(Signals(churn, conversations: [conv]), Now);
        Assert.AreEqual("Respond to unhappy customer conversation", a[0].Action);
        Assert.AreEqual(ChannelKind.Line, a[0].Channel);
        Assert.IsTrue(a.Any(x => x.Action.StartsWith("Retention", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ExpiringContract_SuggestsRenewalWithUpsell()
    {
        var contract = new ContractDTO { ContractNo = "MA-1", Status = ContractStatus.Active, EndDate = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(20), AnnualValue = 100_000 };
        var rec = new RecommendationDTO("CLOUD", "Cloud backup", CatalogItemKind.Service, 0.8, "similar customers");
        NextBestActionDTO a = NextBestActionService.Decide(Signals(contracts: [contract], rec: rec), Now)[0];
        Assert.AreEqual("Start MA renewal discussion", a.Action);
        Assert.AreEqual(1, a.Priority);
        Assert.AreEqual("Cloud backup", a.Offer);
    }

    [TestMethod]
    public void MarketingOffer_RequiresConsent()
    {
        var rec = new RecommendationDTO("X", "Promo", CatalogItemKind.Promotion, 0.5, "r");
        Assert.IsFalse(NextBestActionService.Decide(Signals(rec: rec, consent: false), Now).Any(a => a.Action.StartsWith("Offer", StringComparison.Ordinal)));
        Assert.IsTrue(NextBestActionService.Decide(Signals(rec: rec, consent: true), Now).Any(a => a.Action.StartsWith("Offer", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LongSilence_SendsCheckInAtOptimalTime()
    {
        NextBestActionDTO a = NextBestActionService.Decide(Signals(lastContact: Now.AddDays(-90)), Now).Single(x => x.Action == "Send personalised check-in");
        Assert.AreEqual(Now.AddHours(5), a.SuggestedAt);
    }
}
