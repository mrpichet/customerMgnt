using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Core.Domain;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Tests.Api;

/// <summary>
/// ทดสอบ REST API ทั้ง pipeline (routing, validation, สิทธิ์, ProblemDetails, security headers) ด้วยข้อมูลในหน่วยความจำ
/// แทน AuthServer ด้วย scheme ทดสอบที่อ่านสิทธิ์จาก header X-Test-Perms
/// </summary>
[TestClass]
public sealed class ApiEndToEndTests
{
    private static WebApplicationFactory<Program> _factory = default!;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public TestContext TestContext { get; set; } = default!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Analytics:RefreshInitialDelaySeconds", "3600");
            b.ConfigureTestServices(s => s.AddAuthentication(o =>
            {
                o.DefaultScheme = TestAuthHandler.Scheme;
                o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                o.DefaultChallengeScheme = TestAuthHandler.Scheme;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { }));
        });
    }

    [ClassCleanup]
    public static void Cleanup() => _factory.Dispose();

    private static HttpClient Client(params string[] permissions)
    {
        HttpClient c = _factory.CreateClient();
        if (permissions.Length > 0) c.DefaultRequestHeaders.Add("X-Test-Perms", string.Join(' ', permissions));
        return c;
    }

    [TestMethod]
    public async Task Anonymous_Gets401_HealthIsPublic()
    {
        using HttpClient c = Client();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await c.GetAsync(new Uri("/api/customers", UriKind.Relative), TestContext.CancellationToken)).StatusCode);
        HttpResponseMessage health = await c.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.IsTrue(health.Headers.Contains("X-Content-Type-Options"));
    }

    [TestMethod]
    public async Task MissingPermission_Gets403()
    {
        using HttpClient c = Client(Permissions.CustomerRead);
        HttpResponseMessage r = await c.PostAsJsonAsync("/api/sla/policies", new { name = "x" }, Json, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [TestMethod]
    public async Task RegisterCustomer_ThenSearch_And360_AndSla()
    {
        CancellationToken ct = TestContext.CancellationToken;
        using HttpClient c = Client([.. Permissions.All]);

        // Step 1: ข้อมูลไม่ครบ → 400 ProblemDetails
        HttpResponseMessage bad = await c.PostAsJsonAsync("/api/customers", new { kind = "Juristic" }, Json, ct);
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.AreEqual("application/problem+json", bad.Content.Headers.ContentType?.MediaType);

        // Step 2: ลงทะเบียน → 201 + Location
        HttpResponseMessage created = await c.PostAsJsonAsync("/api/customers", new RegisterCustomerRequest { Kind = CustomerKind.Juristic, LegalName = "บริษัท อีทูอี จำกัด", Country = "TH", TimeZoneId = "Asia/Bangkok", Status = CustomerStatus.Active }, Json, ct);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        CustomerDTO customer = (await created.Content.ReadFromJsonAsync<CustomerDTO>(Json, ct))!;
        Assert.IsNotNull(created.Headers.Location);

        // Step 3: ค้นหาด้วยภาษาไทย + 360
        PagedResultDTO<CustomerDTO> page = (await c.GetFromJsonAsync<PagedResultDTO<CustomerDTO>>("/api/customers?text=" + Uri.EscapeDataString("อีทูอี"), Json, ct))!;
        Assert.AreEqual(customer.Id, page.Items.Single().Id);
        Customer360DTO view = (await c.GetFromJsonAsync<Customer360DTO>($"/api/customers/{customer.Id}/360", Json, ct))!;
        Assert.AreEqual("Standard SLA", view.SlaPolicyName);

        // Step 4: SLA ตามรหัสลูกค้า (endpoint ที่ IssueTracking เรียก)
        HttpResponseMessage target = await c.PostAsJsonAsync("/api/sla/calculate", new SlaCalculateRequest { CustomerCode = customer.Code, SeverityCode = "S2", StartedAt = DateTimeOffset.UtcNow }, Json, ct);
        Assert.AreEqual(HttpStatusCode.OK, target.StatusCode);
        Assert.AreEqual("Urgent", (await target.Content.ReadFromJsonAsync<SlaTargetDTO>(Json, ct))!.IssuePriority);

        // Step 5: ไม่พบ → 404
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.GetAsync(new Uri("/api/customers/999999", UriKind.Relative), ct)).StatusCode);
    }

    [TestMethod]
    public async Task AiTools_Listed()
    {
        using HttpClient c = Client(Permissions.AnalyticsRead);
        JsonElement tools = await c.GetFromJsonAsync<JsonElement>("/api/ai/tools", Json, TestContext.CancellationToken);
        Assert.IsTrue(tools.EnumerateArray().Any(t => t.GetProperty("name").GetString() == "calculate_customer_risk"));
    }

    [TestMethod]
    public async Task LineWebhook_RejectsUnsignedRequests()
    {
        using HttpClient c = Client();
        using var content = new StringContent("{\"events\":[]}", System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage r = await c.PostAsync(new Uri("/api/webhooks/line", UriKind.Relative), content, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

/// <summary>scheme ทดสอบ: header X-Test-Perms = สิทธิ์คั่นด้วยช่องว่าง</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Perms", out var perms))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "tester"), new(ClaimTypes.Name, "Tester") };
        claims.AddRange(perms.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("perm", p)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role)), Scheme)));
    }
}
