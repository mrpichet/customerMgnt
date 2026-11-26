using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

/// <summary>
/// คำนวณ read model จากตาราง mbr_* (สูตรข้อ 1–3) — ทำทีละวัน เขียนแทนที่ทั้งวันใน transaction เดียว (รันซ้ำได้ผลเดิม)
/// รอบปกติคำนวณวันนี้ + เมื่อวาน + วันที่ถูกทำเครื่องหมาย (ingest / คืนสินค้า / เปลี่ยนตั้งค่า)
/// </summary>
public sealed partial class ReadModelRefreshService
{
    private const int Page = 1000;
    private readonly IAnaSourceDAO _source;
    private readonly IAnaReadModelDAO _model;
    private readonly IAnaDocumentDAO _docs;
    private readonly ICouponDAO _coupons;
    private readonly ICampaignDAO _campaigns;
    private readonly ITierDAO _tiers;
    private readonly IPointDAO _points;
    private readonly AnaSettingsProvider _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReadModelRefreshService> _log;

    /// <summary>สร้าง</summary>
    public ReadModelRefreshService(IAnaSourceDAO source, IAnaReadModelDAO model, IAnaDocumentDAO docs, ICouponDAO coupons, ICampaignDAO campaigns, ITierDAO tiers,
        IPointDAO points, AnaSettingsProvider settings, TimeProvider clock, ILogger<ReadModelRefreshService> log)
    {
        _source = source;
        _model = model;
        _docs = docs;
        _coupons = coupons;
        _campaigns = campaigns;
        _tiers = tiers;
        _points = points;
        _settings = settings;
        _clock = clock;
        _log = log;
    }

    // ================================================================== รอบอัตโนมัติ

    /// <summary>งานเบื้องหลัง: คำนวณตารางที่ถึงรอบ + งานคำนวณใหม่ที่ผู้ดูแลสั่ง — คืนจำนวนตารางที่คำนวณ</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        AnalyticsSettingsDTO s = await _settings.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        DateOnly today = AnaTime.Day(now);
        int n = await RunQueuedAsync(ct).ConfigureAwait(false);

