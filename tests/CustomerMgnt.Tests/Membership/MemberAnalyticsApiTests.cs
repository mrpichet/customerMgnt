using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.MemberAnalytics.Services;
using CustomerMgnt.Core.Membership.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CustomerMgnt.Tests.Membership;

/// <summary>
/// ทดสอบ API ระบบวิเคราะห์ (Claude.MD ส่วนที่ 3) ทั้ง pipeline ด้วย store ในหน่วยความจำ — ปิดงานเบื้องหลังแล้วสั่งคำนวณ read model ตรงจาก service
/// </summary>
[TestClass]
public sealed class MemberAnalyticsApiTests
{
    private static WebApplicationFactory<Program> _factory = default!;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string AnaAll = "ana.dashboard.read ana.member.read ana.member.detail ana.promotion.read ana.promotion.all ana.report.build ana.report.share ana.export ana.alert.manage ana.settings.manage";
    private const string MemberAdmin = "member.read member.write member.pii member.verify.override";

    public TestContext TestContext { get; set; } = default!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Analytics:RefreshInitialDelaySeconds", "3600");
            b.UseSetting("MemberAnalytics:WorkerEnabled", "false");
            b.UseSetting("Membership:OtpFixedCodeForDevelopment", "123456");
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

    private static HttpClient Client(string clientId, string perms)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Client", clientId);
        c.DefaultRequestHeaders.Add("X-Test-Perms", perms);
        return c;
    }

    private static Uri U(string path) => new(path, UriKind.Relative);

    private static async Task<T> Expect<T>(Task<HttpResponseMessage> call, HttpStatusCode status)
    {
        using HttpResponseMessage r = await call;
        string body = await r.Content.ReadAsStringAsync();
        Assert.AreEqual(status, r.StatusCode, body);
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new AssertFailedException("empty body");
    }

    private static async Task<string> ExpectError(Task<HttpResponseMessage> call, HttpStatusCode status, string code)
    {
        using HttpResponseMessage r = await call;
        string body = await r.Content.ReadAsStringAsync();
        Assert.AreEqual(status, r.StatusCode, body);
        StringAssert.Contains(body, code);
        return body;
    }

    private static HttpRequestMessage WithKey(HttpMethod method, string path, object body, string? key = null)
    {
        var msg = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: Json) };
        msg.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return msg;
    }

    [TestMethod]
    public async Task Permissions_403_Without_AnaPermission()
    {
        CancellationToken ct = TestContext.CancellationToken;
        using HttpClient anon = _factory.CreateClient();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anon.GetAsync(U("/api/v1/analytics/dashboards/executive"), ct)).StatusCode);
        using HttpClient none = User("nobody", "member.read");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await none.GetAsync(U("/api/v1/analytics/dashboards/executive"), ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await none.GetAsync(U("/api/v1/analytics/catalog"), ct)).StatusCode);
        using HttpClient dash = User("dash", "ana.dashboard.read");
        Assert.AreEqual(HttpStatusCode.OK, (await dash.GetAsync(U("/api/v1/analytics/dashboards/executive"), ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await dash.GetAsync(U("/api/v1/analytics/dashboards/members"), ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await dash.GetAsync(U("/api/v1/analytics/admin/settings"), ct)).StatusCode);
        MetricCatalogDTO cat = await Expect<MetricCatalogDTO>(dash.GetAsync(U("/api/v1/analytics/catalog"), ct), HttpStatusCode.OK);
        Assert.IsTrue(cat.Metrics.Any(m => m.Code == "sales.net"));
        Assert.IsFalse(cat.Metrics.Any(m => m.Code == "member.new"), "member metrics need ana.member.read");
    }

    [TestMethod]
    public async Task DateRange_Over731Days_Is400()
    {
        using HttpClient a = User("range", AnaAll);
        await ExpectError(a.GetAsync(U("/api/v1/analytics/dashboards/executive?from=2020-01-01&to=2024-01-01"), TestContext.CancellationToken), HttpStatusCode.BadRequest, "range_too_large");
    }

    [TestMethod]
    public async Task Ingest_RequiresKey_PerItemResults_AndReplay()
    {
        CancellationToken ct = TestContext.CancellationToken;
        using HttpClient erp = Client("erp-test", "analytics.ingest");
        using HttpClient user = User("u-ingest", AnaAll);

        // ไม่มี Idempotency-Key = 400 / ผู้ใช้ทั่วไป (ไม่มี scope) = 403
        HttpResponseMessage noKey = await erp.PutAsJsonAsync("/api/v1/ingest/products", new[] { new ProductIngestDTO { ItemCode = "SKU-1", UpdatedAt = DateTimeOffset.UtcNow } }, Json, ct);
        Assert.AreEqual(HttpStatusCode.BadRequest, noKey.StatusCode);
        using (HttpRequestMessage m = WithKey(HttpMethod.Put, "/api/v1/ingest/products", Array.Empty<ProductIngestDTO>()))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, (await user.SendAsync(m, ct)).StatusCode);
        }

        // รายการผิด 1 รายการไม่ทำให้ทั้ง batch ล้ม
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            new ProductIngestDTO { ItemCode = "SKU-1", Brand = "ACME", Category = "FOOD", UnitCost = 300m, UpdatedAt = now },
            new ProductIngestDTO { ItemCode = "", UpdatedAt = now },
        };
        string key = Guid.NewGuid().ToString();
        IngestResultDTO first;
        using (HttpRequestMessage m = WithKey(HttpMethod.Put, "/api/v1/ingest/products", items, key))
        {
            first = await Expect<IngestResultDTO>(erp.SendAsync(m, ct), HttpStatusCode.OK);
        }

        Assert.AreEqual(1, first.Accepted);
        Assert.AreEqual(1, first.Rejected);
        Assert.AreEqual(IngestItemStatus.Rejected, first.Results.Single(r => r.Index == 1).Status);

        // ส่งซ้ำ key เดิม body เดิม = response เดิม + Idempotent-Replayed
        using (HttpRequestMessage m = WithKey(HttpMethod.Put, "/api/v1/ingest/products", items, key))
        {
            using HttpResponseMessage replay = await erp.SendAsync(m, ct);
            Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode);
            Assert.IsTrue(replay.Headers.TryGetValues("Idempotent-Replayed", out IEnumerable<string>? v) && v.Contains("true"));
        }

        // ข้อมูลเก่ากว่า (updatedAt ย้อนหลัง) = Stale
        using (HttpRequestMessage m = WithKey(HttpMethod.Put, "/api/v1/ingest/products", new[] { new ProductIngestDTO { ItemCode = "SKU-1", Brand = "OLD", UpdatedAt = now.AddHours(-1) } }))
        {
            IngestResultDTO stale = await Expect<IngestResultDTO>(erp.SendAsync(m, ct), HttpStatusCode.OK);
            Assert.AreEqual(1, stale.Stale);
        }

        // รายการที่ถูกปฏิเสธบันทึกเป็นปัญหาข้อมูล
        PagedResultDTOProbe issues = await Expect<PagedResultDTOProbe>(user.GetAsync(U("/api/v1/analytics/admin/data-issues"), ct), HttpStatusCode.OK);
        Assert.IsTrue(issues.Total >= 1);
    }

    [TestMethod]
    public async Task Reports_SavedFilters_Funnels_Validation()
    {
        CancellationToken ct = TestContext.CancellationToken;
        using HttpClient a = User("builder", AnaAll);

        // ตัวกรองที่บันทึก: สร้าง → แก้ (version) → version เก่า = 409 → ลบ
        SavedFilterDTO f = await Expect<SavedFilterDTO>(a.PostAsJsonAsync("/api/v1/analytics/saved-filters",
            new SavedFilterDTO { Page = "AN-01", Name = "สาขากรุงเทพ", Filter = new AnalyticsFilterDTO { Branch = ["BKK01"] }, IsDefault = true }, Json, ct), HttpStatusCode.Created);
        f.Name = "สาขากรุงเทพ 2";
        SavedFilterDTO f2 = await Expect<SavedFilterDTO>(a.PutAsJsonAsync($"/api/v1/analytics/saved-filters/{f.Id}", f, Json, ct), HttpStatusCode.OK);
        Assert.AreEqual(f.Version + 1, f2.Version);
        Assert.AreEqual(HttpStatusCode.Conflict, (await a.PutAsJsonAsync($"/api/v1/analytics/saved-filters/{f.Id}", f, Json, ct)).StatusCode);
        using (HttpClient other = User("someone-else", AnaAll))
        {
            Assert.AreEqual(0, (await Expect<List<SavedFilterDTO>>(other.GetAsync(U("/api/v1/analytics/saved-filters?page=AN-01"), ct), HttpStatusCode.OK)).Count);
        }

        Assert.AreEqual(HttpStatusCode.NoContent, (await a.DeleteAsync(U($"/api/v1/analytics/saved-filters/{f.Id}"), ct)).StatusCode);

        // รายงาน: เกิน 3 dimension = 400 report_too_complex; รายงานมาตรฐานแก้ไม่ได้ = 409; ทำสำเนาได้; เรียกดูได้
        await ExpectError(a.PostAsJsonAsync("/api/v1/analytics/reports",
            new ReportDefinitionDTO { Name = "ซับซ้อน", Dimensions = ["day", "branch", "tier", "brand"], Metrics = ["sales.net"] }, Json, ct), HttpStatusCode.BadRequest, "report_too_complex");
        List<ReportSummaryDTO> list = await Expect<List<ReportSummaryDTO>>(a.GetAsync(U("/api/v1/analytics/reports?standard=true"), ct), HttpStatusCode.OK);
        Assert.AreEqual(4, list.Count);
        Guid std = list[0].Id;
        ReportDefinitionDTO stdDef = await Expect<ReportDefinitionDTO>(a.GetAsync(U($"/api/v1/analytics/reports/{std}"), ct), HttpStatusCode.OK);
        await ExpectError(a.PutAsJsonAsync($"/api/v1/analytics/reports/{std}", stdDef, Json, ct), HttpStatusCode.Conflict, "standard_report");
        ReportDefinitionDTO copy = await Expect<ReportDefinitionDTO>(a.PostAsJsonAsync($"/api/v1/analytics/reports/{std}/copy", new ReportCopyRequest { Name = "สำเนา" }, Json, ct), HttpStatusCode.Created);
        Assert.IsFalse(copy.Standard);
        await Expect<AnalyticsResultDTO<ReportResultDTO>>(a.GetAsync(U($"/api/v1/analytics/reports/{copy.Id}/run"), ct), HttpStatusCode.OK);

        // Funnel: 1 ขั้น = 400; 2 ขั้น = 201; อ่านผลได้; ผู้มีแค่ ana.promotion.read สร้างไม่ได้
        Assert.AreEqual(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/v1/analytics/funnels",
            new FunnelDTO { Name = "สั้นไป", Steps = [FunnelStep.MessageReceived] }, Json, ct)).StatusCode);
        FunnelDTO fn = await Expect<FunnelDTO>(a.PostAsJsonAsync("/api/v1/analytics/funnels",
            new FunnelDTO { Name = "ข้อความ → ซื้อ", Steps = [FunnelStep.MessageReceived, FunnelStep.Purchased] }, Json, ct), HttpStatusCode.Created);
        await Expect<AnalyticsResultDTO<FunnelResultDTO>>(a.GetAsync(U($"/api/v1/analytics/funnels/{fn.Id}/result"), ct), HttpStatusCode.OK);
        using (HttpClient reader = User("promo-reader", "ana.promotion.read"))
        {
            Assert.AreEqual(1, (await Expect<List<FunnelDTO>>(reader.GetAsync(U("/api/v1/analytics/funnels"), ct), HttpStatusCode.OK)).Count);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/analytics/funnels", fn, Json, ct)).StatusCode);
        }

        // เทียบแคมเปญน้อยกว่า 2 = 400 compare_limit
        await ExpectError(a.GetAsync(U("/api/v1/analytics/campaigns/compare?codes=A"), ct), HttpStatusCode.BadRequest, "compare_limit");
    }

    [TestMethod]
    public async Task Refresh_RangeLimit_And_Overlap409()
    {
        CancellationToken ct = TestContext.CancellationToken;
        using HttpClient a = User("refresher", AnaAll);
        await ExpectError(a.PostAsJsonAsync("/api/v1/analytics/admin/refresh", new RefreshRequest { From = new DateOnly(2020, 1, 1), To = new DateOnly(2023, 1, 1) }, Json, ct),
            HttpStatusCode.BadRequest, "range_too_large");
        RefreshRunDTO run = await Expect<RefreshRunDTO>(a.PostAsJsonAsync("/api/v1/analytics/admin/refresh",
            new RefreshRequest { Tables = [AnaTables.Points], From = new DateOnly(2025, 1, 1), To = new DateOnly(2025, 1, 31) }, Json, ct), HttpStatusCode.Accepted);
        Assert.AreEqual(JobStatus.Queued, run.Status);
        await ExpectError(a.PostAsJsonAsync("/api/v1/analytics/admin/refresh",
            new RefreshRequest { Tables = [AnaTables.Points], From = new DateOnly(2025, 1, 15), To = new DateOnly(2025, 2, 15) }, Json, ct), HttpStatusCode.Conflict, "refresh_running");

        // งานที่รออยู่ทำได้ แล้วสั่งช่วงเดิมได้อีก
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReadModelRefreshService>().RunQueuedAsync(ct);
        }

        List<RefreshRunDTO> runs = await Expect<List<RefreshRunDTO>>(a.GetAsync(U("/api/v1/analytics/admin/refresh"), ct), HttpStatusCode.OK);
        Assert.AreEqual(JobStatus.Done, runs.Single(r => r.Id == run.Id).Status);
        await Expect<RefreshRunDTO>(a.PostAsJsonAsync("/api/v1/analytics/admin/refresh",
            new RefreshRequest { Tables = [AnaTables.Points], From = new DateOnly(2025, 1, 15), To = new DateOnly(2025, 2, 15) }, Json, ct), HttpStatusCode.Accepted);
    }

    [TestMethod]
    public async Task EndToEnd_Sale_To_Dashboard_Drilldown_Export()
    {
        CancellationToken ct = TestContext.CancellationToken;
        using HttpClient admin = User("mbr-admin", MemberAdmin);
        using HttpClient pos = Client("pos-ana", "pos.lookup pos.checkout pos.transaction");
        using HttpClient a = User("analyst", AnaAll + " ana.scope.all member.read");

        // Step 1: สมาชิก 1 คน + ขาย 1,000 บาท และรายการทดสอบ 5,000 บาท (ไม่นับ)
        MemberRegistrationResultDTO reg = await Expect<MemberRegistrationResultDTO>(admin.PostAsJsonAsync("/api/v1/members",
            new RegisterMemberRequest { FirstName = "วิเคราะห์", LastName = "ทดสอบ", Phone = "089-111-2222" }, Json, ct), HttpStatusCode.Created);
        MemberDTO member = await Expect<MemberDTO>(admin.PostAsJsonAsync($"/api/v1/members/{reg.Member.Id}/verify-override", new VerifyOverrideRequest { Reason = "ตรวจบัตรที่สาขา" }, Json, ct),
            HttpStatusCode.OK);
        foreach ((string txn, decimal price, bool test) in new[] { ("A-1", 1000m, false), ("A-TEST", 5000m, true) })
        {
            var sale = new SalesTransactionRequest
            {
                MemberNo = member.MemberNo, BranchCode = "BKK01", TransactionId = txn, PaidAt = DateTimeOffset.UtcNow, PaidAmount = price, IsTest = test,
                Lines = [new QuoteLineRequest { LineNo = 1, ItemCode = "SKU-1", Category = "FOOD", Qty = 1, UnitPrice = price }],
            };
            using HttpRequestMessage m = WithKey(HttpMethod.Post, "/api/v1/pos/transactions", sale);
            await Expect<TransactionResultDTO>(pos.SendAsync(m, ct), HttpStatusCode.Created);
        }

        // Step 2: คำนวณ read model (งานเบื้องหลังปิดอยู่)
        DateOnly today = AnaTime.Today(TimeProvider.System);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ReadModelRefreshService refresh = scope.ServiceProvider.GetRequiredService<ReadModelRefreshService>();
            foreach (string t in AnaTables.All)
            {
                await refresh.RefreshTableAsync(t, [today], null, ct);
            }
        }

        // Step 3: dashboard ผู้บริหาร — ยอดขายสมาชิก 1,000 (ไม่รวมรายการทดสอบ)
        string range = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        AnalyticsResultDTO<ExecutiveDashboardDTO> exec = await Expect<AnalyticsResultDTO<ExecutiveDashboardDTO>>(
            a.GetAsync(U($"/api/v1/analytics/dashboards/executive?{range}"), ct), HttpStatusCode.OK);
        Assert.AreEqual(1000m, exec.Data.Kpis["memberSales"].Value);
        Assert.AreEqual(1m, exec.Data.Kpis["membersNew"].Value);

        // ผู้ใช้ที่ไม่มีกฎขอบเขตและไม่มี ana.scope.all = 200 scopeEmpty (ไม่ใช่ 403)
        using (HttpClient noScope = User("no-scope", AnaAll))
        {
            AnalyticsResultDTO<ExecutiveDashboardDTO> empty = await Expect<AnalyticsResultDTO<ExecutiveDashboardDTO>>(
                noScope.GetAsync(U($"/api/v1/analytics/dashboards/executive?{range}"), ct), HttpStatusCode.OK);
            Assert.IsTrue(empty.ScopeEmpty);
        }

        // ขอบเขตสาขาอื่น = ไม่มียอด
        AnalyticsResultDTO<ExecutiveDashboardDTO> other = await Expect<AnalyticsResultDTO<ExecutiveDashboardDTO>>(
            a.GetAsync(U($"/api/v1/analytics/dashboards/executive?{range}&branch=CNX01"), ct), HttpStatusCode.OK);
        Assert.AreEqual(0m, other.Data.Kpis["memberSales"].Value ?? 0m);

        // Step 4: drill-down ระดับสมาชิกต้องมี ana.member.detail
        AnalyticsResultDTO<DrilldownPageDTO> drill = await Expect<AnalyticsResultDTO<DrilldownPageDTO>>(
            a.GetAsync(U($"/api/v1/analytics/drilldown?{range}&metric=sales.net&level=Member"), ct), HttpStatusCode.OK);
        Assert.AreEqual(1, drill.Data.Total);
        using (HttpClient noDetail = User("no-detail", "ana.dashboard.read ana.scope.all"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, (await noDetail.GetAsync(U($"/api/v1/analytics/drilldown?{range}&metric=sales.net&level=Member"), ct)).StatusCode);
        }

        // Step 5: ส่งออก (ทำเบื้องหลัง) → ดาวน์โหลดได้เฉพาะเจ้าของ; ไม่ mask = 400
        await ExpectError(a.PostAsJsonAsync("/api/v1/analytics/exports",
            new AnaExportRequest { Source = ExportSource.Dashboard, Page = "AN-01", Filter = new AnalyticsFilterDTO { From = today, To = today }, Unmasked = true }, Json, ct),
            HttpStatusCode.BadRequest, "unmasked_not_supported");
        AnaExportJobDTO job = await Expect<AnaExportJobDTO>(a.PostAsJsonAsync("/api/v1/analytics/exports",
            new AnaExportRequest { Source = ExportSource.Dashboard, Page = "AN-01", Filter = new AnalyticsFilterDTO { From = today, To = today }, Format = "Csv" }, Json, ct), HttpStatusCode.Accepted);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AnaReportService>().RunExportsAsync(ct);
        }

        using HttpResponseMessage file = await a.GetAsync(U($"/api/v1/analytics/exports/{job.Id}/file"), ct);
        Assert.AreEqual(HttpStatusCode.OK, file.StatusCode, await file.Content.ReadAsStringAsync(ct));
        Assert.IsTrue((await file.Content.ReadAsByteArrayAsync(ct)).Length > 0);
        using (HttpClient stranger = User("stranger", AnaAll))
        {
            Assert.AreNotEqual(HttpStatusCode.OK, (await stranger.GetAsync(U($"/api/v1/analytics/exports/{job.Id}/file"), ct)).StatusCode);
        }
    }

    /// <summary>อ่านเฉพาะ total ของผลแบบแบ่งหน้า</summary>
    private sealed class PagedResultDTOProbe
    {
        public long Total { get; set; }
    }
}
