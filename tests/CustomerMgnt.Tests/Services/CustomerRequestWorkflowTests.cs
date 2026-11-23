using Common.Core.Exceptions;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Services;
using CustomerMgnt.Tests.Support;

namespace CustomerMgnt.Tests.Services;

[TestClass]
public sealed class CustomerRequestWorkflowTests
{
    public TestContext TestContext { get; set; } = default!;

    private static async Task<(CustomerDTO Customer, CustomerRequestDTO Request)> SeedAsync(TestHost host, CancellationToken ct)
    {
        var user = TestHost.AllPermissions();
        CustomerDTO c = await host.Get<CustomerService>().RegisterAsync(user, new RegisterCustomerRequest { Kind = CustomerKind.Juristic, LegalName = "บริษัท เอ จำกัด", TaxId = "0105551234567", Country = "TH", TimeZoneId = "Asia/Bangkok", Status = CustomerStatus.Active }, ct);
        CustomerRequestDTO r = await host.Get<CustomerRequestService>().CreateAsync(user, new CreateCustomerRequestRequest
        {
            CustomerId = c.Id, Kind = RequestKind.Tor, Title = "ระบบ CRM", Description = "TOR", Budget = 2_000_000,
            Items = [new() { ItemNo = "1", Text = "รองรับ LINE OA", Priority = "Must", Mandatory = true }, new() { ItemNo = "2", Text = "Dashboard", Priority = "Should" }],
        }, ct);
        return (c, r);
    }

    [TestMethod]
    public async Task Register_EncryptsAndMasksTaxId_AndRejectsDuplicate()
    {
        await using var host = new TestHost();
        CancellationToken ct = TestContext.CancellationToken;
        (CustomerDTO c, _) = await SeedAsync(host, ct);
        Assert.AreNotEqual("0105551234567", c.TaxIdMasked);
        Assert.IsTrue(c.TaxIdMasked!.EndsWith("4567", StringComparison.Ordinal));
        await Assert.ThrowsExactlyAsync<ConflictException>(() => host.Get<CustomerService>().RegisterAsync(TestHost.AllPermissions(),
            new RegisterCustomerRequest { Kind = CustomerKind.Juristic, LegalName = "ซ้ำ", TaxId = "0105551234567", Country = "TH", TimeZoneId = "Asia/Bangkok", Status = CustomerStatus.Active }, ct));
    }

    [TestMethod]
    public async Task StateMachine_EnforcesTransitions_AndVersion()
    {
        await using var host = new TestHost();
        CancellationToken ct = TestContext.CancellationToken;
        (_, CustomerRequestDTO r) = await SeedAsync(host, ct);
        CustomerRequestService svc = host.Get<CustomerRequestService>();
        var user = TestHost.AllPermissions();
        Assert.IsTrue(r.RequestNo.StartsWith("REQ", StringComparison.Ordinal));

        await Assert.ThrowsExactlyAsync<ValidationException>(() => svc.DecideAsync(user, r.Id, new RequestDecisionRequest { Version = r.Version, Status = RequestStatus.Forwarded }, ct));
        CustomerRequestDTO reviewed = await svc.DecideAsync(user, r.Id, new RequestDecisionRequest { Version = r.Version, Status = RequestStatus.UnderReview }, ct);
        await Assert.ThrowsExactlyAsync<ConflictException>(() => svc.DecideAsync(user, r.Id, new RequestDecisionRequest { Version = r.Version, Status = RequestStatus.Approved }, ct));
        CustomerRequestDTO approved = await svc.DecideAsync(user, r.Id, new RequestDecisionRequest { Version = reviewed.Version, Status = RequestStatus.Approved, Comment = "OK" }, ct);
        Assert.AreEqual(RequestStatus.Approved, approved.Status);
        StringAssert.Contains(approved.Description, "OK");
    }

    [TestMethod]
    public async Task Forward_CreatesProjectAndRequirements_IsIdempotent_AndSigned()
    {
        await using var host = new TestHost();
        CancellationToken ct = TestContext.CancellationToken;
        (_, CustomerRequestDTO r) = await SeedAsync(host, ct);
        CustomerRequestService svc = host.Get<CustomerRequestService>();
        var user = TestHost.AllPermissions();
        r = await svc.DecideAsync(user, r.Id, new RequestDecisionRequest { Version = r.Version, Status = RequestStatus.Approved }, ct);

        // Step 1: requirementMgnt ล่ม → ProjectMgnt สำเร็จ, ได้คำเตือน, สถานะยังไม่ Forwarded ครบ
        host.Requirements.Fail = true;
        var options = new ForwardRequestOptions { ToProjectMgnt = true, ToRequirementMgnt = true, ProjectManagerId = 3, ProjectCode = "CRM26" };
        ForwardResultDTO first = await svc.ForwardAsync(user, r.Id, options, ct);
        Assert.HasCount(1, host.Projects.Created);
        Assert.IsNotEmpty(first.Warnings);

        // Step 2: เรียกซ้ำหลังระบบกลับมา → ไม่สร้างโครงการซ้ำ และสร้าง requirement ครบทุกข้อ (priority ถูกส่งต่อ)
        host.Requirements.Fail = false;
        ForwardResultDTO second = await svc.ForwardAsync(user, r.Id, options, ct);
        Assert.HasCount(1, host.Projects.Created, "forwarding must be idempotent");
        Assert.HasCount(2, host.Requirements.Requirements);
        Assert.IsEmpty(second.Warnings);
        CustomerRequestDTO after = await svc.GetAsync(user, r.Id, ct);
        Assert.AreEqual(RequestStatus.Forwarded, after.Status);
        Assert.IsTrue(after.Items.All(i => i.RequirementKey is not null));

        // Step 3: ลายเซ็นตรวจผ่าน
        Assert.IsTrue(await svc.VerifyForwardSignatureAsync(user, r.Id, ct));
    }

    [TestMethod]
    public async Task Incident_ToIssueTracking_CarriesCustomerSeverity()
    {
        await using var host = new TestHost();
        CancellationToken ct = TestContext.CancellationToken;
        var user = TestHost.AllPermissions();
        CustomerDTO c = await host.Get<CustomerService>().RegisterAsync(user, new RegisterCustomerRequest { Kind = CustomerKind.Juristic, LegalName = "B", Country = "TH", TimeZoneId = "Asia/Bangkok", Status = CustomerStatus.Active }, ct);
        CustomerRequestService svc = host.Get<CustomerRequestService>();

        await Assert.ThrowsExactlyAsync<ValidationException>(() => svc.CreateAsync(user, new CreateCustomerRequestRequest { CustomerId = c.Id, Kind = RequestKind.Incident, Title = "down", SeverityCode = "X9" }, ct));
        CustomerRequestDTO r = await svc.CreateAsync(user, new CreateCustomerRequestRequest { CustomerId = c.Id, Kind = RequestKind.Incident, Title = "down", SeverityCode = "S1" }, ct);
        r = await svc.DecideAsync(user, r.Id, new RequestDecisionRequest { Version = r.Version, Status = RequestStatus.Approved }, ct);
        await svc.ForwardAsync(user, r.Id, new ForwardRequestOptions { ToIssueTracking = true, IssueTrackingProjectId = 4, IssueTypeId = 1 }, ct);

        (string code, string? severity, string priority) = host.Issues.Created.Single();
        Assert.AreEqual(c.Code, code);
        Assert.AreEqual("S1", severity);
        Assert.AreEqual("Immediate", priority);
    }
}
