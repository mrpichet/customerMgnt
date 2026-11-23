using Common.Core.Domain;
using Common.Core.Exceptions;
using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Data;
using CustomerMgnt.Data.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CustomerMgnt.Tests.Data;

/// <summary>
/// ทดสอบ ADO ทุกตัวกับฐานข้อมูลจริง (PostgreSQL และ SQL Server) ด้วยชุดขั้นตอนเดียวกัน
/// ข้ามการทดสอบเมื่อไม่ได้ตั้งตัวแปร CUSTOMERMGNT_TEST_POSTGRES / CUSTOMERMGNT_TEST_SQLSERVER
/// ตัวอย่าง: CUSTOMERMGNT_TEST_POSTGRES="Host=localhost;Database=customermgnt;Username=postgres;Password=..."
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class DatabaseIntegrationTests
{
    public TestContext TestContext { get; set; } = default!;

    [TestMethod]
    public Task Postgres_AllAdos_Work() => RunAsync(DatabaseProvider.Postgres, "CUSTOMERMGNT_TEST_POSTGRES");

    [TestMethod]
    public Task SqlServer_AllAdos_Work() => RunAsync(DatabaseProvider.SqlServer, "CUSTOMERMGNT_TEST_SQLSERVER");

    private async Task RunAsync(DatabaseProvider provider, string envVar)
    {
        // Step 0: ไม่มี connection string → ข้าม
        string? cs = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(cs))
        {
            Assert.Inconclusive($"{envVar} is not set.");
        }

        CancellationToken ct = TestContext.CancellationToken;

        // Step 1: DI + migration (รันสองครั้งเพื่อยืนยันว่า migration idempotent)
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = provider.ToString(),
            ["Database:ConnectionString"] = cs,
            ["Database:RunMigrations"] = "true",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCustomerMgntData(config);
        await using ServiceProvider sp = services.BuildServiceProvider();
        foreach (IHostedService migrator in sp.GetServices<IHostedService>().Where(h => h is CustomerMgntMigrator))
        {
            await migrator.StartAsync(ct);
            await migrator.StartAsync(ct);
        }

        string run = Guid.NewGuid().ToString("N")[..8];
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Step 2: customer — insert / get / unique / optimistic concurrency / search (ภาษาไทย) / bulk segment
        ICustomerDAO customers = sp.GetRequiredService<ICustomerDAO>();
        CustomerDTO org = NewCustomer($"ORG-{run}", CustomerKind.Juristic, "บริษัท ทดสอบ จำกัด " + run, now);
        org.Id = await customers.InsertAsync(org, ct);
        CustomerDTO person = NewCustomer($"PER-{run}", CustomerKind.Person, "สมชาย ใจดี " + run, now);
        person.Id = await customers.InsertAsync(person, ct);
        CustomerDTO? loaded = await customers.GetAsync(org.Id, ct);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(org.DisplayName, loaded.DisplayName, "Thai text must round-trip");
        Assert.AreEqual(now.ToUnixTimeMilliseconds(), loaded.CreatedAt.ToUnixTimeMilliseconds());
        await Assert.ThrowsExactlyAsync<ConflictException>(() => customers.InsertAsync(NewCustomer(org.Code, CustomerKind.Juristic, "dup", now), ct));

        loaded.Email = "new@example.com";
        Assert.IsTrue(await customers.UpdateAsync(loaded, ct));
        Assert.IsFalse(await customers.UpdateAsync(loaded, ct), "stale version must fail");

        PagedResultDTO<CustomerDTO> page = await customers.SearchAsync(new CustomerSearchCriteria { Text = "ทดสอบ จำกัด " + run, Page = 1, PageSize = 10 }, ct);
        Assert.AreEqual(1, page.Total);
        PagedResultDTO<CustomerDTO> byKind = await customers.SearchAsync(new CustomerSearchCriteria { Text = run, Kind = CustomerKind.Person, Page = 1, PageSize = 10 }, ct);
        Assert.AreEqual(person.Id, byKind.Items.Single().Id);
        Assert.AreEqual(2, (await customers.GetManyAsync([org.Id, person.Id], ct)).Count);
        Assert.IsGreaterThanOrEqualTo(2, (await customers.ListBatchAsync(0, 10_000, ct)).Count);
        await customers.UpdateSegmentsAsync(new Dictionary<long, string> { [org.Id] = "Champions", [person.Id] = "At Risk" }, ct);
        Assert.AreEqual("Champions", (await customers.GetAsync(org.Id, ct))!.Segment);
        Assert.IsNotNull(await customers.GetByCodeAsync(person.Code, ct));

        // Step 3: relationship + identity
        IRelationshipDAO rel = sp.GetRequiredService<IRelationshipDAO>();
        long relId = await rel.InsertAsync(new PartyRelationshipDTO { PersonId = person.Id, OrganizationId = org.Id, Position = "ผู้จัดการ", ValidFrom = new DateOnly(2024, 1, 1), IsPrimaryContact = true }, ct);
        Assert.IsTrue(await rel.EndAsync(relId, new DateOnly(2025, 12, 31), ct));
        PartyRelationshipDTO r = (await rel.ListByCustomerAsync(person.Id, ct)).Single();
        Assert.AreEqual(new DateOnly(2025, 12, 31), r.ValidTo);
        Assert.AreEqual(org.DisplayName, r.OrganizationName);
        await rel.InsertIdentityAsync(new ChannelIdentityDTO { CustomerId = person.Id, Channel = ChannelKind.Line, ExternalId = "U" + run }, ct);
        Assert.AreEqual(person.Id, (await rel.FindIdentityAsync(ChannelKind.Line, "U" + run, ct))!.CustomerId);

        // Step 4: conversation + idempotent message insert + sentiment aggregate
        IConversationDAO conv = sp.GetRequiredService<IConversationDAO>();
        long convId = await conv.InsertAsync(new ConversationDTO { CustomerId = person.Id, Channel = ChannelKind.Line, ExternalUserId = "U" + run, Status = ConversationStatus.Open, NeedsAttention = true, LastMessageAt = now, CreatedAt = now }, ct);
        ChatMessageDTO msg = new() { ConversationId = convId, Direction = MessageDirection.Inbound, Body = "ระบบล่ม ช้ามาก", ExternalMessageId = "m-" + run, Sentiment = -0.8, SentimentLabel = SentimentLabel.Negative, SentAt = now };
        Assert.IsNotNull(await conv.InsertMessageAsync(msg, ct));
        Assert.IsNull(await conv.InsertMessageAsync(msg, ct), "duplicate webhook delivery must be ignored");
        Assert.AreEqual(1, (await conv.ListMessagesAsync(convId, 50, ct)).Count);
        Assert.IsTrue((await conv.ListAsync(ConversationStatus.Open, person.Id, true, 10, ct)).Any(c => c.Id == convId));
        Assert.IsNotNull(await conv.FindOpenAsync(ChannelKind.Line, "U" + run, ct));
        (double avg, int count) = (await conv.SentimentByCustomerAsync([person.Id], now.AddDays(-1), ct))[person.Id];
        Assert.AreEqual(1, count);
        Assert.AreEqual(-0.8, avg, 1e-9);

        // Step 5: activity + reminder + contact stats
        IActivityDAO act = sp.GetRequiredService<IActivityDAO>();
        long actId = await act.InsertAsync(new ActivityDTO { CustomerId = person.Id, Kind = ActivityKind.Meeting, Subject = "ประชุม", Status = ActivityStatus.Planned, OwnerSubject = "u-" + run, DueAt = now.AddMinutes(10), RemindBeforeMinutes = 30, CreatedAt = now }, ct);
        Assert.IsTrue((await act.ListDueRemindersAsync(now, 100, ct)).Any(a => a.Id == actId));
        Assert.IsTrue(await act.TryMarkReminderSentAsync(actId, now, ct));
        Assert.IsFalse(await act.TryMarkReminderSentAsync(actId, now, ct));
        Assert.AreEqual(1, (await act.ListOpenByOwnerAsync("u-" + run, now.AddDays(1), ct)).Count);
        ActivityDTO done = (await act.GetAsync(actId, ct))!;
        done.Status = ActivityStatus.Done;
        done.CompletedAt = now;
        await act.UpdateAsync(done, ct);
        Assert.AreEqual(1, (await act.ContactStatsAsync([person.Id], ct))[person.Id].Done90Days);
        Assert.AreEqual(0, (await act.ListByCustomerAsync(person.Id, true, 10, ct)).Count);

        // Step 6: request + TOR items + sequence (เพิ่มขึ้นทีละ 1)
        ICustomerRequestDAO req = sp.GetRequiredService<ICustomerRequestDAO>();
        int year = 3000 + Random.Shared.Next(5000);
        int s1 = await req.NextSequenceAsync(year, ct);
        int s2 = await req.NextSequenceAsync(year, ct);
        Assert.AreEqual(1, s1);
        Assert.AreEqual(2, s2);
        CustomerRequestDTO cr = new() { RequestNo = $"REQ-{run}", CustomerId = org.Id, Kind = RequestKind.Tor, Title = "TOR ระบบ CRM", Status = RequestStatus.New, CreatedAt = now, UpdatedAt = now, Version = 1,
            Items = [new() { ItemNo = "1", Text = "รองรับ LINE OA", Priority = "High", Mandatory = true }, new() { ItemNo = "2", Text = "รายงาน", Priority = "Medium" }] };
        cr.Id = await req.InsertAsync(cr, ct);
        CustomerRequestDTO crLoaded = (await req.GetAsync(cr.Id, ct))!;
        Assert.AreEqual(2, crLoaded.Items.Count);
        crLoaded.Status = RequestStatus.Approved;
        crLoaded.Items[0].RequirementKey = "SR-1";
        Assert.IsTrue(await req.UpdateAsync(crLoaded, ct));
        Assert.AreEqual("SR-1", (await req.GetAsync(cr.Id, ct))!.Items.Single(i => i.ItemNo == "1").RequirementKey);
        Assert.AreEqual(1, (await req.ListAsync(org.Id, RequestStatus.Approved, 10, ct)).Count);

        // Step 7: opportunity + stage history + bulk score
        IOpportunityDAO opp = sp.GetRequiredService<IOpportunityDAO>();
        OpportunityDTO o = new() { CustomerId = org.Id, Title = "CRM", Stage = PipelineStage.Qualified, Amount = 1_500_000m, ExpectedCloseDate = new DateOnly(2027, 3, 31), StageChangedAt = now, CreatedAt = now, Version = 1, OwnerSubject = "u-" + run };
        o.Id = await opp.InsertAsync(o, ct);
        OpportunityDTO won = (await opp.GetAsync(o.Id, ct))!;
        won.Stage = PipelineStage.Won;
        won.ClosedAt = now;
        Assert.IsTrue(await opp.UpdateAsync(won, new StageHistoryDTO { OpportunityId = o.Id, FromStage = PipelineStage.Qualified, ToStage = PipelineStage.Won, ChangedBy = "u", ChangedAt = now }, ct));
        Assert.IsTrue((await opp.ListClosedAsync(now.AddDays(-1), ct)).Any(x => x.Id == o.Id));
        Assert.AreEqual(0, (await opp.ListAsync(org.Id, true, null, ct)).Count);
        await opp.UpdateScoresAsync(new Dictionary<long, double> { [o.Id] = 0.42 }, ct);
        Assert.AreEqual(0.42, (await opp.GetAsync(o.Id, ct))!.Score!.Value, 1e-9);

        // Step 8: SLA policy ต่อลูกค้า + seed มาตรฐาน + แทนที่ระดับทั้งชุด
        ISlaDAO sla = sp.GetRequiredService<ISlaDAO>();
        Assert.IsTrue((await sla.ListByCustomerAsync(null, ct)).Any(p => p.Name == "Standard SLA" && p.Levels.Count == 4));
        SlaPolicyDTO policy = new() { CustomerId = org.Id, Name = "Gold " + run, ValidFrom = new DateOnly(2026, 1, 1), TimeZoneId = "Asia/Bangkok", BusinessStart = new TimeOnly(8, 0), BusinessEnd = new TimeOnly(18, 0), WorkingDaysMask = 31, AtRiskThreshold = 0.75, IsActive = true, Version = 1,
            Levels = [new() { Code = "P1", Name = "วิกฤต", Rank = 1, ResponseMinutes = 15, ResolutionMinutes = 120, IssuePriority = "Immediate" }] };
        policy.Id = await sla.InsertAsync(policy, ct);
        SlaPolicyDTO pl = (await sla.GetAsync(policy.Id, ct))!;
        Assert.AreEqual(new TimeOnly(18, 0), pl.BusinessEnd);
        pl.Levels.Add(new SeverityLevelDTO { Code = "P2", Name = "สูง", Rank = 2, ResponseMinutes = 60, ResolutionMinutes = 480, IssuePriority = "Urgent", BusinessHoursOnly = true });
        Assert.IsTrue(await sla.UpdateAsync(pl, ct));
        Assert.AreEqual(2, (await sla.ListByCustomerAsync(org.Id, ct)).Single().Levels.Count);

        // Step 9: external link upsert (ไม่สร้างซ้ำ)
        IExternalLinkDAO links = sp.GetRequiredService<IExternalLinkDAO>();
        ExternalLinkDTO link = new() { CustomerId = org.Id, System = ExternalSystem.ProjectMgnt, EntityType = "project", ExternalId = "42", CreatedAt = now };
        long l1 = await links.UpsertAsync(link, ct);
        link.Title = "โครงการ CRM";
        link.CustomerRequestId = cr.Id;
        long l2 = await links.UpsertAsync(link, ct);
        Assert.AreEqual(l1, l2);
        Assert.AreEqual("โครงการ CRM", (await links.ListAsync(org.Id, ExternalSystem.ProjectMgnt, ct)).Single().Title);
        Assert.AreEqual(1, (await links.ListByRequestAsync(cr.Id, ct)).Count);

        // Step 10: engagement bulk + monthly + contract + catalog + profile
        IEngagementDAO eng = sp.GetRequiredService<IEngagementDAO>();
        int inserted = await eng.InsertManyAsync(
            Enumerable.Range(0, 1200).Select(i => new EngagementEventDTO { CustomerId = org.Id, Kind = i % 2 == 0 ? EngagementKind.Purchase : EngagementKind.WebVisit, Value = 100, ItemCode = "MA", OccurredAt = now.AddDays(-i % 60) }).ToList(), ct);
        Assert.AreEqual(1200, inserted);
        Assert.AreEqual(600, (await eng.ListAsync([org.Id], now.AddDays(-61), [EngagementKind.Purchase], ct)).Count);
        Assert.AreEqual(1200, (await eng.ListAsync([org.Id], now.AddDays(-61), null, ct)).Count);
        Assert.AreEqual(60_000m, (await eng.MonthlyPurchasesAsync(org.Id, now.AddDays(-61), ct)).Values.Sum());
        await eng.InsertContractAsync(new ContractDTO { CustomerId = org.Id, ContractNo = "MA-" + run, Title = "MA", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), AnnualValue = 500_000, Status = ContractStatus.Active }, ct);
        Assert.AreEqual(1, (await eng.ListContractsAsync(org.Id, ct)).Count);
        Assert.IsTrue((await eng.ListContractsEndingAsync(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), ct)).Any(c => c.ContractNo == "MA-" + run));
        await eng.UpsertCatalogAsync(new CatalogItemDTO { Code = "C-" + run, Name = "บริการ", Kind = CatalogItemKind.Service, Price = 10 }, ct);
        await eng.UpsertCatalogAsync(new CatalogItemDTO { Code = "C-" + run, Name = "บริการ 2", Kind = CatalogItemKind.Service, Price = 20 }, ct);
        Assert.AreEqual("บริการ 2", (await eng.ListCatalogAsync(ct)).Single(c => c.Code == "C-" + run).Name);
        _ = await eng.ListClosedContractsAsync(ct);

        IUserProfileDAO profiles = sp.GetRequiredService<IUserProfileDAO>();
        await profiles.UpsertAsync(new UserProfileDTO { Subject = "s-" + run, DisplayName = "A" }, ct);
        Guid hub = Guid.NewGuid();
        await profiles.UpsertAsync(new UserProfileDTO { Subject = "s-" + run, DisplayName = "B", MessageHubUserId = hub }, ct);
        UserProfileDTO prof = (await profiles.GetAsync("s-" + run, ct))!;
        Assert.AreEqual("B", prof.DisplayName);
        Assert.AreEqual(hub, prof.MessageHubUserId);
    }

    private static CustomerDTO NewCustomer(string code, CustomerKind kind, string name, DateTimeOffset now) => new()
    {
        Code = code, Kind = kind, DisplayName = name, Status = CustomerStatus.Active, Country = "TH", TimeZoneId = "Asia/Bangkok",
        CreatedAt = now, UpdatedAt = now, Version = 1, TaxIdHash = kind == CustomerKind.Juristic ? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N") : null,
    };
}
