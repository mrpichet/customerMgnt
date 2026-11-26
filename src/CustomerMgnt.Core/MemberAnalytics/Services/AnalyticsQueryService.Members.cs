using Common.Core.Exceptions;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

// วิเคราะห์สมาชิก (§31, สูตรข้อ 2)
public sealed partial class AnalyticsQueryService
{
    /// <summary>กลุ่ม RFM (ตรวจตามลำดับ กฎแรกที่ตรง = ชื่อกลุ่ม)</summary>
    internal static readonly (string Code, string Label, string Rule, Func<int, int, bool> Match)[] RfmGroups =
    [
        ("Champions", "ลูกค้าดีที่สุด", "R≥4 และ F≥4", (r, f) => r >= 4 && f >= 4),
        ("Loyal", "ลูกค้าประจำ", "R≥3 และ F≥3", (r, f) => r >= 3 && f >= 3),
        ("NewCustomers", "ลูกค้าใหม่", "R≥4 และ F≤1", (r, f) => r >= 4 && f <= 1),
        ("Potential", "มีแนวโน้มซื้อเพิ่ม", "R≥3 และ F≤2", (r, f) => r >= 3 && f <= 2),
        ("AtRisk", "เสี่ยงหาย", "R≤2 และ F≥3", (r, f) => r <= 2 && f >= 3),
        ("Hibernating", "ห่างหาย", "R≤2 และ F≤2", (r, f) => r <= 2 && f <= 2),
        ("Others", "อื่น ๆ", "ที่เหลือ", (_, _) => true),
    ];

    private static readonly Dictionary<string, string> GroupLabels = new()
    {
        ["New"] = "สมัครใหม่", ["NoPurchaseYet"] = "สมัครแต่ยังไม่ซื้อ", ["FirstPurchase"] = "ซื้อครั้งแรก", ["Repeat"] = "ซื้อซ้ำ", ["Regular"] = "ซื้อสม่ำเสมอ",
        ["Active"] = "ยังซื้ออยู่", ["AtRisk"] = "เสี่ยงหาย", ["Lapsed"] = "ห่างหาย", ["PointsExpiringSoon"] = "คะแนนใกล้หมดอายุ", ["UnusedCoupon"] = "มีคูปองแต่ยังไม่ใช้",
        ["TierReviewSoon"] = "Tier ใกล้หมดอายุ", ["NearTierUpgrade"] = "ใกล้เลื่อน Tier", ["TierAtRisk"] = "เสี่ยงเสีย Tier",
    };

    /// <summary>เงื่อนไขของกลุ่มการใช้งาน ณ วันอ้างอิง</summary>
    internal static Func<AnaMemberSnapshotDTO, bool> GroupPredicate(string code, DateOnly d2, AnalyticsSettingsDTO s, IReadOnlySet<long> regular)
    {
        DateTimeOffset end = AnaTime.Start(d2.AddDays(1));
        int? Since(DateTimeOffset? at) => at is DateTimeOffset x ? d2.DayNumber - AnaTime.Day(x).DayNumber : null;
        return code switch
        {
            "New" => m => m.RegisteredAt < end && Since(m.RegisteredAt) < s.FirstPurchaseDays,
            "NoPurchaseYet" => m => m.BillsTotal == 0,
            "FirstPurchase" => m => m.BillsTotal == 1,
            "Repeat" => m => m.BillsTotal >= 2,
            "Regular" => m => regular.Contains(m.MemberId),
            "Active" => m => Since(m.LastPurchaseAt) is int x && x <= s.ActiveDays,
            "AtRisk" => m => Since(m.LastPurchaseAt) is int x && x > s.ActiveDays && x <= s.AtRiskDays,
            "Lapsed" => m => Since(m.LastPurchaseAt) is int x && x > s.AtRiskDays,
            "PointsExpiringSoon" => m => m.PointsExpiringSoon > 0,
            "UnusedCoupon" => m => m.UnusedCoupons > 0,
            "TierReviewSoon" => m => m.TierReviewAt is DateTimeOffset r && r >= AnaTime.Start(d2) && Since(r) >= -s.TierReviewDays,
            "NearTierUpgrade" => m => m.NextTierThreshold is decimal t && t > 0 && m.Spend12m >= t * s.NearUpgradePct / 100m,
            "TierAtRisk" => m => m.TierReviewAt is DateTimeOffset r && r >= AnaTime.Start(d2) && Since(r) >= -s.TierRiskDays && m.RetainThreshold is decimal k && m.Spend12m < k,
            _ => throw AnaErrors.Bad("unknown_metric", $"Unknown activity group '{code}'."),
        };
    }

