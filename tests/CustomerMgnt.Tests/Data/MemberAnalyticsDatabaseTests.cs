using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.Membership.Domain;
using Common.Data.Infrastructure;
using CustomerMgnt.Data;
using CustomerMgnt.Data.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CustomerMgnt.Tests.Data;

/// <summary>
/// ทดสอบ DAO ของระบบวิเคราะห์ (ตาราง ana_* และการอ่านต้นทาง mbr_*) กับฐานข้อมูลจริง — ข้ามเมื่อไม่ได้ตั้ง CUSTOMERMGNT_TEST_POSTGRES / CUSTOMERMGNT_TEST_SQLSERVER
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class MemberAnalyticsDatabaseTests
{
    public TestContext TestContext { get; set; } = default!;

    [TestMethod]
    public Task Postgres_AnalyticsAdos_Work() => RunAsync(DatabaseProvider.Postgres, "CUSTOMERMGNT_TEST_POSTGRES");

    [TestMethod]
    public Task SqlServer_AnalyticsAdos_Work() => RunAsync(DatabaseProvider.SqlServer, "CUSTOMERMGNT_TEST_SQLSERVER");

    private async Task RunAsync(DatabaseProvider provider, string envVar)
    {
        string? cs = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(cs))
        {
            Assert.Inconclusive($"{envVar} is not set.");
        }

        CancellationToken ct = TestContext.CancellationToken;
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = provider.ToString(),
            ["Database:ConnectionString"] = cs,
            ["Database:RunMigrations"] = "true",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddCustomerMgntData(config);
        await using ServiceProvider sp = services.BuildServiceProvider();
        foreach (IHostedService migrator in sp.GetServices<IHostedService>().Where(h => h is CustomerMgntMigrator))
        {
            await migrator.StartAsync(ct);
        }

        string run = Guid.NewGuid().ToString("N")[..8];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // วันที่สุ่มในอดีตไกล ๆ เพื่อไม่ชนข้อมูลของรอบอื่น
        DateOnly day = new DateOnly(2001, 1, 1).AddDays(Random.Shared.Next(0, 3000));

        // Step 1: read model — replace แทนที่ทั้งวัน, กรองสาขา (ไม่สนตัวพิมพ์) / ช่องทาง
        IAnaReadModelDAO model = sp.GetRequiredService<IAnaReadModelDAO>();
        var row = new AnaMemberDayDTO { Day = day, MemberId = 1, BranchCode = "BKK01", Channel = SalesChannel.Pos, Bills = 2, Net = 1500.5m, Brands = new() { ["ACME"] = 1000m } };
        await model.ReplaceMemberDaysAsync(day, [row, new AnaMemberDayDTO { Day = day, MemberId = 2, BranchCode = "CNX01", Channel = SalesChannel.Online, Bills = 1, Net = 10m }], ct);
        await model.ReplaceMemberDaysAsync(day, [row], ct);
        IReadOnlyList<AnaMemberDayDTO> days = await model.ListMemberDaysAsync(day, day, null, null, ct);
        Assert.AreEqual(1, days.Count);
        Assert.AreEqual(1500.5m, days[0].Net);
        Assert.AreEqual(1000m, days[0].Brands["ACME"]);
        Assert.AreEqual(1, (await model.ListMemberDaysAsync(day, day, ["bkk01"], [SalesChannel.Pos], ct)).Count);
        Assert.AreEqual(0, (await model.ListMemberDaysAsync(day, day, ["CNX01"], null, ct)).Count);
        Assert.AreEqual(0, (await model.ListMemberDaysAsync(day, day, null, [SalesChannel.Online], ct)).Count);

        await model.ReplacePointsDayAsync(day, [new AnaPointsDayDTO { Day = day, Type = LedgerType.Earn, BranchCode = "BKK01", Points = 120, Entries = 3 }], ct);
        AnaPointsDayDTO p = (await model.ListPointsDaysAsync(day, day, ct)).Single();
        Assert.AreEqual(LedgerType.Earn, p.Type);
        Assert.AreEqual(day, p.Day);
        await model.ReplaceCouponDayAsync(day, [new AnaCouponDayDTO { Day = day, CampaignCode = "C" + run, BatchId = 7, Event = CouponMetricEvent.Redeemed, BranchCode = "", Count = 4 }], ct);
        AnaCouponDayDTO cd = (await model.ListCouponDaysAsync(day, day, ct)).Single();
        Assert.AreEqual(CouponMetricEvent.Redeemed, cd.Event);
        Assert.AreEqual(4, cd.Count);

        // Step 2: dirty day — mark ซ้ำได้, clear เฉพาะที่ mark ก่อนเวลา
        string table = "t-" + run;
        await model.MarkDirtyAsync(table, [day, day.AddDays(1)], ct);
        await model.MarkDirtyAsync(table, [day], ct);
        CollectionAssert.AreEqual(new[] { day, day.AddDays(1) }, (await model.ListDirtyAsync(table, ct)).ToArray());
        await model.ClearDirtyAsync(table, [day], now.AddMinutes(-5), ct);
        Assert.AreEqual(2, (await model.ListDirtyAsync(table, ct)).Count, "marked after cutoff must stay");
        await model.ClearDirtyAsync(table, [day, day.AddDays(1)], DateTimeOffset.UtcNow.AddMinutes(1), ct);
        Assert.AreEqual(0, (await model.ListDirtyAsync(table, ct)).Count);

        // Step 3: เอกสาร — insert ซ้ำ = false, update ตรวจ version, upsert, list ตาม owner, purge
        IAnaDocumentDAO docs = sp.GetRequiredService<IAnaDocumentDAO>();
        string col = "test-" + run;
        Assert.IsTrue(await docs.InsertAsync(col, "a", "u1", new FunnelDTO { Name = "หนึ่ง" }, now, ct));
        Assert.IsFalse(await docs.InsertAsync(col, "a", "u1", new FunnelDTO { Name = "ซ้ำ" }, now, ct));
        Assert.IsTrue(await docs.UpdateAsync(col, "a", "u1", 1, new FunnelDTO { Name = "สอง" }, now, ct));
        Assert.IsFalse(await docs.UpdateAsync(col, "a", "u1", 1, new FunnelDTO { Name = "เก่า" }, now, ct));
        AnaDocument<FunnelDTO> d = (await docs.GetAsync<FunnelDTO>(col, "a", ct))!;
        Assert.AreEqual(2, d.Version);
        Assert.AreEqual("สอง", d.Value.Name);
        await docs.UpsertAsync(col, "b", "u2", new FunnelDTO { Name = "บี" }, now.AddDays(-10), ct);
        await docs.UpsertAsync(col, "b", "u2", new FunnelDTO { Name = "บี2" }, now.AddDays(-10), ct);
        Assert.AreEqual(2, (await docs.GetAsync<FunnelDTO>(col, "b", ct))!.Version);
        Assert.AreEqual(1, (await docs.ListAsync<FunnelDTO>(col, "u2", ct)).Count);
        Assert.AreEqual(2, (await docs.ListAsync<FunnelDTO>(col, null, ct)).Count);
        Assert.AreEqual(1, await docs.PurgeAsync(col, now.AddDays(-1), ct));
        Assert.IsTrue(await docs.DeleteAsync(col, "a", ct));
        Assert.IsNull(await docs.GetAsync<FunnelDTO>(col, "a", ct));

        // Step 4: ข้อความ — ผู้รับซ้ำข้าม, event id ซ้ำ = false
        IAnaMessageDAO msgs = sp.GetRequiredService<IAnaMessageDAO>();
        string code = "CMP" + run;
        var rcp = new MessageRecipientDTO { MessageId = "m-" + run, CampaignCode = code, MemberId = 9, SentAt = now };
        await msgs.InsertRecipientsAsync([rcp, rcp], ct);
        await msgs.InsertRecipientsAsync([rcp], ct);
        Assert.AreEqual(9, (await msgs.GetRecipientAsync(rcp.MessageId, ct))!.MemberId);
        Assert.AreEqual(1, (await msgs.ListRecipientsAsync(code.ToLowerInvariant(), now.AddMinutes(-1), now.AddMinutes(1), ct)).Count);
        var ev = new MessageEventDTO { EventId = Guid.NewGuid(), Type = MessageEventType.Opened, MessageId = rcp.MessageId, CampaignCode = code, MemberId = 9, OccurredAt = now };
        Assert.IsTrue(await msgs.TryInsertEventAsync(ev, ct));
        Assert.IsFalse(await msgs.TryInsertEventAsync(ev, ct));
        MessageEventDTO back = (await msgs.ListEventsAsync(code, now.AddMinutes(-1), now.AddMinutes(1), ct)).Single();
        Assert.AreEqual(ev.EventId, back.EventId);
        Assert.AreEqual(MessageEventType.Opened, back.Type);

        // Step 5: อ่านต้นทาง mbr_* — SQL ต้องรันได้ (ข้อมูลอาจว่าง)
        IAnaSourceDAO src = sp.GetRequiredService<IAnaSourceDAO>();
        DateTimeOffset from = now.AddDays(-1);
        _ = await src.ListTransactionsAsync(from, now, 0, 10, ct);
        _ = await src.ListLedgerAsync(from, now, 0, 10, ct);
        _ = await src.ListCouponEventsAsync(from, now, 0, 10, ct);
        _ = await src.ListCouponsCreatedAsync(from, now, 0, 10, ct);
        _ = await src.ListCouponsExpiredAsync(from, now, 0, 10, ct);
        _ = await src.CountUsableCouponsAsync(from, now, ct);
        _ = await src.CountAvailableCouponsByMemberAsync(now, ct);
        _ = await src.ListMembersAsync(0, 10, ct);
        _ = await src.SumQualifyingSpendAsync(from, ct);
        _ = await src.ListPurchaseStatsAsync(ct);
    }
}
