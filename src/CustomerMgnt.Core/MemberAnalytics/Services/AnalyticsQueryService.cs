using Common.Core.Exceptions;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

/// <summary>
/// คำนวณตัวเลขของหน้าจอวิเคราะห์จาก read model (Claude.MD §30–§34)
/// ทุก method: ตรวจสิทธิ์ → แก้ตัวกรองตามขอบเขต → อ่าน read model → ห่อด้วย <see cref="AnalyticsResultDTO{T}"/>
/// </summary>
public sealed partial class AnalyticsQueryService
{
    private readonly AnaFilterResolver _filters;
    private readonly IAnaReadModelDAO _model;
    private readonly IAnaDocumentDAO _docs;
    private readonly IAnaSourceDAO _source;
    private readonly IAnaMessageDAO _messages;
    private readonly ICampaignDAO _campaigns;
    private readonly ICouponDAO _coupons;
    private readonly ISegmentDAO _segments;
    private readonly ITierDAO _tiers;
    private readonly IPointDAO _points;
    private readonly IMembershipAuditDAO _audit;
    private readonly AnaSettingsProvider _settings;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public AnalyticsQueryService(AnaFilterResolver filters, IAnaReadModelDAO model, IAnaDocumentDAO docs, IAnaSourceDAO source, IAnaMessageDAO messages,
        ICampaignDAO campaigns, ICouponDAO coupons, ISegmentDAO segments, ITierDAO tiers, IPointDAO points, IMembershipAuditDAO audit,
        AnaSettingsProvider settings, TimeProvider clock)
    {
        _filters = filters;
        _model = model;
        _docs = docs;
        _source = source;
        _messages = messages;
        _campaigns = campaigns;
        _coupons = coupons;
        _segments = segments;
        _tiers = tiers;
        _points = points;
        _audit = audit;
        _settings = settings;
        _clock = clock;
    }

    // ================================================================== ส่วนร่วม

    /// <summary>ข้อมูลที่โหลดครั้งเดียวต่อคำขอ</summary>
    internal sealed class Ctx
    {
        public required ResolvedFilter F { get; init; }
        public required AnalyticsSettingsDTO S { get; init; }
        /// <summary>สมาชิกที่ผ่านตัวกรองระดับสมาชิก (tier / ช่องทางสมัคร / แคมเปญที่พาสมัคร) และขอบเขต</summary>
        public required List<AnaMemberSnapshotDTO> Members { get; init; }
        public required Dictionary<long, AnaMemberSnapshotDTO> All { get; init; }
        /// <summary>id ของสมาชิกที่ผ่านตัวกรองระดับสมาชิก (null = ไม่มีตัวกรองระดับสมาชิก)</summary>
        public HashSet<long>? MemberFilter { get; init; }
        public required DateOnly Today { get; init; }
        public bool Empty => F.Empty;

        /// <summary>ตัวเลข 1 ตัว (ขอบเขตว่าง = null + scope_empty)</summary>
        public MetricValueDTO M(string metric, decimal? value, MetricUnit unit, MetricKind kind = MetricKind.Fact, decimal? compare = null, string? insufficient = null,
            string? drilldown = null) =>
            AnaMath.Metric(metric, value, unit, kind, F.Applied.Compare == CompareMode.None ? null : compare, Empty ? AnaFormula.ScopeEmpty : insufficient, drilldown);
    }