    /// <summary>สมาชิกที่ซื้อสม่ำเสมอ: ≥ N ครั้งใน W วัน และมีการซื้ออย่างน้อย K ใน 6 เดือนล่าสุด</summary>
    private async Task<HashSet<long>> RegularAsync(Ctx c, DateOnly d2, CancellationToken ct)
    {
        DateOnly from = new[] { d2.AddDays(-c.S.RegularWindowDays + 1), d2.AddMonths(-6).AddDays(1) }.Min();
        List<AnaMemberDayDTO> rows = await DaysAsync(c, from, d2, ct).ConfigureAwait(false);
        DateOnly windowFrom = d2.AddDays(-c.S.RegularWindowDays + 1);
        DateOnly monthsFrom = d2.AddMonths(-6).AddDays(1);
        return [.. rows.GroupBy(r => r.MemberId).Where(g =>
                g.Where(r => r.Day >= windowFrom).Sum(r => r.Bills) >= c.S.RegularMinPurchases
                && g.Where(r => r.Day >= monthsFrom && r.Bills > 0).Select(r => AnaTime.MonthKey(r.Day)).Distinct().Count() >= c.S.RegularMinMonths)
            .Select(g => g.Key)];
    }

    private async Task<ActivityGroupsDTO> GroupsAsync(Ctx c, CancellationToken ct)
    {
        DateOnly d2 = c.F.To;
        HashSet<long> regular = await RegularAsync(c, d2, ct).ConfigureAwait(false);
        var dto = new ActivityGroupsDTO
        {
            Thresholds = new()
            {
                ["activeDays"] = c.S.ActiveDays, ["atRiskDays"] = c.S.AtRiskDays, ["regularMinPurchases"] = c.S.RegularMinPurchases, ["regularMinMonths"] = c.S.RegularMinMonths,
                ["expiringDays"] = c.S.ExpiringDays, ["tierReviewDays"] = c.S.TierReviewDays, ["nearUpgradePct"] = c.S.NearUpgradePct,
            },
        };
        DateTimeOffset end = AnaTime.Start(d2.AddDays(1));
        List<AnaMemberSnapshotDTO> population = [.. c.Members.Where(m => m.RegisteredAt < end && m.Status != MemberStatus.Closed)];
        foreach ((string code, string label) in GroupLabels)
        {
            Func<AnaMemberSnapshotDTO, bool> p = GroupPredicate(code, d2, c.S, regular);
            dto.Groups.Add(new ActivityGroupDTO
            {
                Code = code, Label = label, Drilldown = "group." + code,
                Count = c.M("group." + code, population.Count(p), MetricUnit.Count, code is "New" or "NoPurchaseYet" or "FirstPurchase" or "Repeat" ? MetricKind.Fact : MetricKind.Computed,
                    drilldown: "group." + code),
            });
        }

        return dto;
    }