        // รอบของแต่ละตาราง (ยอดขายใช้รอบที่สั้นกว่าระหว่างยอดขายกับแคมเปญ เพราะรายงานแคมเปญอ่านตารางเดียวกัน)
        var due = new (string Table, int Minutes)[]
        {
            (AnaTables.Coupons, s.RefreshMinutes.Coupon),
            (AnaTables.Points, s.RefreshMinutes.Coupon),
            (AnaTables.Sales, Math.Min(s.RefreshMinutes.Sales, s.RefreshMinutes.Campaign)),
            (AnaTables.Members, s.RefreshMinutes.Member),
        };
        foreach ((string table, int minutes) in due)
        {
            TableStatusDTO st = await StatusAsync(table, ct).ConfigureAwait(false);
            bool first = st.LastRunAt is null;
            if (!first && now - st.LastRunAt!.Value < TimeSpan.FromMinutes(minutes))
            {
                continue;
            }

            // ครั้งแรกคำนวณย้อนหลัง recomputeDays วัน
            var days = new SortedSet<DateOnly> { today, today.AddDays(-1) };
            if (first)
            {
                foreach (DateOnly d in AnaTime.Days(today.AddDays(-s.RecomputeDays), today))
                {
                    days.Add(d);
                }
            }

            await RefreshTableAsync(table, days, minutes, ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    /// <summary>ทำงานคำนวณใหม่ที่รอคิว</summary>
    public async Task<int> RunQueuedAsync(CancellationToken ct)
    {
        int n = 0;
        foreach (AnaDocument<RefreshRunDTO> d in (await _docs.ListAsync<RefreshRunDTO>(AnaCollections.RefreshRun, null, ct).ConfigureAwait(false))
            .Where(d => d.Value.Status == JobStatus.Queued).OrderBy(d => d.Value.RequestedAt))
        {
            RefreshRunDTO run = d.Value;
            run.Status = JobStatus.Running;
            run.StartedAt = _clock.GetUtcNow();
            await _docs.UpsertAsync(AnaCollections.RefreshRun, run.Id.ToString(), run.RequestedBy, run, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            try
            {
                var days = new SortedSet<DateOnly>(AnaTime.Days(run.From, run.To));
                foreach (string table in run.Tables.Count == 0 ? AnaTables.All : run.Tables)
                {
                    await RefreshTableAsync(table, days, null, ct).ConfigureAwait(false);
                }

                run.Status = JobStatus.Done;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Data.Common.DbException or TimeoutException)
            {
                run.Status = JobStatus.Failed;
                run.Error = "Refresh failed. See server log.";
                LogRefreshFailed(_log, ex, "queued");
            }

            run.FinishedAt = _clock.GetUtcNow();
            await _docs.UpsertAsync(AnaCollections.RefreshRun, run.Id.ToString(), run.RequestedBy, run, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    /// <summary>คำนวณตาราง 1 ตาราง (วันที่ระบุ + วันที่ถูกทำเครื่องหมาย) และบันทึกสถานะ</summary>
    public async Task RefreshTableAsync(string table, IReadOnlyCollection<DateOnly> days, int? intervalMinutes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(days);
        DateTimeOffset started = _clock.GetUtcNow();
        TableStatusDTO st = await StatusAsync(table, ct).ConfigureAwait(false);
        st.LastStatus = JobStatus.Running;
        await _docs.UpsertAsync(AnaCollections.TableStatus, table, null, st, started, ct).ConfigureAwait(false);
        try
        {
            var all = new SortedSet<DateOnly>(days);
            foreach (DateOnly d in await _model.ListDirtyAsync(table, ct).ConfigureAwait(false))
            {
                all.Add(d);
            }

            switch (table)
            {
                case AnaTables.Sales:
                    await RefreshSalesAsync(all, ct).ConfigureAwait(false);
                    break;
                case AnaTables.Points:
                    await RefreshPointsAsync(all, ct).ConfigureAwait(false);
                    break;
                case AnaTables.Coupons:
                    await RefreshCouponsAsync(all, ct).ConfigureAwait(false);
                    break;
                case AnaTables.Members:
                    await RefreshSnapshotsAsync(ct).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(table), table, "Unknown analytics table.");
            }

            await _model.ClearDirtyAsync(table, all, started, ct).ConfigureAwait(false);
            st.LastStatus = JobStatus.Done;
            st.Error = null;
            st.Watermark = started;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Data.Common.DbException or TimeoutException)
        {
            st.LastStatus = JobStatus.Failed;
            st.Error = "Refresh failed. See server log.";
            LogRefreshFailed(_log, ex, table);
        }

        st.LastRunAt = started;
        st.NextRunAt = intervalMinutes is int m ? started.AddMinutes(m) : st.NextRunAt;
        st.DirtyDays = (await _model.ListDirtyAsync(table, ct).ConfigureAwait(false)).Count;
        await _docs.UpsertAsync(AnaCollections.TableStatus, table, null, st, _clock.GetUtcNow(), ct).ConfigureAwait(false);
    }

    /// <summary>สถานะของตาราง</summary>
    public async Task<TableStatusDTO> StatusAsync(string table, CancellationToken ct) =>
        (await _docs.GetAsync<TableStatusDTO>(AnaCollections.TableStatus, table, ct).ConfigureAwait(false))?.Value ?? new TableStatusDTO { Table = table };

    // ================================================================== ยอดขาย (ana_member_daily)

    /// <summary>คำนวณยอดของสมาชิกต่อวัน</summary>
    public async Task RefreshSalesAsync(IEnumerable<DateOnly> days, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(days);
        Dictionary<string, AnaProductDTO> products = (await _docs.ListAsync<AnaProductDTO>(AnaCollections.Product, null, ct).ConfigureAwait(false))
            .ToDictionary(d => d.Id, d => d.Value, StringComparer.OrdinalIgnoreCase);
        foreach (DateOnly day in days)
        {
            var rows = new Dictionary<(long, string, SalesChannel), AnaMemberDayDTO>();
            await foreach (SalesTransactionEntity t in TransactionsAsync(day, ct).ConfigureAwait(false))
            {
                if (!Counts(t))
                {
                    continue;
                }

                var key = (t.MemberId, t.BranchCode ?? string.Empty, t.Channel);
                if (!rows.TryGetValue(key, out AnaMemberDayDTO? r))
                {
                    r = new AnaMemberDayDTO { Day = day, MemberId = t.MemberId, BranchCode = key.Item2, Channel = t.Channel };
                    rows[key] = r;
                }

                AddTransaction(r, t, day, products);
            }

            await _model.ReplaceMemberDaysAsync(day, [.. rows.Values], ct).ConfigureAwait(false);
        }
    }

    /// <summary>รายการขายนับในการวิเคราะห์หรือไม่ (Completed / PartiallyReturned ที่ไม่ใช่รายการทดสอบ)</summary>
    public static bool Counts(SalesTransactionEntity t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return !t.IsTest && t.Status is TransactionStatus.Completed or TransactionStatus.PartiallyReturned;
    }

    /// <summary>ยอดคืน (เงิน) ของบิล: สัดส่วนยอดจ่ายที่คืน × (Subtotal − Discount)</summary>
    public static decimal Returns(SalesTransactionEntity t)
    {
        ArgumentNullException.ThrowIfNull(t);
        decimal basis = t.Subtotal - t.DiscountTotal;
        decimal ratio = t.NetPaid > 0 ? Math.Clamp(t.ReturnedPaid / t.NetPaid, 0, 1) : t.Status == TransactionStatus.Returned ? 1 : 0;
        return Math.Round(basis * ratio, 2);
    }

    private static void AddTransaction(AnaMemberDayDTO r, SalesTransactionEntity t, DateOnly day, Dictionary<string, AnaProductDTO> products)
    {
        decimal returns = Returns(t);
        decimal net = t.Subtotal - t.DiscountTotal - returns;
        r.Bills++;
        r.Gross += t.Subtotal;
        r.Discount += t.DiscountTotal;
        r.Returns += returns;
        r.Net += net;
        r.Eligible += t.Eligible - t.ReturnedEligible;
        r.PointsTender += t.PointsTender;
        r.PointsEarned += t.PointsEarned - t.PointsReversed;

        // แบรนด์ / หมวด / ต้นทุน ตามสัดส่วนยอดจ่ายของบรรทัด
        List<TransactionLineDTO> lines = MembershipJson.Read<List<TransactionLineDTO>>(t.LinesJson);
        decimal paid = lines.Sum(l => l.LinePaid);
        var brandsInBill = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TransactionLineDTO l in lines)
        {
            decimal share = paid > 0 ? l.LinePaid / paid : lines.Count > 0 ? 1m / lines.Count : 0;
            decimal lineNet = Math.Round(net * share, 2);
            r.Items += l.Qty - l.ReturnedQty;
            products.TryGetValue(l.ItemCode, out AnaProductDTO? p);
            string brand = p?.Brand ?? string.Empty;
            string category = l.Category ?? p?.Category ?? string.Empty;
            r.Brands[brand] = r.Brands.GetValueOrDefault(brand) + lineNet;
            brandsInBill.Add(brand);
            r.Categories[category] = r.Categories.GetValueOrDefault(category) + lineNet;
            if (p?.Costs.Where(c => c.EffectiveFrom <= day).OrderByDescending(c => c.EffectiveFrom).FirstOrDefault() is ProductCostDTO cost)
            {
                r.Cost += Math.Round(cost.UnitCost * Math.Max(l.Qty - l.ReturnedQty, 0), 2);
                r.CostedNet += lineNet;
            }
        }

        foreach (string b in brandsInBill)
        {
            r.BrandBills[b] = r.BrandBills.GetValueOrDefault(b) + 1;
        }

        // สิทธิ์แคมเปญที่ใช้ในบิล
        foreach (IGrouping<string, BenefitDTO> g in MembershipJson.Read<List<BenefitDTO>>(t.BenefitsJson).Where(b => !string.IsNullOrEmpty(b.CampaignCode))
            .GroupBy(b => b.CampaignCode, StringComparer.OrdinalIgnoreCase))
        {
            if (!r.Benefits.TryGetValue(g.Key, out AnaBenefitAggDTO? a))
            {
                a = new AnaBenefitAggDTO();
                r.Benefits[g.Key] = a;
            }

            a.Bills++;
            a.Sales += net;
            a.Discount += g.Sum(b => b.Amount);
            a.BonusPoints += g.Sum(b => b.BonusPoints);
        }
    }

    private async IAsyncEnumerable<SalesTransactionEntity> TransactionsAsync(DateOnly day, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        (DateTimeOffset from, DateTimeOffset to) = AnaTime.Range(day, day);
        long after = 0;
        while (true)
        {
            IReadOnlyList<SalesTransactionEntity> page = await _source.ListTransactionsAsync(from, to, after, Page, ct).ConfigureAwait(false);
            foreach (SalesTransactionEntity t in page)
            {
                yield return t;
            }

            if (page.Count < Page)
            {
                yield break;
            }

            after = page[^1].Id;
        }
    }

    // ================================================================== คะแนน (ana_points_daily)

    /// <summary>คำนวณคะแนนต่อวัน/ชนิด/สาขา (สาขาจากรายการขายของวันเดียวกัน)</summary>
    public async Task RefreshPointsAsync(IEnumerable<DateOnly> days, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(days);
        foreach (DateOnly day in days)
        {
            Dictionary<string, string> branchOf = await BranchByTransactionAsync(day, ct).ConfigureAwait(false);
            (DateTimeOffset from, DateTimeOffset to) = AnaTime.Range(day, day);
            var rows = new Dictionary<(LedgerType, string), AnaPointsDayDTO>();
            long after = 0;
            while (true)
            {
                IReadOnlyList<PointLedgerDTO> page = await _source.ListLedgerAsync(from, to, after, Page, ct).ConfigureAwait(false);
                foreach (PointLedgerDTO l in page)
                {
                    string branch = l.TransactionId is not null && branchOf.TryGetValue(l.TransactionId, out string? b) ? b : string.Empty;
                    var key = (l.Type, branch);
                    if (!rows.TryGetValue(key, out AnaPointsDayDTO? r))
                    {
                        r = new AnaPointsDayDTO { Day = day, Type = l.Type, BranchCode = branch };
                        rows[key] = r;
                    }

                    r.Points += l.Points;
                    r.Entries++;
                }

                if (page.Count < Page)
                {
                    break;
                }

                after = page[^1].Id;
            }

            await _model.ReplacePointsDayAsync(day, [.. rows.Values], ct).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, string>> BranchByTransactionAsync(DateOnly day, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (SalesTransactionEntity t in TransactionsAsync(day, ct).ConfigureAwait(false))
        {
            map[t.TransactionId] = t.BranchCode ?? string.Empty;
        }

        return map;
    }

    // ================================================================== คูปอง (ana_coupon_daily)

    /// <summary>คำนวณคูปองต่อวัน/แคมเปญ/ชุด/เหตุการณ์/สาขา</summary>
    public async Task RefreshCouponsAsync(IEnumerable<DateOnly> days, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(days);
        var campaignCodes = new Dictionary<long, string>();
        foreach (DateOnly day in days)
        {
            Dictionary<string, string> branchOf = await BranchByTransactionAsync(day, ct).ConfigureAwait(false);
            (DateTimeOffset from, DateTimeOffset to) = AnaTime.Range(day, day);
            var counts = new Dictionary<(long Campaign, long Batch, CouponMetricEvent Event, string Branch), int>();

            void Add(CouponDTO c, CouponMetricEvent e, string branch)
            {
                var k = (c.CampaignId, c.BatchId, e, branch);
                counts[k] = counts.GetValueOrDefault(k) + 1;
            }

            // Step 1: สร้าง (ออก) และหมดอายุ — จากตารางคูปอง
            await foreach (CouponDTO c in PagedAsync((after, take) => _source.ListCouponsCreatedAsync(from, to, after, take, ct), x => x.Id).ConfigureAwait(false))
            {
                Add(c, CouponMetricEvent.Issued, string.Empty);
            }

            await foreach (CouponDTO c in PagedAsync((after, take) => _source.ListCouponsExpiredAsync(from, to, after, take, ct), x => x.Id).ConfigureAwait(false))
            {
                Add(c, CouponMetricEvent.Expired, string.Empty);
            }

            // Step 2: แจกจ่าย / ใช้ / คืนสิทธิ์ / ยกเลิก — จากประวัติคูปอง
            var events = new List<CouponEventDTO>();
            await foreach (CouponEventDTO e in PagedAsync((after, take) => _source.ListCouponEventsAsync(from, to, after, take, ct), x => x.Id).ConfigureAwait(false))
            {
                events.Add(e);
            }

            Dictionary<string, CouponDTO> byCode = [];
            foreach (string[] chunk in events.Select(e => e.Code).Distinct(StringComparer.Ordinal).Chunk(500))
            {
                foreach (CouponDTO c in await _coupons.GetByCodesAsync(chunk, ct).ConfigureAwait(false))
                {
                    byCode[c.Code] = c;
                }
            }

            foreach (CouponEventDTO e in events)
            {
                if (!byCode.TryGetValue(e.Code, out CouponDTO? c))
                {
                    continue;
                }

                CouponMetricEvent? ev = e switch
                {
                    { ToStatus: CouponStatus.Redeemed } => CouponMetricEvent.Redeemed,
                    { ToStatus: CouponStatus.Reversed } => CouponMetricEvent.Reversed,
                    { ToStatus: CouponStatus.Cancelled } => CouponMetricEvent.Cancelled,
                    { FromStatus: CouponStatus.Available, ToStatus: CouponStatus.Available } when e.Reference is not null && !e.Reference.StartsWith("merge:", StringComparison.Ordinal)
                        => CouponMetricEvent.Distributed,
                    _ => null,
                };
                if (ev is CouponMetricEvent x)
                {
                    string branch = x == CouponMetricEvent.Redeemed && e.Reference is not null && branchOf.TryGetValue(e.Reference, out string? b) ? b : string.Empty;
                    Add(c, x, branch);
                }
            }

            // Step 3: รหัสแคมเปญ
            long[] missing = [.. counts.Keys.Select(k => k.Campaign).Distinct().Where(id => !campaignCodes.ContainsKey(id))];
            if (missing.Length > 0)
            {
                foreach (CampaignDTO c in await _campaigns.ListByIdsAsync(missing, ct).ConfigureAwait(false))
                {
                    campaignCodes[c.Id] = c.Code;
                }
            }

            await _model.ReplaceCouponDayAsync(day, [.. counts.Select(kv => new AnaCouponDayDTO
            {
                Day = day, CampaignCode = campaignCodes.GetValueOrDefault(kv.Key.Campaign, string.Empty), BatchId = kv.Key.Batch, Event = kv.Key.Event,
                BranchCode = kv.Key.Branch, Count = kv.Value,
            })], ct).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<T> PagedAsync<T>(Func<long, int, Task<IReadOnlyList<T>>> load, Func<T, long> id)
    {
        long after = 0;
        while (true)
        {
            IReadOnlyList<T> page = await load(after, Page).ConfigureAwait(false);
            foreach (T x in page)
            {
                yield return x;
            }

            if (page.Count < Page)
            {
                yield break;
            }

            after = id(page[^1]);
        }
    }

    // ================================================================== สมาชิก (ana_member_snapshot)

    /// <summary>คำนวณ snapshot ของสมาชิกทุกคน</summary>
    public async Task RefreshSnapshotsAsync(CancellationToken ct)
    {
        AnalyticsSettingsDTO s = await _settings.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        DateOnly today = AnaTime.Day(now);

        // Step 1: ข้อมูลรวมต่อสมาชิก
        Dictionary<long, MemberPurchaseStatsDTO> stats = (await _source.ListPurchaseStatsAsync(ct).ConfigureAwait(false)).ToDictionary(x => x.MemberId);
        IReadOnlyDictionary<long, decimal> spend = await _source.SumQualifyingSpendAsync(now.AddMonths(-12), ct).ConfigureAwait(false);
        IReadOnlyDictionary<long, int> coupons = await _source.CountAvailableCouponsByMemberAsync(now, ct).ConfigureAwait(false);
        Dictionary<long, int> expiring = (await _points.ListExpiringForNoticeAsync(now, now.AddDays(s.ExpiringDays), ct).ConfigureAwait(false))
            .GroupBy(x => x.MemberId).ToDictionary(g => g.Key, g => g.Sum(x => x.Points));
        List<TierDTO> tiers = [.. (await _tiers.ListAsync(ct).ConfigureAwait(false)).Where(t => t.IsActive).OrderBy(t => t.Rank)];

        // สาขา/ช่องทางที่ซื้อบ่อยที่สุดใน 12 เดือน
        var home = new Dictionary<long, Dictionary<(string, SalesChannel), int>>();
        foreach (AnaMemberDayDTO d in await _model.ListMemberDaysAsync(today.AddDays(-365), today, null, null, ct).ConfigureAwait(false))
        {
            if (!home.TryGetValue(d.MemberId, out Dictionary<(string, SalesChannel), int>? m))
            {
                m = [];
                home[d.MemberId] = m;
            }

            m[(d.BranchCode, d.Channel)] = m.GetValueOrDefault((d.BranchCode, d.Channel)) + d.Bills;
        }

        // Step 2: snapshot ทีละหน้า
        var rows = new List<AnaMemberSnapshotDTO>();
        long after = 0;
        while (true)
        {
            IReadOnlyList<MemberEntity> page = await _source.ListMembersAsync(after, Page, ct).ConfigureAwait(false);
            foreach (MemberEntity m in page)
            {
                int rank = tiers.FirstOrDefault(t => string.Equals(t.Code, m.TierCode, StringComparison.OrdinalIgnoreCase))?.Rank ?? 0;
                TierDTO? current = tiers.FirstOrDefault(t => string.Equals(t.Code, m.TierCode, StringComparison.OrdinalIgnoreCase));
                TierDTO? next = tiers.FirstOrDefault(t => t.Rank > rank);
                stats.TryGetValue(m.Id, out MemberPurchaseStatsDTO? st);
                (string Branch, SalesChannel Channel)? fav = home.TryGetValue(m.Id, out Dictionary<(string, SalesChannel), int>? h) && h.Count > 0
                    ? h.OrderByDescending(x => x.Value).ThenBy(x => x.Key.Item1, StringComparer.Ordinal).First().Key : null;
                rows.Add(new AnaMemberSnapshotDTO
                {
                    MemberId = m.Id, MemberNo = m.MemberNo, DisplayName = $"{m.FirstName} {(m.LastName.Length > 0 ? m.LastName[..1] + "." : string.Empty)}".Trim(),
                    PhoneMasked = m.PhoneMasked, RegisteredAt = m.CreatedAt, RegisteredChannel = m.RegisteredChannel, SourceCampaignCode = m.SourceCampaignCode,
                    Status = m.Status, Verified = m.Status != MemberStatus.PendingVerification, TierCode = m.TierCode, TierReviewAt = m.TierReviewAt,
                    NextTierThreshold = next?.UpgradeThreshold, RetainThreshold = current?.RetainThreshold, Spend12m = spend.GetValueOrDefault(m.Id),
                    FirstPurchaseAt = st?.FirstAt, LastPurchaseAt = st?.LastAt, BillsTotal = st?.Bills ?? 0, PointsAvailable = m.PointsAvailable,
                    PointsExpiringSoon = expiring.GetValueOrDefault(m.Id), UnusedCoupons = coupons.GetValueOrDefault(m.Id),
                    HomeBranch = fav?.Branch is { Length: > 0 } hb ? hb : null, HomeChannel = fav?.Channel, RefreshedAt = now,
                });
            }

            if (page.Count < Page)
            {
                break;
            }

            after = page[^1].Id;
        }

        await _model.ReplaceSnapshotsAsync(rows, ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 4300, Level = LogLevel.Error, Message = "Analytics refresh failed ({Table})")]
    private static partial void LogRefreshFailed(ILogger logger, Exception ex, string table);
}
