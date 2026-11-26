using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

// Drill-down (§34): รายชื่อสมาชิก / ธุรกรรมเบื้องหลังตัวเลข
public sealed partial class AnalyticsQueryService
{
    private static readonly List<ColumnDTO> MemberColumns =
    [
        new() { Key = "memberNo", Label = "เลขสมาชิก" }, new() { Key = "displayName", Label = "ชื่อ" }, new() { Key = "phone", Label = "โทรศัพท์" },
        new() { Key = "tierCode", Label = "Tier" }, new() { Key = "status", Label = "สถานะ" }, new() { Key = "registeredAt", Label = "สมัคร", Type = "Date" },
        new() { Key = "lastPurchaseAt", Label = "ซื้อล่าสุด", Type = "Date" }, new() { Key = "bills", Label = "จำนวนบิล", Type = "Count" },
        new() { Key = "sales", Label = "ยอดขายสุทธิ", Type = "Baht" },
    ];

    private static readonly List<ColumnDTO> TransactionColumns =
    [
        new() { Key = "transactionId", Label = "เลขที่ธุรกรรม" }, new() { Key = "receiptNo", Label = "ใบเสร็จ" }, new() { Key = "occurredAt", Label = "เวลา", Type = "Date" },
        new() { Key = "memberNo", Label = "เลขสมาชิก" }, new() { Key = "branchCode", Label = "สาขา" }, new() { Key = "channel", Label = "ช่องทาง" },
        new() { Key = "status", Label = "สถานะ" }, new() { Key = "net", Label = "ยอดขายสุทธิ", Type = "Baht" }, new() { Key = "discount", Label = "ส่วนลด", Type = "Baht" },
    ];

    /// <summary>สมาชิกเบื้องหลังรหัส drill-down (null = รหัสนี้เป็นระดับธุรกรรมเท่านั้น)</summary>
    private async Task<(HashSet<long>? Members, string? Campaign)> DrillTargetAsync(MembershipCaller caller, Ctx c, string metric, CancellationToken ct)
    {
        string[] parts = metric.Split('.');
        List<AnaMemberDayDTO>? cache = null;
        async Task<List<AnaMemberDayDTO>> RowsAsync() => cache ??= await DaysAsync(c, c.F.From, c.F.To, ct).ConfigureAwait(false);
        switch (parts[0])
        {
            case "member" when parts.Length == 2:
                caller.Require(AnaPermissions.MemberRead);
                DateTimeOffset end = AnaTime.Start(c.F.To.AddDays(1));
                return parts[1] switch
                {
                    "total" => ([.. c.Members.Where(m => m.RegisteredAt < end && m.Status != MemberStatus.Closed).Select(m => m.MemberId)], null),
                    "new" => ([.. c.Members.Where(m => InRange(m.RegisteredAt, c.F.From, c.F.To)).Select(m => m.MemberId)], null),
                    "active" => ([.. (await DaysAsync(c, c.F.To.AddDays(-c.S.ActiveDays + 1), c.F.To, ct).ConfigureAwait(false)).Where(r => r.Bills > 0).Select(r => r.MemberId)], null),
                    "buyers" => ([.. (await RowsAsync().ConfigureAwait(false)).Where(r => r.Bills > 0).Select(r => r.MemberId)], null),
                    "repeat" => ([.. (await RowsAsync().ConfigureAwait(false)).GroupBy(r => r.MemberId).Where(g => g.Sum(r => r.Bills) >= 2).Select(g => g.Key)], null),
                    _ => throw AnaErrors.Bad("unknown_metric", $"Unknown drill-down metric '{metric}'."),
                };
            case "group" when parts.Length == 2:
                caller.Require(AnaPermissions.MemberRead);
                HashSet<long> regular = parts[1] == "Regular" ? await RegularAsync(c, c.F.To, ct).ConfigureAwait(false) : [];
                Func<AnaMemberSnapshotDTO, bool> p = GroupPredicate(parts[1], c.F.To, c.S, regular);
                DateTimeOffset gEnd = AnaTime.Start(c.F.To.AddDays(1));
                return ([.. c.Members.Where(m => m.RegisteredAt < gEnd && m.Status != MemberStatus.Closed).Where(p).Select(m => m.MemberId)], null);
            case "rfm" when parts.Length == 2:
                caller.Require(AnaPermissions.MemberRead);
                (List<RfmScore> scores, _) = Score((await RowsAsync().ConfigureAwait(false)), c.F.To);
                return ([.. scores.Where(s => s.Group.Equals(parts[1], StringComparison.OrdinalIgnoreCase) || $"{s.R}-{s.F}" == parts[1]).Select(s => s.MemberId)], null);
            case "sales" when parts.Length == 2:
                caller.Require(AnaPermissions.DashboardRead);
                return ([.. (await RowsAsync().ConfigureAwait(false)).Where(r => r.Bills > 0).Select(r => r.MemberId)], null);
            case "campaign" when parts.Length is 2 or 3:
                CampaignDTO campaign = await VisibleCampaignAsync(caller, parts[1], ct).ConfigureAwait(false);
                return ([.. (await RowsAsync().ConfigureAwait(false)).Where(r => r.Benefits.ContainsKey(campaign.Code)).Select(r => r.MemberId)], campaign.Code);
            default:
                throw AnaErrors.Bad("unknown_metric", $"Unknown drill-down metric '{metric}'.");
        }
    }

