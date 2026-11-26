using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Core.Domain;
using CustomerMgnt.Core.Membership.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Tests.Membership;

/// <summary>
/// ทดสอบ API ระบบสมาชิกทั้ง pipeline ด้วย store ในหน่วยความจำ: สิทธิ์, maker-checker, สมัครสมาชิก และ POS checkout → คืนสินค้า
/// ผู้เรียกกำหนดด้วย header: X-Test-User (ผู้ใช้) หรือ X-Test-Client (token แบบ client_credentials) และ X-Test-Perms
/// </summary>
[TestClass]
public sealed class MembershipApiTests
{
    private static WebApplicationFactory<Program> _factory = default!;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string AdminPerms = "member.read member.write member.pii member.verify.override tier.manage points.read points.rule.manage";
    private const string PosPerms = "pos.lookup pos.checkout pos.transaction";

    public TestContext TestContext { get; set; } = default!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Analytics:RefreshInitialDelaySeconds", "3600");
            b.UseSetting("Membership:OtpFixedCodeForDevelopment", "123456"); // ไม่ส่ง SMS จริง
            b.ConfigureTestServices(s => s.AddAuthentication(o =>
            {
                o.DefaultScheme = MembershipTestAuthHandler.Scheme;
                o.DefaultAuthenticateScheme = MembershipTestAuthHandler.Scheme;
                o.DefaultChallengeScheme = MembershipTestAuthHandler.Scheme;
            }).AddScheme<AuthenticationSchemeOptions, MembershipTestAuthHandler>(MembershipTestAuthHandler.Scheme, _ => { }));
        });
    }

    [ClassCleanup]
    public static void Cleanup() => _factory.Dispose();

    private static HttpClient User(string user, string perms)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User", user);
        c.DefaultRequestHeaders.Add("X-Test-Perms", perms);
        return c;
    }

    private static HttpClient Pos(string clientId)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Client", clientId);
        c.DefaultRequestHeaders.Add("X-Test-Perms", PosPerms);
        return c;
    }

    [TestMethod]
    public async Task Anonymous401_MissingPermission403()
    {
        using HttpClient anon = _factory.CreateClient();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anon.GetAsync(new Uri("/api/v1/members", UriKind.Relative), TestContext.CancellationToken)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anon.GetAsync(new Uri("/api/v1/me/member", UriKind.Relative), TestContext.CancellationToken)).StatusCode);

        using HttpClient reader = User("reader", "member.read");
        Assert.AreEqual(HttpStatusCode.OK, (await reader.GetAsync(new Uri("/api/v1/members", UriKind.Relative), TestContext.CancellationToken)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/tiers", new TierRequest { Code = "X", Name = "X", Rank = 9 }, Json, TestContext.CancellationToken)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await reader.GetAsync(new Uri("/api/v1/pos/members/lookup?phone=0812345678", UriKind.Relative), TestContext.CancellationToken)).StatusCode);
    }

    [TestMethod]
    public async Task PosPost_WithoutIdempotencyKey_Is400()
    {
        using HttpClient pos = Pos("pos-nokey");
        HttpResponseMessage r = await pos.PostAsJsonAsync("/api/v1/pos/checkout/reserve", Basket("M0000000", 0), Json, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, await r.Content.ReadAsStringAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task EndToEnd_Rule_Member_Checkout_Return()
    {
        CancellationToken ct = TestContext.CancellationToken;
        using HttpClient maker = User("maker", AdminPerms);
        using HttpClient checker = User("checker", "points.read points.rule.approve");
        using HttpClient pos = Pos("pos-e2e");

        // Step 1: Tier GOLD (ตัวคูณ 1.5) และกฎคะแนน 25 บาท = 1 คะแนน → รออนุมัติ
        await Expect<TierDTO>(maker.PostAsJsonAsync("/api/v1/tiers", new TierRequest { Code = "GOLD", Name = "Gold", Rank = 1, PointMultiplier = 1.5m }, Json, ct), HttpStatusCode.Created);
        EarnRuleDTO rule = await Expect<EarnRuleDTO>(maker.PostAsJsonAsync("/api/v1/points/rules",
            new EarnRuleRequest { Code = "STD", Name = "มาตรฐาน", SpendPerPoint = 25m, StartAt = DateTimeOffset.UtcNow.AddDays(-1), Submit = true }, Json, ct), HttpStatusCode.Created);
        Assert.AreEqual(EarnRuleStatus.PendingApproval, rule.Status);
        Assert.IsNotNull(rule.ApprovalRequestId);

        // Step 2: ผู้ยื่นอนุมัติเองไม่ได้ — คนอื่นอนุมัติแล้วกฎ Active
        using (HttpClient selfApprover = User("maker", AdminPerms + " points.rule.approve"))
        {
            HttpResponseMessage self = await selfApprover.PostAsJsonAsync($"/api/v1/approvals/{rule.ApprovalRequestId}/decision", new ApprovalDecisionRequest { Decision = ApprovalDecision.Approve }, Json, ct);
            Assert.AreEqual(HttpStatusCode.Forbidden, self.StatusCode);
        }

        HttpResponseMessage approved = await checker.PostAsJsonAsync($"/api/v1/approvals/{rule.ApprovalRequestId}/decision", new ApprovalDecisionRequest { Decision = ApprovalDecision.Approve, Comment = "ok" }, Json, ct);
        Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync(ct));
        List<EarnRuleDTO> active = await maker.GetFromJsonAsync<List<EarnRuleDTO>>("/api/v1/points/rules?activeOnly=true", Json, ct) ?? [];
        Assert.IsTrue(active.Any(r => r.Code == "STD"));

        // Step 3: สมัครสมาชิก (ได้ Tier แรก) และยืนยันแทน OTP
        MemberRegistrationResultDTO reg = await Expect<MemberRegistrationResultDTO>(maker.PostAsJsonAsync("/api/v1/members",
            new RegisterMemberRequest { FirstName = "สมชาย", LastName = "ใจดี", Phone = "081-234-5678" }, Json, ct), HttpStatusCode.Created);
        Assert.AreEqual(MemberStatus.PendingVerification, reg.Member.Status);
        Assert.AreEqual("GOLD", reg.Member.Tier?.Code);
        HttpResponseMessage duplicate = await maker.PostAsJsonAsync("/api/v1/members", new RegisterMemberRequest { FirstName = "ซ้ำ", LastName = "ซ้ำ", Phone = "0812345678" }, Json, ct);
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
        MemberDTO member = await Expect<MemberDTO>(maker.PostAsJsonAsync($"/api/v1/members/{reg.Member.Id}/verify-override", new VerifyOverrideRequest { Reason = "ตรวจบัตรประชาชนที่สาขา" }, Json, ct), HttpStatusCode.OK);
        Assert.AreEqual(MemberStatus.Active, member.Status);

        // Step 4: POS ค้นหาด้วยเบอร์ และขายครั้งแรก 10,000 บาท → floor(10,000/25) × 1.5 = 600 คะแนน
        PosMemberDTO found = await Expect<PosMemberDTO>(pos.GetAsync(new Uri("/api/v1/pos/members/lookup?phone=0812345678", UriKind.Relative), ct), HttpStatusCode.OK);
        Assert.AreEqual(member.MemberNo, found.MemberNo);
        var first = new QuoteRequest { MemberNo = member.MemberNo, BranchCode = "BKK01", Lines = [new QuoteLineRequest { LineNo = 1, ItemCode = "SKU-9", Qty = 1, UnitPrice = 10_000m }] };
        CheckoutResultDTO r1 = await CheckoutAsync(pos, first, "T-0001", ct);
        Assert.AreEqual(600, r1.PointsEarned);
        Assert.AreEqual(600, r1.Balance.Available);

        // Step 5: ตัวอย่าง spec — 2,350 บาท ส่วนลดท้ายบิล 100 จ่ายคะแนน 200 (50 บาท) → ได้ 132 คะแนน
        QuoteDTO quote = await Expect<QuoteDTO>(pos.PostAsJsonAsync("/api/v1/pos/checkout/quote", Basket(member.MemberNo, 200), Json, ct), HttpStatusCode.OK);
        Assert.AreEqual(2200m, quote.NetPayable);
        Assert.AreEqual(132, quote.PointsToEarn);

        string commitKey = Guid.NewGuid().ToString();
        ReservationDTO rsv = await ReserveAsync(pos, Basket(member.MemberNo, 200), ct);
        CheckoutResultDTO r2 = await CommitAsync(pos, rsv.ReservationId, "T-0002", rsv.Quote.NetPayable, commitKey, ct);
        Assert.AreEqual(132, r2.PointsEarned);
        Assert.AreEqual(200, r2.PointsRedeemed);
        Assert.AreEqual(600 - 200 + 132, r2.Balance.Available);

        // Step 6: ส่ง commit ซ้ำด้วย key เดิม (network หลุด) → ผลเดิม ไม่ให้คะแนนซ้ำ
        CheckoutResultDTO replay = await CommitAsync(pos, rsv.ReservationId, "T-0002", rsv.Quote.NetPayable, commitKey, ct);
        Assert.AreEqual(r2.PointsEarned, replay.PointsEarned);
        PointBalanceDTO afterReplay = await maker.GetFromJsonAsync<PointBalanceDTO>($"/api/v1/points/balances/{member.Id}", Json, ct) ?? new();
        Assert.AreEqual(532, afterReplay.Available);

        // Step 7: คืนสินค้าบางส่วน → ย้อนคะแนนที่ได้ และคืนคะแนนที่จ่ายตามสัดส่วน
        using var ret = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/transactions/T-0002/returns")
        {
            Content = JsonContent.Create(new ReturnRequest { ReturnId = "RT-01", Lines = [new ReturnLineRequest { LineNo = 2, Qty = 1 }] }, options: Json),
        };
        ret.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        ReturnResultDTO rr = await Expect<ReturnResultDTO>(pos.SendAsync(ret, ct), HttpStatusCode.OK);
        Assert.IsTrue(rr.PointsReversed is > 0 and < 132, $"reversed {rr.PointsReversed}");
        Assert.IsTrue(rr.PointsRestored is > 0 and < 200, $"restored {rr.PointsRestored}");
        PointBalanceDTO afterReturn = await maker.GetFromJsonAsync<PointBalanceDTO>($"/api/v1/points/balances/{member.Id}", Json, ct) ?? new();
        Assert.AreEqual(532 - rr.PointsReversed + rr.PointsRestored, afterReturn.Available);

        // Step 8: ledger ของสมาชิกครบทุกรายการ
        PagedResultDTO<PointLedgerDTO>? ledger = await maker.GetFromJsonAsync<PagedResultDTO<PointLedgerDTO>>($"/api/v1/points/ledger?memberId={member.Id}&pageSize=50", Json, ct);
        Assert.IsNotNull(ledger);
        Assert.AreEqual(afterReturn.Available, ledger.Items.Sum(l => l.Points));
    }

    private static QuoteRequest Basket(string memberNo, int points) => new()
    {
        MemberNo = memberNo,
        BranchCode = "BKK01",
        Lines =
        [
            new QuoteLineRequest { LineNo = 1, ItemCode = "SKU-1", Category = "FOOD", Qty = 2, UnitPrice = 450m },
            new QuoteLineRequest { LineNo = 2, ItemCode = "SKU-2", Category = "HOME", Qty = 1, UnitPrice = 1450m },
        ],
        BillDiscount = 100m,
        PointsToRedeem = points,
    };

    private static async Task<CheckoutResultDTO> CheckoutAsync(HttpClient pos, QuoteRequest req, string txnId, CancellationToken ct)
    {
        ReservationDTO rsv = await ReserveAsync(pos, req, ct);
        return await CommitAsync(pos, rsv.ReservationId, txnId, rsv.Quote.NetPayable, Guid.NewGuid().ToString(), ct);
    }

    private static async Task<ReservationDTO> ReserveAsync(HttpClient pos, QuoteRequest req, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pos/checkout/reserve") { Content = JsonContent.Create(req, options: Json) };
        msg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await Expect<ReservationDTO>(pos.SendAsync(msg, ct), HttpStatusCode.Created);
    }

    private static async Task<CheckoutResultDTO> CommitAsync(HttpClient pos, string reservationId, string txnId, decimal paid, string key, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/pos/checkout/{reservationId}/commit")
        {
            Content = JsonContent.Create(new CommitRequest { TransactionId = txnId, ReceiptNo = "R-" + txnId, PaidAmount = paid }, options: Json),
        };
        msg.Headers.Add("Idempotency-Key", key);
        return await Expect<CheckoutResultDTO>(pos.SendAsync(msg, ct), HttpStatusCode.OK);
    }

    private static async Task<T> Expect<T>(Task<HttpResponseMessage> call, HttpStatusCode status)
    {
        using HttpResponseMessage r = await call;
        string body = await r.Content.ReadAsStringAsync();
        Assert.AreEqual(status, r.StatusCode, body);
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new AssertFailedException("empty body");
    }
}

/// <summary>scheme ทดสอบ: ผู้ใช้ (X-Test-User) หรือ client_credentials (X-Test-Client) พร้อมสิทธิ์จาก X-Test-Perms</summary>
internal sealed class MembershipTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "MembershipTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Perms", out var perms))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>();
        string? client = Request.Headers["X-Test-Client"];
        if (!string.IsNullOrEmpty(client))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, client));
            claims.Add(new Claim("client_id", client));
        }
        else
        {
            string user = Request.Headers["X-Test-User"].ToString() is { Length: > 0 } u ? u : "tester";
            claims.Add(new Claim(ClaimTypes.NameIdentifier, user));
            claims.Add(new Claim(ClaimTypes.Name, user));
            claims.Add(new Claim("preferred_username", user));
            claims.Add(new Claim("client_id", "customermgnt-membership"));
        }

        claims.AddRange(perms.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("perm", p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }
}
