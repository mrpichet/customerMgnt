using System.Security.Cryptography;
using System.Text;
using CustomerMgnt.Api.Controllers;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Services;
using CustomerMgnt.Integrations;

namespace CustomerMgnt.Tests.Api;

[TestClass]
public sealed class WebhookAndGatewayTests
{
    private const string Secret = "test-channel-secret";

    [TestMethod]
    public void LineSignature_ValidAndTampered()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"events\":[]}");
        string sig = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));
        Assert.IsTrue(WebhooksController.VerifyBase64Signature(body, sig, Secret));
        Assert.IsFalse(WebhooksController.VerifyBase64Signature(Encoding.UTF8.GetBytes("{\"events\":[1]}"), sig, Secret));
        Assert.IsFalse(WebhooksController.VerifyBase64Signature(body, "not-base64!", Secret));
        Assert.IsFalse(WebhooksController.VerifyBase64Signature(body, sig, "other-secret"));
    }

    [TestMethod]
    public void HexSignature_Valid()
    {
        byte[] body = Encoding.UTF8.GetBytes("hello");
        string hex = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));
        Assert.IsTrue(WebhooksController.VerifyHexSignature(body, hex, Secret));
        Assert.IsFalse(WebhooksController.VerifyHexSignature(body, hex[..^2] + "00", Secret));
        Assert.IsFalse(WebhooksController.VerifyHexSignature(body, "zz", Secret));
    }

    [TestMethod]
    public void ParseLineEvents_KeepsOnlyRecentTextMessages()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);
        string json = $$$"""
            {"destination":"x","events":[
              {"type":"message","timestamp":{{{now.ToUnixTimeMilliseconds()}}},"source":{"type":"user","userId":"U1"},"message":{"type":"text","id":"m1","text":"สวัสดีครับ"}},
              {"type":"message","timestamp":{{{now.ToUnixTimeMilliseconds()}}},"source":{"type":"user","userId":"U1"},"message":{"type":"sticker","id":"m2"}},
              {"type":"follow","timestamp":{{{now.ToUnixTimeMilliseconds()}}},"source":{"type":"user","userId":"U2"}},
              {"type":"message","timestamp":{{{now.AddDays(-1).ToUnixTimeMilliseconds()}}},"source":{"type":"user","userId":"U3"},"message":{"type":"text","id":"m3","text":"old"}},
              {"message":{"type":"text"}},
              42
            ]}
            """;
        InboundMessageRequest m = WebhooksController.ParseLineEvents(Encoding.UTF8.GetBytes(json), now, 300).Single();
        Assert.AreEqual("U1", m.ExternalUserId);
        Assert.AreEqual("m1", m.ExternalMessageId);
        Assert.AreEqual("สวัสดีครับ", m.Body);
        Assert.AreEqual(ChannelKind.Line, m.Channel);
    }

    [TestMethod]
    [DataRow(ChannelKind.Line, "Line")]
    [DataRow(ChannelKind.Facebook, "FacebookMessenger")]
    [DataRow(ChannelKind.Teams, "TeamsChat")]
    [DataRow(ChannelKind.WebChat, null)]
    [DataRow(ChannelKind.Phone, null)]
    public void MessageHub_ChannelMapping(ChannelKind channel, string? expected) => Assert.AreEqual(expected, MessageHubGateway.MapChannel(channel));

    [TestMethod]
    [DataRow("must", "Critical")]
    [DataRow("Should", "High")]
    [DataRow("could", "Low")]
    [DataRow(null, "Medium")]
    public void RequirementPriority_Normalized(string? input, string expected) => Assert.AreEqual(expected, RequirementMgntGateway.NormalizePriority(input));

    [TestMethod]
    public void RelationshipPeriods_Overlap()
    {
        var jan = new DateOnly(2026, 1, 1);
        Assert.IsTrue(RelationshipService.Overlaps(jan, null, jan.AddMonths(6), null));
        Assert.IsTrue(RelationshipService.Overlaps(jan, jan.AddMonths(3), jan.AddMonths(3), null), "same-day boundary overlaps");
        Assert.IsFalse(RelationshipService.Overlaps(jan, jan.AddMonths(3), jan.AddMonths(3).AddDays(1), null));
    }
}