    /// <summary>แถวทั้งหมดของ drill-down (ใช้ทั้งหน้าจอ ส่งออก และบันทึก segment)</summary>
    internal async Task<(Ctx C, List<ColumnDTO> Columns, List<Dictionary<string, object?>> Rows, HashSet<long> Members)> DrillRowsAsync(MembershipCaller caller, AnalyticsFilterDTO? filter,
        string metric, DrillLevel level, string? sort, int limit, CancellationToken ct)
    {
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        if (c.Empty)
        {
            return (c, level == DrillLevel.Member ? MemberColumns : TransactionColumns, [], []);
        }

        (HashSet<long>? members, string? campaign) = await DrillTargetAsync(caller, c, metric, ct).ConfigureAwait(false);
        HashSet<long> ids = members ?? [];
        if (level == DrillLevel.Member)
        {
            caller.Require(AnaPermissions.MemberDetail);
            Dictionary<long, (int Bills, decimal Net)> agg = (await DaysAsync(c, c.F.From, c.F.To, ct).ConfigureAwait(false))
                .Where(r => ids.Contains(r.MemberId)).GroupBy(r => r.MemberId).ToDictionary(g => g.Key, g => (g.Sum(r => r.Bills), g.Sum(r => r.Net)));
            IEnumerable<Dictionary<string, object?>> rows = ids.Select(id => c.All.GetValueOrDefault(id)).Where(m => m is not null).Select(m => new Dictionary<string, object?>
            {
                ["memberId"] = m!.MemberId, ["memberNo"] = m.MemberNo, ["displayName"] = m.DisplayName, ["phone"] = m.PhoneMasked, ["tierCode"] = m.TierCode,
                ["status"] = m.Status.ToString(), ["registeredAt"] = AnaTime.Day(m.RegisteredAt).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["lastPurchaseAt"] = m.LastPurchaseAt is DateTimeOffset lp ? AnaTime.Day(lp).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
                ["bills"] = agg.GetValueOrDefault(m.MemberId).Bills, ["sales"] = agg.GetValueOrDefault(m.MemberId).Net,
            });
            rows = Sort(rows, sort, "memberNo");
            return (c, MemberColumns, [.. rows.Take(limit)], ids);
        }

        // ธุรกรรม: อ่าน mbr_transaction ตรง (ตามตัวกรองสาขา/ช่องทางและสมาชิกของรหัส)
        caller.Require(AnaPermissions.DashboardRead);
        var list = new List<Dictionary<string, object?>>();
        (DateTimeOffset from, DateTimeOffset to) = AnaTime.Range(c.F.From, c.F.To);
        long after = 0;
        while (list.Count < limit)
        {
            IReadOnlyList<SalesTransactionEntity> page = await _source.ListTransactionsAsync(from, to, after, 1000, ct).ConfigureAwait(false);
            foreach (SalesTransactionEntity t in page)
            {
                if (!ReadModelRefreshService.Counts(t) || !ids.Contains(t.MemberId)
                    || (c.F.Branches is not null && !c.F.Branches.Contains(t.BranchCode ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    || (c.F.Channels is not null && !c.F.Channels.Contains(t.Channel))
                    || (campaign is not null && !MembershipJson.Read<List<BenefitDTO>>(t.BenefitsJson).Any(b => string.Equals(b.CampaignCode, campaign, StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                list.Add(new Dictionary<string, object?>
                {
                    ["transactionId"] = t.TransactionId, ["receiptNo"] = t.ReceiptNo, ["occurredAt"] = t.OccurredAt, ["memberNo"] = t.MemberNo, ["branchCode"] = t.BranchCode,
                    ["channel"] = t.Channel.ToString(), ["status"] = t.Status.ToString(), ["net"] = t.Subtotal - t.DiscountTotal - ReadModelRefreshService.Returns(t), ["discount"] = t.DiscountTotal,
                });
            }

            if (page.Count < 1000)
            {
                break;
            }

            after = page[^1].Id;
        }

        return (c, TransactionColumns, [.. Sort(list, sort, null).Take(limit)], ids);
    }

    private static IEnumerable<Dictionary<string, object?>> Sort(IEnumerable<Dictionary<string, object?>> rows, string? sort, string? fallback)
    {
        string? key = string.IsNullOrWhiteSpace(sort) ? fallback : sort.TrimStart('-');
        if (key is null)
        {
            return rows;
        }

        bool desc = sort?.StartsWith('-') ?? false;
        Func<Dictionary<string, object?>, object?> sel = r => r.GetValueOrDefault(key);
        var cmp = Comparer<object?>.Create((a, b) => (a, b) switch
        {
            (null, null) => 0,
            (null, _) => -1,
            (_, null) => 1,
            (IComparable x, _) when a.GetType() == b.GetType() => x.CompareTo(b),
            _ => string.CompareOrdinal(a.ToString(), b.ToString()),
        });
        return desc ? rows.OrderByDescending(sel, cmp) : rows.OrderBy(sel, cmp);
    }

    /// <summary>AN-09 drill-down</summary>
    public async Task<AnalyticsResultDTO<DrilldownPageDTO>> DrilldownAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, DrilldownQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(query);
        AnaPermissions.RequireAny(caller);
        (Ctx c, List<ColumnDTO> cols, List<Dictionary<string, object?>> rows, _) = await DrillRowsAsync(caller, filter, query.Metric, query.Level, query.Sort, int.MaxValue, ct).ConfigureAwait(false);
        int page = Math.Max(query.Page, 1);
        var dto = new DrilldownPageDTO
        {
            Metric = query.Metric, Level = query.Level, Columns = cols, Rows = [.. rows.Skip((page - 1) * query.PageSize).Take(query.PageSize)], Page = page, PageSize = query.PageSize,
            Total = rows.Count,
        };
        return await WrapAsync(c, dto, ct).ConfigureAwait(false);
    }

    /// <summary>บันทึกรายชื่อของ drill-down เป็น Segment แบบ snapshot</summary>
    public async Task<SegmentSnapshotResultDTO> SaveDrilldownSegmentAsync(MembershipCaller caller, DrilldownSegmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.SegmentWrite);
        caller.Require(AnaPermissions.MemberDetail);
        (_, _, _, HashSet<long> members) = await DrillRowsAsync(caller, request.Filter, request.Metric, DrillLevel.Member, null, 0, ct).ConfigureAwait(false);
        return await SaveSnapshotSegmentAsync(caller, request.Name, null, [.. members], "ana.drilldown.segment", ct).ConfigureAwait(false);
    }
}
