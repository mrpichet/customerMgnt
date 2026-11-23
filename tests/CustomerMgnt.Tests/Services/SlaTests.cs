using Common.Core.Exceptions;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;
using CustomerMgnt.Tests.Support;

namespace CustomerMgnt.Tests.Services;

[TestClass]
public sealed class SlaTests
{
    public TestContext TestContext { get; set; } = default!;

    private static SlaPolicyDTO Policy(string? holidays = null) => new()
    {
        Id = 1, Name = "Gold", TimeZoneId = "Asia/Bangkok", BusinessStart = new TimeOnly(8, 30), BusinessEnd = new TimeOnly(17, 30),
        WorkingDaysMask = 31, AtRiskThreshold = 0.75, IsActive = true, ValidFrom = new DateOnly(2020, 1, 1), Holidays = holidays,
        Levels =
        [
            new() { Code = "S1", Name = "Critical", Rank = 1, ResponseMinutes = 30, ResolutionMinutes = 240, BusinessHoursOnly = false, IssuePriority = "Immediate" },
            new() { Code = "S2", Name = "High", Rank = 2, ResponseMinutes = 60, ResolutionMinutes = 120, BusinessHoursOnly = true, IssuePriority = "Urgent" },
        ],
    };

    [TestMethod]
    public void BusinessHours_SkipWeekend()
    {
        // ศุกร์ 16:30 (ไทย) + 120 นาทีทำการ = 60 นาทีวันศุกร์ + 60 นาทีวันจันทร์ → จันทร์ 09:30 (ไทย) = 02:30Z
        var start = new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);
        SlaTargetDTO t = SlaService.CalculateTargets("C1", Policy(), "s2", start);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 23, 2, 30, 0, TimeSpan.Zero), t.ResolutionDueAt);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 20, 10, 30, 0, TimeSpan.Zero), t.ResponseDueAt);
        Assert.AreEqual("Urgent", t.IssuePriority);
    }

    [TestMethod]
    public void BusinessHours_SkipHoliday_AndStartBeforeOpening()
    {
        // จันทร์เป็นวันหยุด → เริ่มนับอังคาร 08:30; เริ่มนับจากเสาร์ 03:00 (ไทย)
        var start = new DateTimeOffset(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);
        SlaTargetDTO t = SlaService.CalculateTargets("C1", Policy("2026-11-23"), "S2", start);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 24, 2, 30, 0, TimeSpan.Zero), t.ResponseDueAt); // อังคาร 09:30 ไทย
    }

    [TestMethod]
    public void TwentyFourSeven_AddsClockMinutes()
    {
        var start = new DateTimeOffset(2026, 11, 21, 15, 0, 0, TimeSpan.Zero); // เสาร์
        SlaTargetDTO t = SlaService.CalculateTargets("C1", Policy(), "S1", start);
        Assert.AreEqual(start.AddMinutes(240), t.ResolutionDueAt);
    }

    [TestMethod]
    public void UnknownSeverity_ListsAllowedCodes()
    {
        ValidationException ex = Assert.ThrowsExactly<ValidationException>(() => SlaService.CalculateTargets("C1", Policy(), "S9", DateTimeOffset.UtcNow));
        CollectionAssert.AreEqual(new[] { "S1", "S2" }, ex.Errors["severityCode"]);
    }

    [TestMethod]
    public void Evaluate_States()
    {
        SlaPolicyDTO p = Policy();
        var start = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
        SlaTargetDTO t = SlaService.CalculateTargets("C1", p, "S1", start);

        Assert.AreEqual(SlaState.OnTrack, SlaService.Evaluate(t, p, null, null, start.AddMinutes(60)).ResolutionState);
        Assert.AreEqual(SlaState.AtRisk, SlaService.Evaluate(t, p, null, null, start.AddMinutes(200)).ResolutionState);
        SlaEvaluationDTO late = SlaService.Evaluate(t, p, start.AddMinutes(10), null, start.AddMinutes(300));
        Assert.AreEqual(SlaState.Breached, late.ResolutionState);
        Assert.AreEqual(SlaState.Met, late.ResponseState);
        Assert.IsLessThan(0, late.MinutesToResolutionDue);
    }

    [TestMethod]
    public async Task PerCustomerPolicy_OverridesStandard()
    {
        await using var host = new TestHost();
        var user = TestHost.AllPermissions();
        CancellationToken ct = TestContext.CancellationToken;
        CustomerService customers = host.Get<CustomerService>();
        SlaService sla = host.Get<SlaService>();

        CustomerDTO gold = await customers.RegisterAsync(user, new RegisterCustomerRequest { Kind = CustomerKind.Juristic, LegalName = "Gold Co", Country = "TH", TimeZoneId = "Asia/Bangkok", Status = CustomerStatus.Active }, ct);
        CustomerDTO basic = await customers.RegisterAsync(user, new RegisterCustomerRequest { Kind = CustomerKind.Juristic, LegalName = "Basic Co", Country = "TH", TimeZoneId = "Asia/Bangkok", Status = CustomerStatus.Active }, ct);

        // Step 1: ลูกค้า Gold กำหนด severity ของตัวเอง (P1/P2) — ลูกค้าอื่นยังใช้ S1-S4 มาตรฐาน
        await sla.CreatePolicyAsync(user, new SlaPolicyRequest
        {
            CustomerId = gold.Id, Name = "Gold SLA", ValidFrom = new DateOnly(2026, 1, 1), TimeZoneId = "Asia/Bangkok", BusinessStart = new TimeOnly(8, 0), BusinessEnd = new TimeOnly(18, 0),
            WorkingDaysMask = 31, AtRiskThreshold = 0.8,
            Levels = [new() { Code = "P1", Name = "Down", Rank = 1, ResponseMinutes = 15, ResolutionMinutes = 60 }, new() { Code = "P2", Name = "Degraded", Rank = 2, ResponseMinutes = 60, ResolutionMinutes = 480, BusinessHoursOnly = true }],
        }, ct);

        SlaTargetDTO goldTarget = await sla.CalculateTargetsAsync(user, new SlaCalculateRequest { CustomerCode = gold.Code, SeverityCode = "P1", StartedAt = host.Clock.GetUtcNow() }, ct);
        Assert.AreEqual("Gold SLA", goldTarget.PolicyName);
        Assert.AreEqual("Immediate", goldTarget.IssuePriority, "rank 1 maps to the highest IssueTracking priority by default");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => sla.CalculateTargetsAsync(user, new SlaCalculateRequest { CustomerCode = gold.Code, SeverityCode = "S1" }, ct));

        SlaTargetDTO basicTarget = await sla.CalculateTargetsAsync(user, new SlaCalculateRequest { CustomerCode = basic.Code, SeverityCode = "S1" }, ct);
        Assert.AreEqual("Standard SLA", basicTarget.PolicyName);
    }

    [TestMethod]
    public async Task ManagePolicy_RequiresPermission()
    {
        await using var host = new TestHost();
        SlaService sla = host.Get<SlaService>();
        await Assert.ThrowsExactlyAsync<ForbiddenException>(() => sla.CreatePolicyAsync(TestHost.With(Permissions.SlaRead), new SlaPolicyRequest { Name = "x", Levels = [] }, TestContext.CancellationToken));
    }

    [TestMethod]
    public void Aging_BucketsBySeverity()
    {
        var now = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
        ExternalIssueDTO Issue(int ageDays, string sev, bool closed = false) => new() { Subject = "x", CreatedAt = now.AddDays(-ageDays), SeverityCode = sev, IsClosed = closed, CustomerCode = "C1" };
        IssueAgingSummaryDTO s = InsightService.BuildAging("C1", [Issue(1, "S1"), Issue(10, "S2"), Issue(12, "S2"), Issue(100, "S3"), Issue(5, "S1", closed: true)], [7, 30, 90], null, now);
        Assert.AreEqual(4, s.OpenIssues);
        Assert.AreEqual(1, s.Buckets[0].BySeverity["S1"]);
        Assert.AreEqual(2, s.Buckets[1].BySeverity["S2"]);
        Assert.AreEqual(1, s.Buckets[3].Total);
        Assert.AreEqual("90+ วัน", s.Buckets[3].Label);
    }
}
