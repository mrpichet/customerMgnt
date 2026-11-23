using CustomerMgnt.Core.Analytics;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Services;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Tests.Analytics;

[TestClass]
public sealed class SentimentTests
{
    private readonly SentimentAnalysisService _svc = new(Options.Create(new CustomerMgntOptions()));

    [TestMethod]
    [DataRow("ระบบล่มอีกแล้ว แย่มาก ช้ามาก")]
    [DataRow("This is terrible, the system is down again and support is slow")]
    public void Negative(string text) => Assert.AreEqual(SentimentLabel.Negative, _svc.Analyze(text).Label);

    [TestMethod]
    [DataRow("ขอบคุณมากครับ บริการดีมาก ประทับใจ")]
    [DataRow("Thank you, great service, very happy")]
    public void Positive(string text) => Assert.AreEqual(SentimentLabel.Positive, _svc.Analyze(text).Label);

    [TestMethod]
    public void Negation_FlipsPolarity() => Assert.AreNotEqual(SentimentLabel.Positive, _svc.Analyze("ไม่ดีเลย").Label);

    [TestMethod]
    public void Empty_IsNeutral() => Assert.AreEqual(SentimentLabel.Neutral, _svc.Analyze("   ").Label);

    [TestMethod]
    public void LegalThreat_NeedsAttention() => Assert.IsTrue(_svc.Analyze("จะฟ้องร้องและยกเลิกสัญญา").NeedsAttention);
}