    /// <summary>AN-04 กลุ่มการใช้งาน (วันอ้างอิง = to)</summary>
    public async Task<AnalyticsResultDTO<ActivityGroupsDTO>> ActivityGroupsAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.MemberRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        return await WrapAsync(c, await GroupsAsync(c, ct).ConfigureAwait(false), ct, AnaTables.Members, AnaTables.Sales).ConfigureAwait(false);
    }

    /// <summary>คะแนน RFM ของผู้ซื้อในช่วง</summary>
    internal sealed record RfmScore(long MemberId, int R, int F, int Bills, decimal Monetary, string Group);

    private static (List<RfmScore> Scores, RfmBoundariesDTO Bounds) Score(List<AnaMemberDayDTO> rows, DateOnly d2)
    {
        var raw = rows.GroupBy(r => r.MemberId).Select(g => (Id: g.Key, R: (decimal)(d2.DayNumber - g.Where(r => r.Bills > 0).Select(r => r.Day.DayNumber).DefaultIfEmpty(d2.DayNumber).Max()),
            F: (decimal)g.Sum(r => r.Bills), M: g.Sum(r => r.Net))).Where(x => x.F > 0).ToList();
        var b = new RfmBoundariesDTO
        {
            R = AnaMath.Quintiles([.. raw.Select(x => x.R).Order()]), F = AnaMath.Quintiles([.. raw.Select(x => x.F).Order()]),
            M = AnaMath.Quintiles([.. raw.Select(x => x.M).Order()]),
        };
        List<RfmScore> scores = [.. raw.Select(x =>
        {
            int r = AnaMath.Score(x.R, b.R, invert: true), f = AnaMath.Score(x.F, b.F, invert: false);
            return new RfmScore(x.Id, r, f, (int)x.F, x.M, RfmGroups.First(g => g.Match(r, f)).Code);
        })];
        return (scores, b);
    }

    /// <summary>AN-05 RFM</summary>
    public async Task<AnalyticsResultDTO<RfmDTO>> RfmAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.MemberRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        (List<RfmScore> scores, RfmBoundariesDTO bounds) = Score(await DaysAsync(c, c.F.From, c.F.To, ct).ConfigureAwait(false), c.F.To);
        decimal total = scores.Sum(x => x.Monetary);
        var dto = new RfmDTO
        {
            Boundaries = bounds, MembersScored = scores.Count,
            InsufficientReason = c.Empty ? AnaFormula.ScopeEmpty : scores.Count < 5 ? AnaFormula.GroupTooSmall : null,
        };
        for (int r = 5; r >= 1; r--)
        {
            for (int f = 1; f <= 5; f++)
            {
                List<RfmScore> cell = [.. scores.Where(x => x.R == r && x.F == f)];
                dto.Cells.Add(new RfmCellDTO
                {
                    R = r, F = f, Members = cell.Count, AvgMonetary = cell.Count == 0 ? 0 : Math.Round(cell.Average(x => x.Monetary), 2), Group = RfmGroups.First(g => g.Match(r, f)).Code,
                });
            }
        }

        dto.Groups = [.. RfmGroups.Select(g => new RfmGroupDTO
        {
            Code = g.Code, Label = g.Label, Rule = g.Rule, Members = scores.Count(x => x.Group == g.Code),
            SalesShare = AnaMath.Pct(scores.Where(x => x.Group == g.Code).Sum(x => x.Monetary), total),
        })];
        return await WrapAsync(c, dto, ct, AnaTables.Sales).ConfigureAwait(false);
    }

    /// <summary>AN-05 บันทึกช่อง/กลุ่ม RFM เป็น Segment แบบ snapshot</summary>
    public async Task<SegmentSnapshotResultDTO> SaveRfmSegmentAsync(MembershipCaller caller, RfmSegmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(AnaPermissions.MemberRead);
        caller.Require(MembershipPermissions.SegmentWrite);
        ValidationException.ThrowIf(request.Cells.Count == 0 && request.Groups.Count == 0, "cells", "Choose at least one cell or group.");
        Ctx c = await ContextAsync(caller, request.Filter, ct).ConfigureAwait(false);
        (List<RfmScore> scores, _) = Score(await DaysAsync(c, c.F.From, c.F.To, ct).ConfigureAwait(false), c.F.To);
        var cells = new HashSet<string>(request.Cells, StringComparer.Ordinal);
        var groups = new HashSet<string>(request.Groups, StringComparer.OrdinalIgnoreCase);
        List<long> ids = [.. scores.Where(x => cells.Contains($"{x.R}-{x.F}") || groups.Contains(x.Group)).Select(x => x.MemberId)];
        return await SaveSnapshotSegmentAsync(caller, request.Name, request.Description, ids, "ana.rfm.segment", ct).ConfigureAwait(false);
    }

    private async Task<SegmentSnapshotResultDTO> SaveSnapshotSegmentAsync(MembershipCaller caller, string name, string? description, List<long> ids, string action, CancellationToken ct)
    {
        AnalyticsSettingsDTO s = await _settings.GetAsync(ct).ConfigureAwait(false);
        if (ids.Count > s.ExportMaxRows)
        {
            throw AnaErrors.Bad("too_many_rows", $"A snapshot segment may hold at most {s.ExportMaxRows} members.");
        }

        DateTimeOffset now = _clock.GetUtcNow();
        var seg = new SegmentDTO
        {
            Name = name.Trim(), Description = description, RefreshDaily = false, Rule = new SegmentRuleDTO { Op = "And", Items = [] }, CreatedBy = caller.User.Subject, Version = 1,
        };
        long id = await _segments.InsertAsync(seg, ct).ConfigureAwait(false);
        await _segments.ReplaceMembersAsync(id, ids, now, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, action, $"segment:{id}", null, new { name = seg.Name, members = ids.Count }, now)], ct).ConfigureAwait(false);
        return new SegmentSnapshotResultDTO { SegmentId = id, MemberCount = ids.Count, SnapshotAt = now };
    }

    /// <summary>AN-06 Cohort</summary>
    public async Task<AnalyticsResultDTO<CohortDTO>> CohortsAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CohortBy by, CohortMeasure measure, int months, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.MemberRead);
        ValidationException.ThrowIf(months is < 1 or > 24, "months", "months must be between 1 and 24.");
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        var dto = new CohortDTO { CohortBy = by, Measure = measure };
        DateOnly firstMonth = new(c.F.From.Year, c.F.From.Month, 1);
        DateOnly lastMonth = new(c.F.To.Year, c.F.To.Month, 1);

        // Step 1: สมาชิกของแต่ละรุ่นและเดือนเริ่มต้น
        var members = new List<(string Cohort, long Id, DateOnly Start)>();
        foreach (AnaMemberSnapshotDTO m in c.Members)
        {
            DateTimeOffset? startAt = by == CohortBy.FirstPurchase ? m.FirstPurchaseAt : m.RegisteredAt;
            if (startAt is not DateTimeOffset sa)
            {
                continue;
            }

            DateOnly d = AnaTime.Day(sa);
            DateOnly month = new(d.Year, d.Month, 1);
            if (month < firstMonth || month > lastMonth || (by == CohortBy.SourceCampaign && m.SourceCampaignCode is null))
            {
                continue;
            }

            members.Add((by == CohortBy.SourceCampaign ? m.SourceCampaignCode! : AnaTime.MonthKey(month), m.MemberId, month));
        }

        // Step 2: ยอดต่อสมาชิกต่อเดือน
        DateOnly loadTo = new[] { lastMonth.AddMonths(months).AddDays(-1), c.Today.AddDays(-1) }.Min();
        var perMonth = new Dictionary<(long, string), (bool Bought, decimal Net)>();
        if (members.Count > 0 && loadTo >= firstMonth)
        {
            var ids = new HashSet<long>(members.Select(x => x.Id));
            foreach (AnaMemberDayDTO r in await DaysAsync(c, firstMonth, loadTo, ct).ConfigureAwait(false))
            {
                if (!ids.Contains(r.MemberId))
                {
                    continue;
                }

                var k = (r.MemberId, AnaTime.MonthKey(r.Day));
                (bool b, decimal n) = perMonth.GetValueOrDefault(k);
                perMonth[k] = (b || r.Bills > 0, n + r.Net);
            }
        }

        // Step 3: ค่าแต่ละช่อง (เดือนที่ยังไม่ครบ = null)
        DateOnly currentMonth = new(c.Today.Year, c.Today.Month, 1);
        foreach (IGrouping<string, (string Cohort, long Id, DateOnly Start)> g in members.GroupBy(x => x.Cohort).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var row = new CohortRowDTO { Cohort = g.Key, Size = g.Count() };
            for (int k = 0; k < months; k++)
            {
                // รุ่นแบบแคมเปญ: เดือนเริ่มของแต่ละคนต่างกัน — ช่องว่างเมื่อมีสมาชิกคนใดยังไม่ครบเดือนนั้น
                if (g.Any(x => x.Start.AddMonths(k) >= currentMonth))
                {
                    row.Values.Add(null);
                    continue;
                }

                decimal v = measure == CohortMeasure.Retention
                    ? AnaMath.Pct(g.Count(x => perMonth.GetValueOrDefault((x.Id, AnaTime.MonthKey(x.Start.AddMonths(k)))).Bought), row.Size) ?? 0
                    : Math.Round(g.Sum(x => Enumerable.Range(0, k + 1).Sum(j => perMonth.GetValueOrDefault((x.Id, AnaTime.MonthKey(x.Start.AddMonths(j)))).Net)) / row.Size, 2);
                row.Values.Add(c.Empty ? null : v);
            }

            dto.Rows.Add(row);
        }

        return await WrapAsync(c, dto, ct, AnaTables.Sales, AnaTables.Members).ConfigureAwait(false);
    }

    /// <summary>AN-07 การซื้อซ้ำ</summary>
    public async Task<AnalyticsResultDTO<RepeatPurchaseDTO>> RepeatPurchaseAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, int? firstPurchaseDays, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.MemberRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        int n = firstPurchaseDays ?? c.S.FirstPurchaseDays;
        ValidationException.ThrowIf(n is < 1 or > 365, "firstPurchaseDays", "firstPurchaseDays must be between 1 and 365.");
        AppliedFilterDTO a = c.F.Applied;
        DateOnly loadFrom = (a.CompareFrom is DateOnly cf0 && cf0 < c.F.From ? cf0 : c.F.From).AddDays(-365);
        DateOnly loadTo = new[] { c.F.To.AddDays(n), c.Today.AddDays(-1) }.Max() > c.Today.AddDays(-1) ? c.Today.AddDays(-1) : c.F.To.AddDays(n);
        loadTo = loadTo < c.F.To ? c.F.To : loadTo;
        List<AnaMemberDayDTO> rows = await DaysAsync(c, loadFrom, loadTo, ct).ConfigureAwait(false);
        Dictionary<long, List<DateOnly>> days = rows.Where(r => r.Bills > 0).GroupBy(r => r.MemberId).ToDictionary(g => g.Key, g => g.Select(r => r.Day).Distinct().Order().ToList());
        DateOnly lastComplete = c.Today.AddDays(-1);

        (decimal? Repeat, decimal? Conv, decimal? Gap, decimal? Within) Calc(DateOnly from, DateOnly to)
        {
            Totals t = Totals.Of(rows.Where(r => r.Day >= from && r.Day <= to));
            List<AnaMemberSnapshotDTO> fresh = [.. c.Members.Where(m => InRange(m.RegisteredAt, from, to) && AnaTime.Day(m.RegisteredAt).AddDays(n) <= lastComplete)];
            int converted = fresh.Count(m => m.FirstPurchaseAt is DateTimeOffset fp && AnaTime.Day(fp).DayNumber - AnaTime.Day(m.RegisteredAt).DayNumber <= n);
            var gaps = new List<decimal>();
            int firstBuyers = 0, within = 0;
            foreach ((long _, List<DateOnly> ds) in days)
            {
                for (int i = 1; i < ds.Count; i++)
                {
                    if (ds[i] >= from && ds[i] <= to)
                    {
                        gaps.Add(ds[i].DayNumber - ds[i - 1].DayNumber);
                    }
                }
            }

            foreach (AnaMemberSnapshotDTO m in c.Members.Where(m => m.FirstPurchaseAt is DateTimeOffset fp && InRange(fp, from, to)))
            {
                DateOnly first = AnaTime.Day(m.FirstPurchaseAt!.Value);
                if (first.AddDays(n) > lastComplete)
                {
                    continue;
                }

                firstBuyers++;
                if (days.TryGetValue(m.MemberId, out List<DateOnly>? ds) && ds.Any(d => d > first && d.DayNumber - first.DayNumber <= n))
                {
                    within++;
                }
            }

            return (AnaMath.Pct(t.Repeaters, t.Buyers), AnaMath.Pct(converted, fresh.Count), gaps.Count == 0 ? null : Math.Round(gaps.Average(), 2), AnaMath.Pct(within, firstBuyers));
        }

        var cur = Calc(c.F.From, c.F.To);
        var cmp = a.CompareFrom is DateOnly cf && a.CompareTo is DateOnly cto ? Calc(cf, cto) : default;
        Dictionary<long, int> bills = rows.Where(r => r.Day >= c.F.From && r.Day <= c.F.To).GroupBy(r => r.MemberId).ToDictionary(g => g.Key, g => g.Sum(r => r.Bills));
        var dto = new RepeatPurchaseDTO
        {
            RepeatRate = c.M("member.repeatRate", cur.Repeat, MetricUnit.Percent, MetricKind.Computed, cmp.Repeat, drilldown: "member.repeat"),
            FirstPurchaseConversion = c.M("member.firstPurchaseConversion", cur.Conv, MetricUnit.Percent, MetricKind.Computed, cmp.Conv),
            AvgDaysBetweenPurchases = c.M("member.avgDaysBetweenPurchases", cur.Gap, MetricUnit.Days, MetricKind.Computed, cmp.Gap),
            RepeatWithinNDays = c.M("member.repeatWithinNDays", cur.Within, MetricUnit.Percent, MetricKind.Computed, cmp.Within),
            ByPurchaseCount =
            [
                new KeyValueDTO { Key = "1", Value = bills.Count(b => b.Value == 1) },
                new KeyValueDTO { Key = "2", Value = bills.Count(b => b.Value == 2) },
                new KeyValueDTO { Key = "3+", Value = bills.Count(b => b.Value >= 3) },
            ],
            Trend = Series(c, "member.repeatRate", MetricUnit.Percent, (f, t) => { Totals x = Totals.Of(rows.Where(r => r.Day >= f && r.Day <= t)); return AnaMath.Pct(x.Repeaters, x.Buyers); }),
        };
        return await WrapAsync(c, dto, ct, AnaTables.Sales, AnaTables.Members).ConfigureAwait(false);
    }

    /// <summary>AN-08 มูลค่าการซื้อ</summary>
    public async Task<AnalyticsResultDTO<PurchaseValueDTO>> PurchaseValueAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, ValueBreakdown breakdown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.MemberRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        AppliedFilterDTO a = c.F.Applied;
        List<AnaMemberDayDTO> all = await DaysAsync(c, a.CompareFrom is DateOnly cf0 && cf0 < c.F.From ? cf0 : c.F.From, c.F.To, ct).ConfigureAwait(false);
        List<AnaMemberDayDTO> rows = [.. all.Where(r => r.Day >= c.F.From)];
        Totals t = Totals.Of(rows);
        Totals? ct2 = a.CompareFrom is DateOnly cf && a.CompareTo is DateOnly cto ? Totals.Of(all.Where(r => r.Day >= cf && r.Day <= cto)) : null;
        var dto = new PurchaseValueDTO
        {
            AvgSalesPerMember = c.M("sales.perMember", AnaMath.Div(t.Net, t.Buyers), MetricUnit.Baht, MetricKind.Computed, ct2 is null ? null : AnaMath.Div(ct2.Net, ct2.Buyers), drilldown: "member.buyers"),
            AvgTicket = c.M("sales.avgTicket", AnaMath.Div(t.Net, t.Bills), MetricUnit.Baht, MetricKind.Computed, ct2 is null ? null : AnaMath.Div(ct2.Net, ct2.Bills), drilldown: "sales.net"),
            ItemsPerTicket = c.M("sales.itemsPerTicket", AnaMath.Div(t.Items, t.Bills), MetricUnit.Ratio, MetricKind.Computed, ct2 is null ? null : AnaMath.Div(ct2.Items, ct2.Bills)),
        };

        // การกระจายตามช่วงยอดซื้อต่อคน
        Dictionary<long, decimal> perMember = rows.GroupBy(r => r.MemberId).ToDictionary(g => g.Key, g => g.Sum(r => r.Net));
        List<decimal> bands = [.. c.S.PurchaseValueBands.Order()];
        for (int i = 0; i <= bands.Count; i++)
        {
            decimal lo = i == 0 ? decimal.MinValue : bands[i - 1];
            decimal hi = i == bands.Count ? decimal.MaxValue : bands[i];
            string key = i == 0 ? $"0-{bands.FirstOrDefault()}" : i == bands.Count ? $">{bands[^1]}" : $"{lo}-{hi}";
            List<decimal> v = [.. perMember.Values.Where(x => x > lo && x <= hi || (i == 0 && x <= hi))];
            dto.Distribution.Add(new ValueBandDTO { Key = key, Members = v.Count, Sales = v.Sum() });
        }

        // แยกตามมิติ
        Dictionary<string, string> tierNames = (await _tiers.ListAsync(ct).ConfigureAwait(false)).ToDictionary(x => x.Code, x => x.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> branchNames = await BranchNamesAsync(ct).ConfigureAwait(false);
        IEnumerable<(string Key, long Member, decimal Net, int Bills)> parts = breakdown switch
        {
            ValueBreakdown.Tier => rows.Select(r => (c.All.TryGetValue(r.MemberId, out AnaMemberSnapshotDTO? m) ? m.TierCode ?? "" : "", r.MemberId, r.Net, r.Bills)),
            ValueBreakdown.Branch => rows.Select(r => (r.BranchCode, r.MemberId, r.Net, r.Bills)),
            ValueBreakdown.Channel => rows.Select(r => (r.Channel.ToString(), r.MemberId, r.Net, r.Bills)),
            ValueBreakdown.Category => rows.SelectMany(r => r.Categories.Select(x => (x.Key, r.MemberId, x.Value, 0))),
            _ => rows.SelectMany(r => r.Brands.Select(x => (x.Key, r.MemberId, x.Value, r.BrandBills.GetValueOrDefault(x.Key)))),
        };
        dto.Breakdown = [.. parts.GroupBy(x => x.Key).Select(g =>
        {
            int bills = g.Sum(x => x.Bills);
            string label = breakdown switch
            {
                ValueBreakdown.Tier => tierNames.GetValueOrDefault(g.Key, g.Key),
                ValueBreakdown.Branch => branchNames.GetValueOrDefault(g.Key, g.Key),
                _ => g.Key,
            };
            return new ValueBreakdownRowDTO
            {
                Key = g.Key, Label = label.Length == 0 ? "ไม่ระบุ" : label, Members = g.Select(x => x.Member).Distinct().Count(), Sales = g.Sum(x => x.Net),
                AvgTicket = bills == 0 ? null : AnaMath.Div(g.Sum(x => x.Net), bills) is decimal v ? Math.Round(v, 2) : null,
            };
        }).OrderByDescending(x => x.Sales)];
        if (c.Empty)
        {
            dto.Distribution = [];
            dto.Breakdown = [];
        }

        return await WrapAsync(c, dto, ct, AnaTables.Sales).ConfigureAwait(false);
    }

    /// <summary>AN-15 แหล่งที่มาของสมาชิก</summary>
    public async Task<AnalyticsResultDTO<MemberSourceDTO>> SourcesAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.MemberRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        int n = c.S.FirstPurchaseDays;
        DateOnly lastComplete = c.Today.AddDays(-1);
        List<AnaMemberDayDTO> rows = await DaysAsync(c, c.F.From, c.F.To, ct).ConfigureAwait(false);
        Dictionary<long, int> bills = rows.GroupBy(r => r.MemberId).ToDictionary(g => g.Key, g => g.Sum(r => r.Bills));
        List<AnaMemberSnapshotDTO> fresh = [.. c.Members.Where(m => InRange(m.RegisteredAt, c.F.From, c.F.To))];

        decimal? FirstRate(List<AnaMemberSnapshotDTO> g)
        {
            List<AnaMemberSnapshotDTO> due = [.. g.Where(m => AnaTime.Day(m.RegisteredAt).AddDays(n) <= lastComplete)];
            return AnaMath.Pct(due.Count(m => m.FirstPurchaseAt is DateTimeOffset fp && AnaTime.Day(fp).DayNumber - AnaTime.Day(m.RegisteredAt).DayNumber <= n), due.Count);
        }

        decimal? RepeatRate(List<AnaMemberSnapshotDTO> g)
        {
            List<int> b = [.. g.Select(m => bills.GetValueOrDefault(m.MemberId)).Where(x => x > 0)];
            return AnaMath.Pct(b.Count(x => x >= 2), b.Count);
        }

        List<CampaignCostIngestDTO> costs = await CampaignCostsAsync(ct).ConfigureAwait(false);
        var dto = new MemberSourceDTO
        {
            ByRegisteredChannel = [.. fresh.GroupBy(m => m.RegisteredChannel).Select(g => g.ToList()).Select(g => new RegisteredChannelSourceDTO
            {
                Key = g[0].RegisteredChannel.ToString(), NewMembers = g.Count, VerifiedRate = AnaMath.Pct(g.Count(m => m.Verified), g.Count), FirstPurchaseRate = FirstRate(g),
                RepeatRate = RepeatRate(g),
            }).OrderByDescending(x => x.NewMembers)],
            BySourceCampaign = [.. fresh.Where(m => m.SourceCampaignCode is not null).GroupBy(m => m.SourceCampaignCode!, StringComparer.OrdinalIgnoreCase).Select(g => g.ToList()).Select(g =>
            {
                string code = g[0].SourceCampaignCode!;
                List<CampaignCostIngestDTO> k = [.. costs.Where(x => string.Equals(x.CampaignCode, code, StringComparison.OrdinalIgnoreCase))];
                return new CampaignSourceDTO
                {
                    CampaignCode = code, NewMembers = g.Count, FirstPurchaseRate = FirstRate(g),
                    CostPerNewMember = c.M("source.costPerNewMember", AnaMath.Div(k.Sum(x => x.Amount), g.Count), MetricUnit.Baht, MetricKind.Computed,
                        insufficient: k.Count == 0 ? AnaFormula.MissingSource : null),
                };
            }).OrderByDescending(x => x.NewMembers)],
        };
        return await WrapAsync(c, dto, ct, AnaTables.Members, AnaTables.Sales).ConfigureAwait(false);
    }
}
