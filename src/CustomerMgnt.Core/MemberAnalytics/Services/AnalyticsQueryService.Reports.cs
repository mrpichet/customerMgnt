using System.Globalization;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

// รายงานสร้างเอง: จัดกลุ่มตาม dimension (≤ 3) แล้วคำนวณ metric (≤ 10) จาก ana_member_daily
public sealed partial class AnalyticsQueryService
{
    private sealed record Fact(long MemberId, decimal Net, decimal Gross, decimal Discount, decimal Returns, int Bills, decimal Items, decimal Points, decimal PromoSales,
        decimal PromoDiscount, bool PromoBill, int NewMembers);

    /// <summary>รันนิยามรายงาน</summary>
    internal async Task<AnalyticsResultDTO<ReportResultDTO>> RunDefinitionAsync(MembershipCaller caller, ReportDefinitionDTO def, AnalyticsFilterDTO filter, int page, int pageSize,
        CancellationToken ct)
    {
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        List<string> metrics = [.. def.Metrics.Where(m => AnaReportService.Metrics.FirstOrDefault(x => x.Code == m) is var x && x.Code is not null && caller.Has(x.Requires))];
        List<AnaMemberDayDTO> rows = await DaysAsync(c, c.F.From, c.F.To, ct).ConfigureAwait(false);
        Dictionary<string, string> branchNames = await BranchNamesAsync(ct).ConfigureAwait(false);
        List<string> dims = def.Dimensions;
        bool memberDimsOnly = dims.All(d => d is "day" or "week" or "month" or "tier" or "registeredChannel");

        string Time(DateOnly d, string dim) => dim switch
        {
            "week" => AnaTime.Bucket(d, Granularity.Week).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "month" => AnaTime.MonthKey(d),
            _ => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        // Step 1: แตกแถวเป็น fact พร้อม key ของ dimension
        var facts = new List<(string[] Key, Fact F)>();
        foreach (AnaMemberDayDTO r in rows)
        {
            AnaMemberSnapshotDTO? m = c.All.GetValueOrDefault(r.MemberId);
            IEnumerable<(string[] Key, decimal Share, int Bills, AnaBenefitAggDTO? Benefit)> parts = [([], 1m, r.Bills, null)];
            foreach (string dim in dims)
            {
                parts = parts.SelectMany(p => dim switch
                {
                    "brand" => r.Brands.Select(b => ((string[])[.. p.Key, b.Key], r.Net != 0 ? p.Share * b.Value / r.Net : 0, Math.Min(p.Bills, r.BrandBills.GetValueOrDefault(b.Key)), p.Benefit)),
                    "category" => r.Categories.Select(x => ((string[])[.. p.Key, x.Key], r.Net != 0 ? p.Share * x.Value / r.Net : 0, p.Bills, p.Benefit)),
                    "campaign" => r.Benefits.Select(b => ((string[])[.. p.Key, b.Key], p.Share, b.Value.Bills, (AnaBenefitAggDTO?)b.Value)),
                    _ => [((string[])[.. p.Key, dim switch
                    {
                        "day" or "week" or "month" => Time(r.Day, dim),
                        "branch" => r.BranchCode,
                        "channel" => r.Channel.ToString(),
                        "tier" => m?.TierCode ?? string.Empty,
                        _ => m?.RegisteredChannel.ToString() ?? string.Empty,
                    }], p.Share, p.Bills, p.Benefit)],
                });
            }

            foreach ((string[] key, decimal share, int bills, AnaBenefitAggDTO? b) in parts)
            {
                decimal promoSales = b?.Sales ?? r.Benefits.Values.Sum(x => x.Sales) * share;
                decimal promoDiscount = b?.Discount ?? r.Benefits.Values.Sum(x => x.Discount) * share;
                decimal net = b is not null ? b.Sales : r.Net * share;
                facts.Add((key, new Fact(r.MemberId, net, r.Gross * share, r.Discount * share, r.Returns * share, bills, r.Items * share, r.PointsEarned * share, promoSales, promoDiscount,
                    r.Benefits.Count > 0, 0)));
            }
        }

        // สมาชิกใหม่ (เฉพาะ dimension เวลา / tier / ช่องทางสมัคร)
        if (metrics.Contains("member.new") && memberDimsOnly)
        {
            foreach (AnaMemberSnapshotDTO m in c.Members.Where(m => InRange(m.RegisteredAt, c.F.From, c.F.To)))
            {
                DateOnly d = AnaTime.Day(m.RegisteredAt);
                string[] key = [.. dims.Select(dim => dim switch { "tier" => m.TierCode ?? string.Empty, "registeredChannel" => m.RegisteredChannel.ToString(), _ => Time(d, dim) })];
                facts.Add((key, new Fact(m.MemberId, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 1)));
            }
        }

        decimal? Value(string metric, IReadOnlyCollection<Fact> fs)
        {
            int bills = fs.Sum(f => f.Bills);
            Dictionary<long, int> perMember = fs.Where(f => f.NewMembers == 0).GroupBy(f => f.MemberId).ToDictionary(g => g.Key, g => g.Sum(f => f.Bills));
            int buyers = perMember.Count(x => x.Value > 0);
            decimal net = fs.Sum(f => f.Net);
            return metric switch
            {
                "sales.net" => net,
                "sales.gross" => fs.Sum(f => f.Gross),
                "sales.discount" => fs.Sum(f => f.Discount),
                "sales.returns" => fs.Sum(f => f.Returns),
                "sales.bills" => bills,
                "sales.avgTicket" => AnaMath.Div(net, bills),
                "sales.items" => fs.Sum(f => f.Items),
                "sales.itemsPerTicket" => AnaMath.Div(fs.Sum(f => f.Items), bills),
                "member.buyers" => buyers,
                "member.repeatRate" => AnaMath.Pct(perMember.Count(x => x.Value >= 2), buyers),
                "member.salesPerMember" => AnaMath.Div(net, buyers),
                "member.new" => memberDimsOnly ? fs.Sum(f => f.NewMembers) : null,
                "points.earned" => fs.Sum(f => f.Points),
                "promo.sales" => fs.Sum(f => f.PromoSales),
                "promo.discount" => fs.Sum(f => f.PromoDiscount),
                _ => null,
            };
        }

        // Step 2: จัดกลุ่ม
        var result = new ReportResultDTO
        {
            Columns =
            [
                .. dims.Select(d => new ColumnDTO { Key = d, Label = AnaReportService.Dimensions.First(x => x.Code == d).Label }),
                .. metrics.Select(m => AnaReportService.Metrics.First(x => x.Code == m)).Select(m => new ColumnDTO
                {
                    Key = m.Code, Label = m.Label, Type = m.Unit switch { MetricUnit.Baht => "Baht", MetricUnit.Percent => "Percent", MetricUnit.Points => "Points", _ => "Count" },
                }),
            ],
        };
        List<Dictionary<string, object?>> all = c.Empty ? [] : [.. facts.GroupBy(f => string.Join('\u001f', f.Key)).Select(g =>
        {
            string[] key = g.First().Key;
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < dims.Count; i++)
            {
                row[dims[i]] = key[i].Length == 0 ? "ไม่ระบุ" : dims[i] == "branch" ? branchNames.GetValueOrDefault(key[i], key[i]) : key[i];
            }

            List<Fact> fs = [.. g.Select(x => x.F)];
            foreach (string m in metrics)
            {
                MetricUnit unit = AnaReportService.Metrics.First(x => x.Code == m).Unit;
                row[m] = AnaMath.Round(Value(m, fs), unit);
            }

            return row;
        })];
        IEnumerable<Dictionary<string, object?>> sorted = all;
        foreach (ReportSortDTO s in Enumerable.Reverse(def.Sort))
        {
            sorted = Sort(sorted, (s.Desc ? "-" : string.Empty) + s.Key, null);
        }

        all = [.. sorted];
        List<Fact> every = [.. facts.Select(f => f.F)];
        foreach (string m in metrics)
        {
            MetricUnit unit = AnaReportService.Metrics.First(x => x.Code == m).Unit;
            result.Totals[m] = c.Empty ? null : AnaMath.Round(Value(m, every), unit);
            if (m == "member.new" && !memberDimsOnly)
            {
                result.Insufficient[m] = AnaFormula.MissingSource;
            }
        }

        foreach (string m in def.Metrics.Except(metrics))
        {
            result.Insufficient[m] = "permission_required";
        }

        result.Total = all.Count;
        result.Page = page;
        result.PageSize = pageSize;
        result.Rows = [.. all.Skip((page - 1) * pageSize).Take(pageSize)];
        return await WrapAsync(c, result, ct, AnaTables.Sales).ConfigureAwait(false);
    }

