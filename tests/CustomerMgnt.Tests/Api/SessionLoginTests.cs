using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CustomerMgnt.Api.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CustomerMgnt.Tests.Api;

/// <summary>/api/session/login ส่งต่อไป AuthServer <c>api/v1/auth/login</c> (แทน AuthServer ด้วย handler จำลอง)</summary>
[TestClass]
public sealed class SessionLoginTests
{
    private static WebApplicationFactory<Program> _factory = default!;
    private static readonly FakeAuthServer AuthServer = new();

    public TestContext TestContext { get; set; } = default!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Analytics:RefreshInitialDelaySeconds", "3600");
            b.UseSetting("AuthServer:Authority", "https://auth.test");
            b.UseSetting("AuthServer:LoginClientId", "customermgnt-web");
            b.ConfigureTestServices(s => s.AddHttpClient(SessionController.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => AuthServer));
        });
    }

    [ClassCleanup]
    public static void Cleanup() => _factory.Dispose();

    [TestMethod]
    public async Task Login_CallsV1_AndReturnsToken()
    {
        using HttpClient c = _factory.CreateClient();
        using HttpResponseMessage r = await c.PostAsJsonAsync("/api/session/login", new { login = "somchai", password = "Correct-Pass1!" }, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.AreEqual("/api/v1/auth/login", AuthServer.LastPath);
        using JsonDocument sent = JsonDocument.Parse(AuthServer.LastBody!);
        Assert.AreEqual("somchai", sent.RootElement.GetProperty("userName").GetString());
        Assert.AreEqual("customermgnt-web", sent.RootElement.GetProperty("clientId").GetString());
        using JsonDocument body = JsonDocument.Parse(await r.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual("token-abc", body.RootElement.GetProperty("accessToken").GetString());
        Assert.IsTrue(body.RootElement.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(9));
    }

    [TestMethod]
    public async Task Login_Mfa_Returns401WithCode_ThenSucceedsWithOtp()
    {
        using HttpClient c = _factory.CreateClient();
        using HttpResponseMessage first = await c.PostAsJsonAsync("/api/session/login", new { login = "mfa-user", password = "Correct-Pass1!" }, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, first.StatusCode);
        StringAssert.Contains(await first.Content.ReadAsStringAsync(TestContext.CancellationToken), "\"code\":\"mfa_required\"");

        using HttpResponseMessage second = await c.PostAsJsonAsync("/api/session/login", new { login = "mfa-user", password = "Correct-Pass1!", otpCode = "123456" }, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
    }

    [TestMethod]
    public async Task Login_WrongPassword_Generic401()
    {
        using HttpClient c = _factory.CreateClient();
        using HttpResponseMessage r = await c.PostAsJsonAsync("/api/session/login", new { login = "somchai", password = "wrong" }, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, r.StatusCode);
        string body = await r.Content.ReadAsStringAsync(TestContext.CancellationToken);
        Assert.IsFalse(body.Contains("mfa_required", StringComparison.Ordinal));
    }

    /// <summary>AuthServer จำลอง: รหัสผ่านถูก = Correct-Pass1!, ผู้ใช้ mfa-user ต้องส่ง OTP 123456</summary>
    private sealed class FakeAuthServer : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (LastPath != "/api/v1/auth/login")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            using JsonDocument doc = JsonDocument.Parse(LastBody);
            string user = doc.RootElement.GetProperty("userName").GetString()!;
            string password = doc.RootElement.GetProperty("password").GetString()!;
            string? otp = doc.RootElement.TryGetProperty("otpCode", out JsonElement o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;

            if (password != "Correct-Pass1!")
            {
                return Json(HttpStatusCode.Unauthorized, """{"succeeded":false,"mfaRequired":false,"error":"invalid_credentials","tokens":null}""");
            }

            if (user == "mfa-user" && otp != "123456")
            {
                return Json(HttpStatusCode.Unauthorized, """{"succeeded":false,"mfaRequired":true,"error":"mfa_required","tokens":null}""");
            }

            return Json(HttpStatusCode.OK, """{"succeeded":true,"mfaRequired":false,"passwordChangeRequired":false,"error":null,"tokens":{"accessToken":"token-abc","tokenType":"Bearer","expiresIn":600}}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