    private async Task<Ctx> ContextAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CancellationToken ct, DataScope? scope = null)
    {
        ResolvedFilter f = await _filters.ResolveAsync(caller, filter, ct, scope).ConfigureAwait(false);
        AnalyticsSettingsDTO s = await _settings.GetAsync(ct).ConfigureAwait(false);
        IReadOnlyList<AnaMemberSnapshotDTO> snaps = f.Empty ? [] : await _model.ListSnapshotsAsync(ct).ConfigureAwait(false);
        AppliedFilterDTO a = f.Applied;
        bool memberLevel = a.Tier.Count > 0 || a.RegisteredChannel.Count > 0 || a.SourceCampaign.Count > 0;
        IReadOnlyCollection<string>? branches = f.Branches;
        IReadOnlyCollection<SalesChannel>? channels = f.Channels;
        var members = snaps.Where(m =>
                (a.Tier.Count == 0 || (m.TierCode is not null && a.Tier.Contains(m.TierCode, StringComparer.OrdinalIgnoreCase)))
                && (a.RegisteredChannel.Count == 0 || a.RegisteredChannel.Contains(m.RegisteredChannel))
                && (a.SourceCampaign.Count == 0 || (m.SourceCampaignCode is not null && a.SourceCampaign.Contains(m.SourceCampaignCode, StringComparer.OrdinalIgnoreCase)))
                // ตัวเลขระดับสมาชิกภายใต้ขอบเขต/ตัวกรองสาขา ใช้สาขาที่ซื้อบ่อยที่สุด (HomeBranch)
                && (branches is null || (m.HomeBranch is not null && branches.Contains(m.HomeBranch, StringComparer.OrdinalIgnoreCase)))
                && (channels is null || (m.HomeChannel is SalesChannel hc && channels.Contains(hc))))
            .ToList();
        return new Ctx
        {
            F = f, S = s, Members = members, All = snaps.ToDictionary(x => x.MemberId),
            MemberFilter = memberLevel ? [.. members.Select(m => m.MemberId)] : null, Today = AnaTime.Today(_clock),
        };
    }

    /// <summary>แถวยอดขายของช่วง ตัดตามตัวกรองทุกมิติ (แบรนด์/หมวดใช้ยอดของแบรนด์/หมวดนั้น)</summary>
    private async Task<List<AnaMemberDayDTO>> DaysAsync(Ctx c, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (c.Empty)
        {
            return [];
        }

        IReadOnlyList<AnaMemberDayDTO> rows = await _model.ListMemberDaysAsync(from, to, c.F.Branches, c.F.Channels, ct).ConfigureAwait(false);
        IReadOnlyCollection<string>? brands = c.F.Brands;
        List<string> categories = c.F.Applied.Category;
        var result = new List<AnaMemberDayDTO>(rows.Count);
        foreach (AnaMemberDayDTO r in rows)
        {
            if (c.MemberFilter is not null && !c.MemberFilter.Contains(r.MemberId))
            {
                continue;
            }

            if (brands is null && categories.Count == 0)
            {
                result.Add(r);
                continue;
            }

            // ยอดเฉพาะแบรนด์/หมวดที่เลือก — สัดส่วนยอดใช้ปรับส่วนลด ต้นทุน และสิทธิ์แคมเปญ
            decimal net = brands is null ? r.Net : r.Brands.Where(b => brands.Contains(b.Key, StringComparer.OrdinalIgnoreCase)).Sum(b => b.Value);
            if (categories.Count > 0)
            {
                decimal catNet = r.Categories.Where(x => categories.Contains(x.Key, StringComparer.OrdinalIgnoreCase)).Sum(x => x.Value);
                net = Math.Min(net, catNet);
            }

            int bills = brands is null ? r.Bills : Math.Min(r.Bills, r.BrandBills.Where(b => brands.Contains(b.Key, StringComparer.OrdinalIgnoreCase)).Sum(b => b.Value));
            if (net == 0 && bills == 0)
            {
                continue;
            }

            decimal share = r.Net != 0 ? Math.Clamp(net / r.Net, 0, 1) : 0;
            result.Add(new AnaMemberDayDTO
            {
                Day = r.Day, MemberId = r.MemberId, BranchCode = r.BranchCode, Channel = r.Channel, Bills = Math.Max(bills, net != 0 ? 1 : 0), Net = net,
                Gross = Math.Round(r.Gross * share, 2), Discount = Math.Round(r.Discount * share, 2), Returns = Math.Round(r.Returns * share, 2),
                Eligible = Math.Round(r.Eligible * share, 2), PointsTender = Math.Round(r.PointsTender * share, 2), PointsEarned = (int)Math.Round(r.PointsEarned * share),
                Items = Math.Round(r.Items * share, 2), Cost = Math.Round(r.Cost * share, 2), CostedNet = Math.Round(r.CostedNet * share, 2),
                Brands = brands is null ? r.Brands : r.Brands.Where(b => brands.Contains(b.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(),
                BrandBills = r.BrandBills,
                Categories = categories.Count == 0 ? r.Categories : r.Categories.Where(x => categories.Contains(x.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(),
                Benefits = r.Benefits.ToDictionary(b => b.Key, b => new AnaBenefitAggDTO
                {
                    Bills = b.Value.Bills, Sales = Math.Round(b.Value.Sales * share, 2), Discount = Math.Round(b.Value.Discount * share, 2), BonusPoints = b.Value.BonusPoints,
                }),
            });
        }

        return result;
    }

    /// <summary>ยอดรวมของชุดแถว</summary>
    internal sealed class Totals
    {
        public decimal Net { get; init; }
        public int Bills { get; init; }
        public int Buyers { get; init; }
        public int Repeaters { get; init; }
        public decimal Items { get; init; }
        public decimal PromoDiscount { get; init; }

        public static Totals Of(IEnumerable<AnaMemberDayDTO> rows)
        {
            var list = rows as IReadOnlyCollection<AnaMemberDayDTO> ?? [.. rows];
            Dictionary<long, int> bills = list.GroupBy(r => r.MemberId).ToDictionary(g => g.Key, g => g.Sum(r => r.Bills));
            return new Totals
            {
                Net = list.Sum(r => r.Net), Bills = list.Sum(r => r.Bills), Buyers = bills.Count(b => b.Value > 0), Repeaters = bills.Count(b => b.Value >= 2),
                Items = list.Sum(r => r.Items), PromoDiscount = list.Sum(r => r.Benefits.Values.Sum(b => b.Discount)),
            };
        }
    }

    /// <summary>ห่อผลด้วยตัวกรองที่ใช้จริงและความสดของข้อมูล</summary>
    private async Task<AnalyticsResultDTO<T>> WrapAsync<T>(Ctx c, T data, CancellationToken ct, params string[] tables)
    {
        DateTimeOffset? asOf = null;
        foreach (string t in tables.Length == 0 ? AnaTables.All : tables)
        {
            TableStatusDTO? st = (await _docs.GetAsync<TableStatusDTO>(AnaCollections.TableStatus, t, ct).ConfigureAwait(false))?.Value;
            DateTimeOffset? w = st?.Watermark;
            asOf = w is null ? asOf ?? null : asOf is null || w < asOf ? w : asOf;
            if (w is null)
            {
                asOf = null;
                break;
            }
        }

        return new AnalyticsResultDTO<T>
        {
            Data = data, AppliedFilter = c.F.Applied, ScopeEmpty = c.Empty, ScopeClamped = c.F.Clamped, DataAsOf = asOf,
            Stale = asOf is null || _clock.GetUtcNow() - asOf.Value > TimeSpan.FromMinutes(c.S.StaleAfterMinutes),
        };
    }

    /// <summary>กราฟแนวเวลา: รวมค่าของแต่ละช่วง (ช่วงเปรียบเทียบจับคู่ตามลำดับช่วง)</summary>
    private static SeriesDTO Series(Ctx c, string metric, MetricUnit unit, Func<DateOnly, DateOnly, decimal?> valueOf, bool withCompare = true)
    {
        Granularity g = c.F.Applied.Granularity;
        List<DateOnly> buckets = [.. AnaTime.Days(c.F.From, c.F.To).Select(d => AnaTime.Bucket(d, g)).Distinct()];
        var s = new SeriesDTO { Metric = metric, Unit = unit };
        int offset = c.F.Applied.CompareFrom is DateOnly cf ? c.F.From.DayNumber - cf.DayNumber : 0;
        foreach (DateOnly b in buckets)
        {
            DateOnly bFrom = b < c.F.From ? c.F.From : b;
            DateOnly bTo = (g switch { Granularity.Week => b.AddDays(6), Granularity.Month => b.AddMonths(1).AddDays(-1), _ => b }) is var e && e > c.F.To ? c.F.To : e;
            decimal? v = c.Empty ? null : valueOf(bFrom, bTo);
            decimal? cv = null;
            if (withCompare && !c.Empty && c.F.Applied.Compare != CompareMode.None)
            {
                (DateOnly f, DateOnly t) = c.F.Applied.Compare == CompareMode.SamePeriodLastYear ? (bFrom.AddYears(-1), bTo.AddYears(-1)) : (bFrom.AddDays(-offset), bTo.AddDays(-offset));
                cv = valueOf(f, t);
            }

            s.Points.Add(new SeriesPointDTO
            {
                Key = b.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Label = AnaTime.Label(b, g), Value = AnaMath.Round(v, unit),
                CompareValue = AnaMath.Round(cv, unit), InsufficientReason = c.Empty ? AnaFormula.ScopeEmpty : null,
            });
        }

        return s;
    }

    private static bool InRange(DateTimeOffset at, DateOnly from, DateOnly to)
    {
        DateOnly d = AnaTime.Day(at);
        return d >= from && d <= to;
    }

    /// <summary>ชื่อสาขาจาก master</summary>
    private async Task<Dictionary<string, string>> BranchNamesAsync(CancellationToken ct) =>
        (await _docs.ListAsync<BranchIngestDTO>(AnaCollections.Branch, null, ct).ConfigureAwait(false))
            .ToDictionary(d => d.Id, d => d.Value.Name ?? d.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>ยอดขายทั้งหมด (รวมคนที่ไม่ใช่สมาชิก) จาก sales-summary — null = ไม่มีข้อมูล</summary>
    private async Task<Func<DateOnly, DateOnly, decimal?>> TotalSalesAsync(Ctx c, CancellationToken ct)
    {
        List<SalesSummaryIngestDTO> all = [.. (await _docs.ListAsync<SalesSummaryIngestDTO>(AnaCollections.SalesSummary, null, ct).ConfigureAwait(false))
            .Select(d => d.Value)
            .Where(s => (c.F.Branches is null || c.F.Branches.Contains(s.BranchCode, StringComparer.OrdinalIgnoreCase)) && (c.F.Channels is null || c.F.Channels.Contains(s.Channel)))];
        return (from, to) =>
        {
            List<SalesSummaryIngestDTO> rows = [.. all.Where(s => s.BusinessDate >= from && s.BusinessDate <= to)];
            return rows.Count == 0 ? null : rows.Sum(s => s.NetSales);
        };
    }

    /// <summary>แคมเปญที่ผู้เรียกเห็นได้ (ana.promotion.all = ทุกแคมเปญ, ana.promotion.read = ที่ตนสร้าง)</summary>
    private async Task<List<CampaignDTO>> VisibleCampaignsAsync(MembershipCaller caller, CancellationToken ct)
    {
        IReadOnlyList<CampaignDTO> all = await _campaigns.ListByStatusAsync(Enum.GetValues<CampaignStatus>(), ct).ConfigureAwait(false);
        return caller.Has(AnaPermissions.PromotionAll) || caller.User == Common.Core.Domain.UserContextDTO.System
            ? [.. all]
            : [.. all.Where(c => string.Equals(c.CreatedBy, caller.User.Subject, StringComparison.Ordinal))];
    }

    private async Task<CampaignDTO> VisibleCampaignAsync(MembershipCaller caller, string code, CancellationToken ct)
    {
        caller.Require(AnaPermissions.PromotionRead);
        CampaignDTO c = await _campaigns.GetByCodeAsync(code.Trim().ToUpperInvariant(), ct).ConfigureAwait(false) ?? throw new NotFoundException("Campaign not found.");
        if (!caller.Has(AnaPermissions.PromotionAll) && !string.Equals(c.CreatedBy, caller.User.Subject, StringComparison.Ordinal))
        {
            throw new NotFoundException("Campaign not found.");
        }

        return c;
    }

    /// <summary>ต้นทุนแคมเปญ (revision ล่าสุดของแต่ละรายการ)</summary>
    private async Task<List<CampaignCostIngestDTO>> CampaignCostsAsync(CancellationToken ct) =>
        [.. (await _docs.ListAsync<CampaignCostIngestDTO>(AnaCollections.CampaignCost, null, ct).ConfigureAwait(false)).Select(d => d.Value)];

    // ================================================================== Dashboard (§30)

    /// <summary>AN-01 ภาพรวมผู้บริหาร</summary>
    public async Task<AnalyticsResultDTO<ExecutiveDashboardDTO>> ExecutiveAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.DashboardRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        AppliedFilterDTO a = c.F.Applied;
        DateOnly loadFrom = new[] { c.F.From, a.CompareFrom ?? c.F.From, c.F.To.AddDays(-c.S.ActiveDays + 1), (a.CompareTo ?? c.F.To).AddDays(-c.S.ActiveDays + 1) }.Min();
        List<AnaMemberDayDTO> rows = await DaysAsync(c, loadFrom, c.F.To, ct).ConfigureAwait(false);
        IReadOnlyList<AnaPointsDayDTO> points = c.Empty ? [] : await _model.ListPointsDaysAsync(loadFrom, c.F.To, ct).ConfigureAwait(false);
        IReadOnlyList<AnaCouponDayDTO> coupons = c.Empty ? [] : await _model.ListCouponDaysAsync(loadFrom, c.F.To, ct).ConfigureAwait(false);
        Func<DateOnly, DateOnly, decimal?> total = await TotalSalesAsync(c, ct).ConfigureAwait(false);
        List<CampaignDTO> campaigns = await VisibleCampaignsAsync(caller, ct).ConfigureAwait(false);

        Dictionary<string, decimal?> Kpi(DateOnly from, DateOnly to)
        {
            Totals t = Totals.Of(rows.Where(r => r.Day >= from && r.Day <= to));
            decimal? all = total(from, to);
            var p = points.Where(x => x.Day >= from && x.Day <= to && InBranch(c, x.BranchCode)).ToList();
            var cp = coupons.Where(x => x.Day >= from && x.Day <= to && InBranch(c, x.BranchCode)).ToList();
            DateTimeOffset end = AnaTime.Start(to.AddDays(1));
            return new()
            {
                ["membersTotal"] = c.Members.Count(m => m.RegisteredAt < end && m.Status != MemberStatus.Closed),
                ["membersNew"] = c.Members.Count(m => InRange(m.RegisteredAt, from, to)),
                ["membersActive"] = rows.Where(r => r.Day > to.AddDays(-c.S.ActiveDays) && r.Day <= to && r.Bills > 0).Select(r => r.MemberId).Distinct().Count(),
                ["memberSales"] = t.Net,
                ["memberSalesShare"] = all is decimal s ? AnaMath.Pct(t.Net, s) : null,
                ["avgTicket"] = AnaMath.Div(t.Net, t.Bills),
                ["purchaseFrequency"] = AnaMath.Div(t.Bills, t.Buyers),
                ["repeatRate"] = AnaMath.Pct(t.Repeaters, t.Buyers),
                ["pointsEarned"] = p.Where(x => x.Type == LedgerType.Earn).Sum(x => x.Points),
                ["pointsRedeemed"] = -p.Where(x => x.Type == LedgerType.Redeem).Sum(x => x.Points),
                ["couponsRedeemed"] = cp.Where(x => x.Event == CouponMetricEvent.Redeemed).Sum(x => x.Count) - cp.Where(x => x.Event == CouponMetricEvent.Reversed).Sum(x => x.Count),
                ["activeCampaigns"] = campaigns.Count(x => x.StartAt < end && x.EndAt >= AnaTime.Start(from) && x.Status is CampaignStatus.Active or CampaignStatus.Paused or CampaignStatus.Ended),
                ["promotionDiscount"] = t.PromoDiscount,
                ["hasTotal"] = all is null ? 0 : 1,
            };
        }

        Dictionary<string, decimal?> cur = Kpi(c.F.From, c.F.To);
        Dictionary<string, decimal?>? cmp = a.CompareFrom is DateOnly cf && a.CompareTo is DateOnly ctTo ? Kpi(cf, ctTo) : null;
        var units = new (string Key, MetricUnit Unit, MetricKind Kind, string? Drill)[]
        {
            ("membersTotal", MetricUnit.Count, MetricKind.Fact, "member.total"), ("membersNew", MetricUnit.Count, MetricKind.Fact, "member.new"),
            ("membersActive", MetricUnit.Count, MetricKind.Computed, "member.active"), ("memberSales", MetricUnit.Baht, MetricKind.Fact, "sales.net"),
            ("memberSalesShare", MetricUnit.Percent, MetricKind.Computed, null), ("avgTicket", MetricUnit.Baht, MetricKind.Computed, "sales.net"),
            ("purchaseFrequency", MetricUnit.Ratio, MetricKind.Computed, "member.buyers"), ("repeatRate", MetricUnit.Percent, MetricKind.Computed, "member.repeat"),
            ("pointsEarned", MetricUnit.Points, MetricKind.Fact, null), ("pointsRedeemed", MetricUnit.Points, MetricKind.Fact, null),
            ("couponsRedeemed", MetricUnit.Count, MetricKind.Fact, null), ("activeCampaigns", MetricUnit.Count, MetricKind.Fact, null),
            ("promotionDiscount", MetricUnit.Baht, MetricKind.Fact, null),
        };
        var dto = new ExecutiveDashboardDTO();
        foreach ((string key, MetricUnit unit, MetricKind kind, string? drill) in units)
        {
            string? reason = key == "memberSalesShare" && cur["hasTotal"] == 0 ? AnaFormula.MissingSource : null;
            dto.Kpis[key] = c.M(key, cur[key], unit, kind, cmp?[key], reason, drill);
        }

        dto.SalesTrend = Series(c, "memberSales", MetricUnit.Baht, (f, t) => rows.Where(r => r.Day >= f && r.Day <= t).Sum(r => r.Net));
        dto.MembersTrend = Series(c, "membersNew", MetricUnit.Count, (f, t) => c.Members.Count(m => InRange(m.RegisteredAt, f, t)));
        Dictionary<string, string> names = await BranchNamesAsync(ct).ConfigureAwait(false);
        dto.TopBranches = [.. rows.Where(r => r.Day >= c.F.From && r.BranchCode.Length > 0).GroupBy(r => r.BranchCode)
            .Select(g => new KeyValueDTO { Key = g.Key, Label = names.GetValueOrDefault(g.Key, g.Key), Value = g.Sum(r => r.Net) })
            .OrderByDescending(x => x.Value).Take(10)];
        dto.Alerts = c.Empty ? [] : [.. (await _docs.ListAsync<AlertEventDTO>(AnaCollections.AlertEvent, null, ct).ConfigureAwait(false))
            .Select(d => d.Value).Where(e => e.State == AlertState.Fired && (e.BranchCode is null || InBranch(c, e.BranchCode)))
            .OrderByDescending(e => e.OccurredAt).Take(5)];
        return await WrapAsync(c, dto, ct).ConfigureAwait(false);
    }

    private static bool InBranch(Ctx c, string branch) => c.F.Branches is null || c.F.Branches.Contains(branch, StringComparer.OrdinalIgnoreCase);

    /// <summary>AN-02 ภาพรวมสมาชิก</summary>
    public async Task<AnalyticsResultDTO<MemberDashboardDTO>> MemberDashboardAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.MemberRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        Dictionary<string, string> tierNames = (await _tiers.ListAsync(ct).ConfigureAwait(false)).ToDictionary(t => t.Code, t => t.Name, StringComparer.OrdinalIgnoreCase);
        var dto = new MemberDashboardDTO();
        foreach (RegisteredChannel ch in Enum.GetValues<RegisteredChannel>())
        {
            SeriesDTO s = Series(c, "membersNew", MetricUnit.Count, (f, t) => c.Members.Count(m => m.RegisteredChannel == ch && InRange(m.RegisteredAt, f, t)), withCompare: false);
            s.Label = ch.ToString();
            dto.NewByRegisteredChannel.Add(s);
        }

        dto.ByStatus = [.. Enum.GetValues<MemberStatus>().Select(st => new KeyValueDTO { Key = st.ToString(), Value = c.Members.Count(m => m.Status == st) })];
        dto.ByTier = [.. c.Members.Where(m => m.TierCode is not null).GroupBy(m => m.TierCode!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyValueDTO { Key = g.Key, Label = tierNames.GetValueOrDefault(g.Key, g.Key), Value = g.Count() }).OrderByDescending(x => x.Value)];
        ActivityGroupsDTO groups = await GroupsAsync(c, ct).ConfigureAwait(false);
        dto.ActivityGroups = [.. groups.Groups.Select(g => new ActivityGroupCountDTO { Key = g.Code, Value = g.Count })];
        dto.NearTierUpgrade = groups.Groups.First(g => g.Code == "NearTierUpgrade").Count;
        dto.TierAtRisk = groups.Groups.First(g => g.Code == "TierAtRisk").Count;
        decimal? Rate(DateOnly f, DateOnly t)
        {
            List<AnaMemberSnapshotDTO> n = [.. c.Members.Where(m => InRange(m.RegisteredAt, f, t))];
            return AnaMath.Pct(n.Count(m => m.Verified), n.Count);
        }

        dto.VerificationRate = c.M("member.verificationRate", Rate(c.F.From, c.F.To), MetricUnit.Percent, MetricKind.Computed,
            c.F.Applied.CompareFrom is DateOnly cf && c.F.Applied.CompareTo is DateOnly cto ? Rate(cf, cto) : null, drilldown: "member.new");
        return await WrapAsync(c, dto, ct, AnaTables.Members, AnaTables.Sales).ConfigureAwait(false);
    }

    /// <summary>AN-03 ภาพรวมโปรโมชัน</summary>
    public async Task<AnalyticsResultDTO<PromotionDashboardDTO>> PromotionDashboardAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.PromotionRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        List<CampaignDTO> visible = await VisibleCampaignsAsync(caller, ct).ConfigureAwait(false);
        if (c.F.Applied.Campaign.Count > 0)
        {
            visible = [.. visible.Where(x => c.F.Applied.Campaign.Contains(x.Code, StringComparer.OrdinalIgnoreCase))];
        }

        var codes = new HashSet<string>(visible.Select(x => x.Code), StringComparer.OrdinalIgnoreCase);
        DateTimeOffset start = AnaTime.Start(c.F.From), end = AnaTime.Start(c.F.To.AddDays(1));
        List<CampaignDTO> inPeriod = [.. visible.Where(x => x.StartAt < end && x.EndAt >= start)];
        List<AnaMemberDayDTO> rows = await DaysAsync(c, c.F.Applied.CompareFrom is DateOnly cf0 && cf0 < c.F.From ? cf0 : c.F.From, c.F.To, ct).ConfigureAwait(false);
        IReadOnlyList<AnaCouponDayDTO> coupons = c.Empty ? [] : await _model.ListCouponDaysAsync(c.F.Applied.CompareFrom is DateOnly cf1 && cf1 < c.F.From ? cf1 : c.F.From, c.F.To, ct).ConfigureAwait(false);

        Dictionary<string, decimal?> Kpi(DateOnly from, DateOnly to)
        {
            var part = rows.Where(r => r.Day >= from && r.Day <= to).SelectMany(r => r.Benefits.Where(b => codes.Contains(b.Key)).Select(b => (r.MemberId, b.Value))).ToList();
            int participants = part.Select(x => x.MemberId).Distinct().Count();
            int eligible = c.Members.Count(m => m.Status == MemberStatus.Active);
            var cp = coupons.Where(x => x.Day >= from && x.Day <= to && codes.Contains(x.CampaignCode) && InBranch(c, x.BranchCode)).ToList();
            return new()
            {
                ["eligibleMembers"] = eligible, ["participants"] = participants, ["participationRate"] = AnaMath.Pct(participants, eligible),
                ["campaignSales"] = part.Sum(x => x.Value.Sales), ["promotionDiscount"] = part.Sum(x => x.Value.Discount),
                ["couponsIssued"] = cp.Where(x => x.Event == CouponMetricEvent.Issued).Sum(x => x.Count),
                ["couponsRedeemed"] = cp.Where(x => x.Event == CouponMetricEvent.Redeemed).Sum(x => x.Count) - cp.Where(x => x.Event == CouponMetricEvent.Reversed).Sum(x => x.Count),
            };
        }

        Dictionary<string, decimal?> cur = Kpi(c.F.From, c.F.To);
        Dictionary<string, decimal?>? cmp = c.F.Applied.CompareFrom is DateOnly cf && c.F.Applied.CompareTo is DateOnly cto ? Kpi(cf, cto) : null;
        var dto = new PromotionDashboardDTO
        {
            CampaignsByStatus = [.. Enum.GetValues<CampaignStatus>().Select(s => new KeyValueDTO { Key = s.ToString(), Value = visible.Count(x => x.Status == s) })],
        };
        foreach ((string k, MetricUnit u) in new[] { ("eligibleMembers", MetricUnit.Count), ("participants", MetricUnit.Count), ("participationRate", MetricUnit.Percent),
            ("campaignSales", MetricUnit.Baht), ("promotionDiscount", MetricUnit.Baht), ("couponsIssued", MetricUnit.Count), ("couponsRedeemed", MetricUnit.Count) })
        {
            dto.Kpis[k] = c.M(k, cur[k], u, u == MetricUnit.Percent ? MetricKind.Computed : MetricKind.Fact, cmp?[k]);
        }

        List<AnaMemberDayDTO> inP = [.. rows.Where(r => r.Day >= c.F.From)];
        dto.TopCampaigns = [.. inPeriod.Select(x => new TopCampaignDTO
            {
                CampaignCode = x.Code, Name = x.Name, Status = x.Status,
                Participants = inP.Where(r => r.Benefits.ContainsKey(x.Code)).Select(r => r.MemberId).Distinct().Count(),
                CampaignSales = inP.Sum(r => r.Benefits.TryGetValue(x.Code, out AnaBenefitAggDTO? b) ? b.Sales : 0),
            })
            .OrderByDescending(x => x.CampaignSales).Take(10)];
        dto.BudgetUsage = [.. inPeriod.Where(x => x.Limits.Budget is > 0).Select(x => new BudgetUsageDTO
        {
            CampaignCode = x.Code, Budget = x.Limits.Budget!.Value, Used = x.Usage.BudgetUsed, UsedPct = AnaMath.Pct(x.Usage.BudgetUsed, x.Limits.Budget!.Value) ?? 0,
        }).OrderByDescending(x => x.UsedPct)];
        if (c.Empty)
        {
            dto.TopCampaigns = [];
            dto.BudgetUsage = [];
        }

        return await WrapAsync(c, dto, ct, AnaTables.Sales, AnaTables.Coupons).ConfigureAwait(false);
    }
}