    /// <summary>ตาราง KPI ของ dashboard (ใช้ส่งออก): metric, value, compare, change%</summary>
    internal async Task<ReportDTO> DashboardTableAsync(MembershipCaller caller, string page, AnalyticsFilterDTO filter, CancellationToken ct)
    {
        Dictionary<string, MetricValueDTO> kpis = page.ToUpperInvariant() switch
        {
            "AN-01" => (await ExecutiveAsync(caller, filter, ct).ConfigureAwait(false)).Data.Kpis,
            "AN-02" => (await MemberDashboardAsync(caller, filter, ct).ConfigureAwait(false)).Data is var d
                ? new Dictionary<string, MetricValueDTO> { ["nearTierUpgrade"] = d.NearTierUpgrade, ["tierAtRisk"] = d.TierAtRisk, ["verificationRate"] = d.VerificationRate }
                    .Concat(d.ActivityGroups.Select(g => KeyValuePair.Create("group." + g.Key, g.Value))).ToDictionary()
                : [],
            "AN-03" => (await PromotionDashboardAsync(caller, filter, ct).ConfigureAwait(false)).Data.Kpis,
            _ => throw AnaErrors.Bad("unknown_page", "page must be AN-01, AN-02 or AN-03."),
        };
        static string N(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return new ReportDTO
        {
            Title = page,
            Columns = ["metric", "value", "unit", "compareValue", "changePct", "insufficientReason"],
            Rows = [.. kpis.Select(k => new List<string> { k.Key, N(k.Value.Value), k.Value.Unit.ToString(), N(k.Value.CompareValue), N(k.Value.ChangePct), k.Value.InsufficientReason ?? string.Empty })],
        };
    }
}
