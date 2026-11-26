using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

// คะแนน คูปอง แคมเปญ และ funnel (§32, §33, §37 — สูตรข้อ 3–5)
public sealed partial class AnalyticsQueryService
{
    // ================================================================== คะแนน / คูปอง

    /// <summary>AN-10 คะแนน</summary>
    public async Task<AnalyticsResultDTO<PointsAnalyticsDTO>> PointsAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.DashboardRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        AppliedFilterDTO a = c.F.Applied;
        DateOnly from = a.CompareFrom is DateOnly cf0 && cf0 < c.F.From ? cf0 : c.F.From;
        List<AnaPointsDayDTO> rows = c.Empty ? [] : [.. (await _model.ListPointsDaysAsync(from, c.F.To, ct).ConfigureAwait(false)).Where(x => InBranch(c, x.BranchCode))];

        decimal Sum(DateOnly f, DateOnly t, LedgerType type, int sign) => sign * rows.Where(x => x.Day >= f && x.Day <= t && x.Type == type).Sum(x => (decimal)x.Points);
        Dictionary<string, decimal?> Kpi(DateOnly f, DateOnly t)
        {
            decimal earned = Sum(f, t, LedgerType.Earn, 1), redeemed = Sum(f, t, LedgerType.Redeem, -1);
            return new()
            {
                ["earned"] = earned, ["redeemed"] = redeemed, ["expired"] = Sum(f, t, LedgerType.Expire, -1), ["adjusted"] = Sum(f, t, LedgerType.Adjustment, 1),
                ["redemptionRate"] = AnaMath.Pct(redeemed, earned),
            };
        }

        Dictionary<string, decimal?> cur = Kpi(c.F.From, c.F.To);
        Dictionary<string, decimal?>? cmp = a.CompareFrom is DateOnly cf && a.CompareTo is DateOnly cto ? Kpi(cf, cto) : null;
        decimal outstanding = c.Members.Sum(m => (decimal)Math.Max(m.PointsAvailable, 0));
        var dto = new PointsAnalyticsDTO();
        foreach (string k in new[] { "earned", "redeemed", "expired", "adjusted" })
        {
            dto.Kpis[k] = c.M("points." + k, cur[k], MetricUnit.Points, MetricKind.Fact, cmp?[k]);
        }

        dto.Kpis["outstanding"] = c.M("points.outstanding", outstanding, MetricUnit.Points);
        dto.Kpis["liabilityBaht"] = c.M("points.liabilityBaht", outstanding * c.S.PointCostBaht, MetricUnit.Baht, MetricKind.Estimate);
        dto.Kpis["redemptionRate"] = c.M("points.redemptionRate", cur["redemptionRate"], MetricUnit.Percent, MetricKind.Computed, cmp?["redemptionRate"]);
        foreach ((string k, LedgerType type, int sign) in new[] { ("earned", LedgerType.Earn, 1), ("redeemed", LedgerType.Redeem, -1), ("expired", LedgerType.Expire, -1), ("adjusted", LedgerType.Adjustment, 1) })
        {
            dto.FlowTrend.Add(Series(c, "points." + k, MetricUnit.Points, (f, t) => Sum(f, t, type, sign), withCompare: false));
        }

        if (!c.Empty)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            DateOnly today = AnaTime.Day(now);
            DateOnly m0 = new(today.Year, today.Month, 1);
            var expiring = await _points.ListExpiringForNoticeAsync(now, AnaTime.Start(m0.AddMonths(3)), ct).ConfigureAwait(false);
            HashSet<long> ids = [.. c.Members.Select(m => m.MemberId)];
            dto.ExpiringNext = [.. Enumerable.Range(0, 3).Select(i => m0.AddMonths(i)).Select(m => new KeyValueDTO
            {
                Key = AnaTime.MonthKey(m), Label = AnaTime.Label(m, Granularity.Month),
                Value = expiring.Where(x => ids.Contains(x.MemberId) && AnaTime.MonthKey(AnaTime.Day(x.ExpiresAt)) == AnaTime.MonthKey(m)).Sum(x => (decimal)x.Points),
            })];
            (DateTimeOffset rf, DateTimeOffset rt) = AnaTime.Range(c.F.From, c.F.To);
            long after = 0;
            while (true)
            {
                IReadOnlyList<PointLedgerDTO> page = await _source.ListLedgerAsync(rf, rt, after, 1000, ct).ConfigureAwait(false);
                foreach (PointLedgerDTO l in page.Where(l => l.Type == LedgerType.Adjustment && Math.Abs(l.Points) >= c.S.LargeAdjustmentPoints && ids.Contains(l.MemberId)))
                {
                    dto.LargeAdjustments.Add(new LargeAdjustmentDTO
                    {
                        LedgerId = l.Id, MemberNoMasked = MaskNo(c.All.GetValueOrDefault(l.MemberId)?.MemberNo), Points = l.Points, At = l.CreatedAt, By = l.Actor,
                    });
                }

                if (page.Count < 1000 || dto.LargeAdjustments.Count >= 100)
                {
                    break;
                }

                after = page[^1].Id;
            }
        }

        return await WrapAsync(c, dto, ct, AnaTables.Points, AnaTables.Members).ConfigureAwait(false);
    }

    private static string MaskNo(string? no) => no is null ? "-" : no.Length <= 4 ? new string('x', no.Length) : no[..3] + new string('x', no.Length - 5) + no[^2..];

    /// <summary>AN-11 คูปอง</summary>
    public async Task<AnalyticsResultDTO<CouponAnalyticsDTO>> CouponsAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, string? campaign, long? batchId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.DashboardRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        AppliedFilterDTO a = c.F.Applied;
        DateOnly from = a.CompareFrom is DateOnly cf0 && cf0 < c.F.From ? cf0 : c.F.From;
        string? code = string.IsNullOrWhiteSpace(campaign) ? null : campaign.Trim().ToUpperInvariant();
        List<AnaCouponDayDTO> rows = c.Empty ? [] : [.. (await _model.ListCouponDaysAsync(from, c.F.To, ct).ConfigureAwait(false))
            .Where(x => InBranch(c, x.BranchCode) || x.BranchCode.Length == 0)
            .Where(x => (code is null || string.Equals(x.CampaignCode, code, StringComparison.OrdinalIgnoreCase)) && (batchId is null || x.BatchId == batchId))];
        Dictionary<long, string> codes = (await _campaigns.ListByStatusAsync(Enum.GetValues<CampaignStatus>(), ct).ConfigureAwait(false)).ToDictionary(x => x.Id, x => x.Code);

        async Task<int> UsableAsync(DateOnly f, DateOnly t)
        {
            (DateTimeOffset rf, DateTimeOffset rt) = AnaTime.Range(f, t);
            IReadOnlyDictionary<long, int> u = await _source.CountUsableCouponsAsync(rf, rt, ct).ConfigureAwait(false);
            return u.Where(x => code is null || string.Equals(codes.GetValueOrDefault(x.Key), code, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Value);
        }

        int Count(DateOnly f, DateOnly t, CouponMetricEvent e) => rows.Where(x => x.Day >= f && x.Day <= t && x.Event == e).Sum(x => x.Count);
        async Task<Dictionary<string, decimal?>> KpiAsync(DateOnly f, DateOnly t)
        {
            int redeemed = Count(f, t, CouponMetricEvent.Redeemed) - Count(f, t, CouponMetricEvent.Reversed);
            int usable = batchId is null ? await UsableAsync(f, t).ConfigureAwait(false) : 0;
            return new()
            {
                ["issued"] = Count(f, t, CouponMetricEvent.Issued), ["distributed"] = Count(f, t, CouponMetricEvent.Distributed), ["redeemed"] = redeemed,
                ["expired"] = Count(f, t, CouponMetricEvent.Expired), ["cancelled"] = Count(f, t, CouponMetricEvent.Cancelled),
                ["usageRate"] = batchId is null ? AnaMath.Pct(redeemed, usable) : null,
            };
        }

        Dictionary<string, decimal?> cur = await KpiAsync(c.F.From, c.F.To).ConfigureAwait(false);
        Dictionary<string, decimal?>? cmp = a.CompareFrom is DateOnly cf && a.CompareTo is DateOnly cto ? await KpiAsync(cf, cto).ConfigureAwait(false) : null;
        var dto = new CouponAnalyticsDTO();
        foreach (string k in new[] { "issued", "distributed", "redeemed", "expired", "cancelled" })
        {
            dto.Kpis[k] = c.M("coupon." + k, cur[k], MetricUnit.Count, MetricKind.Fact, cmp?[k]);
        }

        dto.Kpis["usageRate"] = c.M("coupon.usageRate", cur["usageRate"], MetricUnit.Percent, MetricKind.Computed, cmp?["usageRate"],
            batchId is null ? null : AnaFormula.MissingSource);
        dto.Kpis["avgDaysToRedeem"] = c.M("coupon.avgDaysToRedeem", null, MetricUnit.Days, MetricKind.Computed, insufficient: AnaFormula.MissingSource);

        // ยอดขายที่ใช้คูปอง: สิทธิ์ของแคมเปญที่ต้องใช้คูปอง
        HashSet<string> couponCampaigns = [.. (await _campaigns.ListByStatusAsync(Enum.GetValues<CampaignStatus>(), ct).ConfigureAwait(false))
            .Where(x => x.RequiresCoupon && (code is null || string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))).Select(x => x.Code)];
        List<AnaMemberDayDTO> sales = await DaysAsync(c, c.F.From, c.F.To, ct).ConfigureAwait(false);
        dto.Kpis["salesWithCoupon"] = c.M("coupon.salesWithCoupon", sales.Sum(r => r.Benefits.Where(b => couponCampaigns.Contains(b.Key)).Sum(b => b.Value.Sales)), MetricUnit.Baht);
        foreach (CouponMetricEvent e in new[] { CouponMetricEvent.Issued, CouponMetricEvent.Redeemed, CouponMetricEvent.Expired })
        {
            dto.Trend.Add(Series(c, "coupon." + e.ToString().ToLowerInvariant(), MetricUnit.Count, (f, t) => Count(f, t, e), withCompare: false));
        }

        // ป้าย: ใช้ต่ำ = < 5% เมื่อผ่านช่วงใช้ได้ ≥ 50%, ผิดปกติ = วันใดใช้ > 3 × ค่าเฉลี่ย 7 วันก่อนหน้า
        if (!c.Empty)
        {
            Dictionary<string, CampaignDTO> byCode = (await _campaigns.ListByStatusAsync(Enum.GetValues<CampaignStatus>(), ct).ConfigureAwait(false))
                .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            DateTimeOffset now = _clock.GetUtcNow();
            foreach (IGrouping<string, AnaCouponDayDTO> g in rows.Where(x => x.Day >= c.F.From && x.CampaignCode.Length > 0).GroupBy(x => x.CampaignCode, StringComparer.OrdinalIgnoreCase))
            {
                int issued = g.Where(x => x.Event == CouponMetricEvent.Issued).Sum(x => x.Count);
                int redeemed = g.Where(x => x.Event == CouponMetricEvent.Redeemed).Sum(x => x.Count) - g.Where(x => x.Event == CouponMetricEvent.Reversed).Sum(x => x.Count);
                decimal? rate = AnaMath.Pct(redeemed, issued);
                string? flag = null;
                if (byCode.TryGetValue(g.Key, out CampaignDTO? camp) && rate < 5 && camp.EndAt > camp.StartAt
                    && (now - camp.StartAt).TotalSeconds >= (camp.EndAt - camp.StartAt).TotalSeconds / 2)
                {
                    flag = "LowUsage";
                }

                var daily = g.Where(x => x.Event == CouponMetricEvent.Redeemed).GroupBy(x => x.Day).ToDictionary(x => x.Key, x => x.Sum(y => y.Count));
                if (daily.Any(d => Enumerable.Range(1, 7).Select(i => daily.GetValueOrDefault(d.Key.AddDays(-i))).Average() is double avg && avg > 0 && d.Value > 3 * avg))
                {
                    flag = "Abnormal";
                }

                dto.ByCampaign.Add(new CouponCampaignRowDTO { CampaignCode = g.Key, Issued = issued, Redeemed = redeemed, UsageRate = rate, Flag = flag });
            }

            dto.ByCampaign = [.. dto.ByCampaign.OrderByDescending(x => x.Redeemed)];
        }

        return await WrapAsync(c, dto, ct, AnaTables.Coupons).ConfigureAwait(false);
    }

    // ================================================================== แคมเปญ

    /// <summary>ตัวกรองของรายงานแคมเปญ: ไม่ส่งช่วงวัน = ช่วงแคมเปญ (ถึงเมื่อวาน, ไม่เกิน 731 วัน)</summary>
    private AnalyticsFilterDTO CampaignFilter(AnalyticsFilterDTO? filter, CampaignDTO campaign)
    {
        AnalyticsFilterDTO f = filter?.Clone() ?? new AnalyticsFilterDTO();
        if (f.From is null && f.To is null)
        {
            DateOnly yesterday = AnaTime.Today(_clock).AddDays(-1);
            DateOnly start = AnaTime.Day(campaign.StartAt);
            DateOnly end = AnaTime.Day(campaign.EndAt);
            f.To = end < yesterday ? end : yesterday < start ? start : yesterday;
            f.From = start > f.To ? f.To : start;
            if (f.To.Value.DayNumber - f.From.Value.DayNumber + 1 > AnaFilterResolver.MaxRangeDays)
            {
                f.From = f.To.Value.AddDays(-AnaFilterResolver.MaxRangeDays + 1);
            }
        }

        return f;
    }

    /// <summary>สมาชิกเป้าหมายของแคมเปญ แยกกลุ่มทดลอง / ควบคุม</summary>
    private async Task<(HashSet<long> Treated, HashSet<long> Control)> TargetsAsync(Ctx c, CampaignDTO campaign, CancellationToken ct)
    {
        HashSet<long>? seg = null;
        if (!campaign.Targets.AllMembers && campaign.Targets.SegmentIds.Count > 0)
        {
            seg = [];
            foreach (long sid in campaign.Targets.SegmentIds)
            {
                for (int page = 1; ; page++)
                {
                    PagedResultDTO<long> p = await _segments.PageMemberIdsAsync(sid, page, 5000, ct).ConfigureAwait(false);
                    seg.UnionWith(p.Items);
                    if (p.Items.Count < 5000)
                    {
                        break;
                    }
                }
            }
        }

        var treated = new HashSet<long>();
        var control = new HashSet<long>();
        foreach (AnaMemberSnapshotDTO m in c.Members.Where(m => m.Status == MemberStatus.Active || m.BillsTotal > 0))
        {
            bool target = campaign.Targets.AllMembers
                || (m.TierCode is not null && campaign.Targets.TierCodes.Contains(m.TierCode, StringComparer.OrdinalIgnoreCase))
                || (seg?.Contains(m.MemberId) ?? false);
            if (!target)
            {
                continue;
            }

            (HoldoutAssigner.IsControl(campaign.Code, m.MemberNo, campaign.HoldoutPct) ? control : treated).Add(m.MemberId);
        }

        return (treated, control);
    }

    /// <summary>ยอดที่นับให้แคมเปญตามวิธีนับ (1 แถว = สมาชิก/วัน/สาขา/ช่องทาง)</summary>
    internal sealed record Attributed(AnaMemberDayDTO Row, int Bills, decimal Sales, decimal Discount, int BonusPoints);

    private async Task<List<Attributed>> AttributeAsync(Ctx c, CampaignDTO campaign, Attribution attribution, List<AnaMemberDayDTO> rows, HashSet<long> treated, CancellationToken ct)
    {
        var result = new List<Attributed>();
        Dictionary<long, List<DateOnly>> messaged = [];
        if (attribution == Attribution.Influenced)
        {
            (DateTimeOffset f, DateTimeOffset t) = AnaTime.Range(c.F.From.AddDays(-c.S.InfluenceWindowDays), c.F.To);
            foreach (MessageRecipientDTO r in await _messages.ListRecipientsAsync(campaign.Code, f, t, ct).ConfigureAwait(false))
            {
                if (!messaged.TryGetValue(r.MemberId, out List<DateOnly>? l))
                {
                    l = [];
                    messaged[r.MemberId] = l;
                }

                l.Add(AnaTime.Day(r.SentAt));
            }
        }

        DateOnly cs = AnaTime.Day(campaign.StartAt), ce = AnaTime.Day(campaign.EndAt);
        foreach (AnaMemberDayDTO r in rows)
        {
            AnaBenefitAggDTO? b = r.Benefits.GetValueOrDefault(campaign.Code);
            switch (attribution)
            {
                case Attribution.InPeriod when r.Day >= cs && r.Day <= ce && treated.Contains(r.MemberId):
                    result.Add(new Attributed(r, r.Bills, r.Net, b?.Discount ?? 0, b?.BonusPoints ?? 0));
                    break;
                case Attribution.BenefitUsed when b is not null:
                    result.Add(new Attributed(r, b.Bills, b.Sales, b.Discount, b.BonusPoints));
                    break;
                case Attribution.Influenced when b is not null:
                    result.Add(new Attributed(r, b.Bills, b.Sales, b.Discount, b.BonusPoints));
                    break;
                case Attribution.Influenced when messaged.TryGetValue(r.MemberId, out List<DateOnly>? sent) && sent.Any(d => r.Day >= d && r.Day.DayNumber - d.DayNumber <= c.S.InfluenceWindowDays):
                    result.Add(new Attributed(r, r.Bills, r.Net, 0, 0));
                    break;
                default:
                    break;
            }
        }

        return result;
    }

    private static CampaignAnalyticsSummaryDTO Summary(CampaignDTO x, List<Attributed> att, int? eligible, bool hasCost) => new()
    {
        CampaignCode = x.Code, Name = x.Name, Status = x.Status, RewardType = x.RewardType, StartAt = x.StartAt, EndAt = x.EndAt, HoldoutPct = x.HoldoutPct,
        EligibleMembers = eligible, Participants = att.Select(a => a.Row.MemberId).Distinct().Count(), CampaignSales = att.Sum(a => a.Sales),
        Discount = att.Sum(a => a.Discount), HasCost = hasCost,
    };

    /// <summary>AN-12 รายการแคมเปญพร้อมผลรวม (วิธีนับ BenefitUsed)</summary>
    public async Task<AnalyticsResultDTO<PagedResultDTO<CampaignAnalyticsSummaryDTO>>> CampaignsAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CampaignAnalyticsCriteria criteria,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(criteria);
        caller.Require(AnaPermissions.PromotionRead);
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        DateTimeOffset start = AnaTime.Start(c.F.From), end = AnaTime.Start(c.F.To.AddDays(1));
        List<CampaignDTO> list = [.. (await VisibleCampaignsAsync(caller, ct).ConfigureAwait(false))
            .Where(x => x.StartAt < end && x.EndAt >= start)
            .Where(x => criteria.Status.Count == 0 || criteria.Status.Contains(x.Status))
            .Where(x => criteria.RewardType.Count == 0 || criteria.RewardType.Contains(x.RewardType))
            .Where(x => c.F.Applied.Campaign.Count == 0 || c.F.Applied.Campaign.Contains(x.Code, StringComparer.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(criteria.Text) || x.Code.Contains(criteria.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains(criteria.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.StartAt)];
        List<AnaMemberDayDTO> rows = await DaysAsync(c, c.F.From, c.F.To, ct).ConfigureAwait(false);
        HashSet<string> withCost = [.. (await CampaignCostsAsync(ct).ConfigureAwait(false)).Select(x => x.CampaignCode.ToUpperInvariant())];
        List<CampaignAnalyticsSummaryDTO> all = [.. list.Select(x => Summary(x, [.. rows.Where(r => r.Benefits.ContainsKey(x.Code))
            .Select(r => new Attributed(r, r.Benefits[x.Code].Bills, r.Benefits[x.Code].Sales, r.Benefits[x.Code].Discount, r.Benefits[x.Code].BonusPoints))], null,
            withCost.Contains(x.Code.ToUpperInvariant())))];
        return await WrapAsync(c, AnaPaged.Of(all, criteria.Page, criteria.PageSize), ct, AnaTables.Sales).ConfigureAwait(false);
    }

    private async Task<(Ctx C, CampaignDTO Campaign, List<AnaMemberDayDTO> Rows, HashSet<long> Treated, HashSet<long> Control)> CampaignContextAsync(MembershipCaller caller, string code,
        AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        CampaignDTO campaign = await VisibleCampaignAsync(caller, code, ct).ConfigureAwait(false);
        Ctx c = await ContextAsync(caller, CampaignFilter(filter, campaign), ct).ConfigureAwait(false);
        List<AnaMemberDayDTO> rows = await DaysAsync(c, c.F.From, c.F.To, ct).ConfigureAwait(false);
        (HashSet<long> treated, HashSet<long> control) = await TargetsAsync(c, campaign, ct).ConfigureAwait(false);
        return (c, campaign, rows, treated, control);
    }

    private async Task<Dictionary<string, MetricValueDTO>> CampaignKpisAsync(Ctx c, CampaignDTO campaign, List<Attributed> att, HashSet<long> treated, CancellationToken ct)
    {
        int participants = att.Select(a => a.Row.MemberId).Distinct().Count();
        int bills = att.Sum(a => a.Bills);
        decimal sales = att.Sum(a => a.Sales);
        (DateTimeOffset f, DateTimeOffset t) = AnaTime.Range(c.F.From, c.F.To);
        int messaged = c.Empty ? 0 : (await _messages.ListRecipientsAsync(campaign.Code, f, t, ct).ConfigureAwait(false)).Select(r => r.MemberId).Distinct().Count();
        List<AnaCouponDayDTO> cp = c.Empty ? [] : [.. (await _model.ListCouponDaysAsync(c.F.From, c.F.To, ct).ConfigureAwait(false))
            .Where(x => string.Equals(x.CampaignCode, campaign.Code, StringComparison.OrdinalIgnoreCase) && (InBranch(c, x.BranchCode) || x.BranchCode.Length == 0))];
        string drill = "campaign." + campaign.Code;
        return new()
        {
            ["eligibleMembers"] = c.M("campaign.eligibleMembers", treated.Count, MetricUnit.Count),
            ["messaged"] = c.M("campaign.messaged", messaged, MetricUnit.Count),
            ["participants"] = c.M("campaign.participants", participants, MetricUnit.Count, drilldown: drill + ".participants"),
            ["participationRate"] = c.M("campaign.participationRate", AnaMath.Pct(participants, treated.Count), MetricUnit.Percent, MetricKind.Computed),
            ["transactions"] = c.M("campaign.transactions", bills, MetricUnit.Count, drilldown: drill),
            ["campaignSales"] = c.M("campaign.sales", sales, MetricUnit.Baht, drilldown: drill),
            ["avgTicket"] = c.M("campaign.avgTicket", AnaMath.Div(sales, bills), MetricUnit.Baht, MetricKind.Computed),
            ["discount"] = c.M("campaign.discount", att.Sum(a => a.Discount), MetricUnit.Baht),
            ["pointsGiven"] = c.M("campaign.pointsGiven", att.Sum(a => a.BonusPoints), MetricUnit.Points),
            ["couponsIssued"] = c.M("campaign.couponsIssued", cp.Where(x => x.Event == CouponMetricEvent.Issued).Sum(x => x.Count), MetricUnit.Count),
            ["couponsRedeemed"] = c.M("campaign.couponsRedeemed",
                cp.Where(x => x.Event == CouponMetricEvent.Redeemed).Sum(x => x.Count) - cp.Where(x => x.Event == CouponMetricEvent.Reversed).Sum(x => x.Count), MetricUnit.Count),
            ["newMembers"] = c.M("campaign.newMembers", c.Members.Count(m => string.Equals(m.SourceCampaignCode, campaign.Code, StringComparison.OrdinalIgnoreCase)
                && InRange(m.RegisteredAt, c.F.From, c.F.To)), MetricUnit.Count),
            ["budgetUsedPct"] = c.M("campaign.budgetUsedPct", campaign.Limits.Budget is > 0 ? AnaMath.Pct(campaign.Usage.BudgetUsed, campaign.Limits.Budget.Value) : null,
                MetricUnit.Percent, MetricKind.Computed, insufficient: campaign.Limits.Budget is > 0 ? null : AnaFormula.MissingSource),
        };
    }

    /// <summary>AN-13 ภาพรวมแคมเปญ</summary>
    public async Task<AnalyticsResultDTO<CampaignReportDTO>> CampaignReportAsync(MembershipCaller caller, string code, AnalyticsFilterDTO? filter, Attribution attribution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        (Ctx c, CampaignDTO campaign, List<AnaMemberDayDTO> rows, HashSet<long> treated, _) = await CampaignContextAsync(caller, code, filter, ct).ConfigureAwait(false);
        List<Attributed> att = await AttributeAsync(c, campaign, attribution, rows, treated, ct).ConfigureAwait(false);
        bool hasCost = (await CampaignCostsAsync(ct).ConfigureAwait(false)).Any(x => string.Equals(x.CampaignCode, campaign.Code, StringComparison.OrdinalIgnoreCase));
        var segments = new List<TargetSegmentDTO>();
        foreach (long sid in campaign.Targets.SegmentIds)
        {
            if (await _segments.GetAsync(sid, ct).ConfigureAwait(false) is SegmentDTO s)
            {
                segments.Add(new TargetSegmentDTO { SegmentId = s.Id, Name = s.Name });
            }
        }

        var dto = new CampaignReportDTO
        {
            Campaign = Summary(campaign, att, treated.Count, hasCost), TargetSegments = segments, Attribution = attribution, InfluenceWindowDays = c.S.InfluenceWindowDays,
            Kpis = await CampaignKpisAsync(c, campaign, att, treated, ct).ConfigureAwait(false),
            Trend =
            [
                Series(c, "campaign.sales", MetricUnit.Baht, (f, t) => att.Where(a => a.Row.Day >= f && a.Row.Day <= t).Sum(a => a.Sales), withCompare: false),
                Series(c, "campaign.participants", MetricUnit.Count, (f, t) => att.Where(a => a.Row.Day >= f && a.Row.Day <= t).Select(a => a.Row.MemberId).Distinct().Count(), withCompare: false),
            ],
        };
        return await WrapAsync(c, dto, ct, AnaTables.Sales, AnaTables.Coupons).ConfigureAwait(false);
    }

    /// <summary>AN-13 แยกมิติ</summary>
    public async Task<AnalyticsResultDTO<List<BreakdownRowDTO>>> CampaignBreakdownAsync(MembershipCaller caller, string code, AnalyticsFilterDTO? filter, CampaignDimension dimension,
        Attribution attribution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        (Ctx c, CampaignDTO campaign, List<AnaMemberDayDTO> rows, HashSet<long> treated, _) = await CampaignContextAsync(caller, code, filter, ct).ConfigureAwait(false);
        List<Attributed> att = await AttributeAsync(c, campaign, attribution, rows, treated, ct).ConfigureAwait(false);
        Dictionary<string, string> branchNames = await BranchNamesAsync(ct).ConfigureAwait(false);
        IEnumerable<(string Key, Attributed A, decimal Share)> parts = dimension switch
        {
            CampaignDimension.Branch => att.Select(a => (a.Row.BranchCode, a, 1m)),
            CampaignDimension.Channel => att.Select(a => (a.Row.Channel.ToString(), a, 1m)),
            CampaignDimension.Tier => att.Select(a => (c.All.GetValueOrDefault(a.Row.MemberId)?.TierCode ?? string.Empty, a, 1m)),
            CampaignDimension.Day => att.Select(a => (a.Row.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), a, 1m)),
            CampaignDimension.Category => att.SelectMany(a => a.Row.Categories.Select(x => (x.Key, a, a.Row.Net != 0 ? x.Value / a.Row.Net : 0))),
            _ => att.SelectMany(a => a.Row.Brands.Select(x => (x.Key, a, a.Row.Net != 0 ? x.Value / a.Row.Net : 0))),
        };
        List<BreakdownRowDTO> result = c.Empty ? [] : [.. parts.GroupBy(x => x.Key).Select(g =>
        {
            int bills = g.Sum(x => x.Share == 1m ? x.A.Bills : 0);
            decimal sales = Math.Round(g.Sum(x => x.A.Sales * x.Share), 2);
            return new BreakdownRowDTO
            {
                Key = g.Key, Label = g.Key.Length == 0 ? "ไม่ระบุ" : dimension == CampaignDimension.Branch ? branchNames.GetValueOrDefault(g.Key, g.Key) : g.Key,
                Participants = g.Select(x => x.A.Row.MemberId).Distinct().Count(), Transactions = bills, Sales = sales,
                Discount = Math.Round(g.Sum(x => x.A.Discount * x.Share), 2), AvgTicket = bills == 0 ? null : Math.Round(sales / bills, 2),
            };
        }).OrderBy(x => dimension == CampaignDimension.Day ? x.Key : string.Empty, StringComparer.Ordinal).ThenByDescending(x => x.Sales)];
        return await WrapAsync(c, result, ct, AnaTables.Sales).ConfigureAwait(false);
    }

    private static (CampaignLiftDTO Lift, decimal? GrossMarginTreated, decimal? Coverage) Lift(Ctx c, CampaignDTO campaign, List<AnaMemberDayDTO> rows, HashSet<long> treated, HashSet<long> control)
    {
        var dto = new CampaignLiftDTO { HoldoutPct = campaign.HoldoutPct, HasControl = campaign.HoldoutPct > 0 && control.Count > 0, Treated = treated.Count, Control = control.Count };
        DateOnly cs = AnaTime.Day(campaign.StartAt), ce = AnaTime.Day(campaign.EndAt);
        List<AnaMemberDayDTO> period = [.. rows.Where(r => r.Day >= cs && r.Day <= ce)];
        List<AnaMemberDayDTO> tRows = [.. period.Where(r => treated.Contains(r.MemberId))];
        List<AnaMemberDayDTO> cRows = [.. period.Where(r => control.Contains(r.MemberId))];
        decimal tNet = tRows.Sum(r => r.Net), tCostedNet = tRows.Sum(r => r.CostedNet), tCost = tRows.Sum(r => r.Cost);
        decimal? coverage = AnaMath.Pct(tCostedNet, tNet);
        decimal? margin = tCostedNet > 0 ? (tCostedNet - tCost) / tCostedNet : null;
        if (!dto.HasControl)
        {
            string reason = AnaFormula.NoControlGroup;
            dto.PurchaseRateTreated = c.M("lift.purchaseRateTreated", null, MetricUnit.Percent, MetricKind.Computed, insufficient: reason);
            dto.PurchaseRateControl = c.M("lift.purchaseRateControl", null, MetricUnit.Percent, MetricKind.Computed, insufficient: reason);
            dto.SalesPerMemberTreated = c.M("lift.salesPerMemberTreated", null, MetricUnit.Baht, MetricKind.Computed, insufficient: reason);
            dto.SalesPerMemberControl = c.M("lift.salesPerMemberControl", null, MetricUnit.Baht, MetricKind.Computed, insufficient: reason);
            dto.Lift = c.M("lift.purchaseRateDiff", null, MetricUnit.Percent, MetricKind.Estimate, insufficient: reason);
            dto.UpliftSales = c.M("lift.upliftSales", null, MetricUnit.Baht, MetricKind.Estimate, insufficient: reason);
            return (dto, margin, coverage);
        }

        int tBuy = tRows.Where(r => r.Bills > 0).Select(r => r.MemberId).Distinct().Count();
        int cBuy = cRows.Where(r => r.Bills > 0).Select(r => r.MemberId).Distinct().Count();
        decimal st = tNet / treated.Count, sc = cRows.Sum(r => r.Net) / control.Count;
        string? small = control.Count < c.S.MinGroupSize || treated.Count < c.S.MinGroupSize ? AnaFormula.GroupTooSmall : null;
        dto.PurchaseRateTreated = c.M("lift.purchaseRateTreated", AnaMath.Pct(tBuy, treated.Count), MetricUnit.Percent, MetricKind.Computed);
        dto.PurchaseRateControl = c.M("lift.purchaseRateControl", AnaMath.Pct(cBuy, control.Count), MetricUnit.Percent, MetricKind.Computed);
        dto.SalesPerMemberTreated = c.M("lift.salesPerMemberTreated", st, MetricUnit.Baht, MetricKind.Computed);
        dto.SalesPerMemberControl = c.M("lift.salesPerMemberControl", sc, MetricUnit.Baht, MetricKind.Computed);
        (decimal diff, decimal low, decimal high) = AnaMath.TwoProportion(tBuy, treated.Count, cBuy, control.Count);
        dto.Lift = c.M("lift.purchaseRateDiff", diff, MetricUnit.Percent, MetricKind.Estimate, insufficient: small);
        if (small is null && !c.Empty)
        {
            dto.Lift.CiLow = low;
            dto.Lift.CiHigh = high;
            dto.Lift.Significant = low > 0 || high < 0;
        }

        dto.UpliftSales = c.M("lift.upliftSales", (st - sc) * treated.Count, MetricUnit.Baht, MetricKind.Estimate, insufficient: small);
        return (dto, margin, coverage);
    }

    /// <summary>AN-13 กลุ่มควบคุม</summary>
    public async Task<AnalyticsResultDTO<CampaignLiftDTO>> CampaignLiftAsync(MembershipCaller caller, string code, AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        (Ctx c, CampaignDTO campaign, List<AnaMemberDayDTO> rows, HashSet<long> treated, HashSet<long> control) = await CampaignContextAsync(caller, code, filter, ct).ConfigureAwait(false);
        return await WrapAsync(c, Lift(c, campaign, rows, treated, control).Lift, ct, AnaTables.Sales, AnaTables.Members).ConfigureAwait(false);
    }

    /// <summary>AN-13 ต้นทุน / ROI</summary>
    public async Task<AnalyticsResultDTO<CampaignRoiDTO>> CampaignRoiAsync(MembershipCaller caller, string code, AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        (Ctx c, CampaignDTO campaign, List<AnaMemberDayDTO> rows, HashSet<long> treated, HashSet<long> control) = await CampaignContextAsync(caller, code, filter, ct).ConfigureAwait(false);
        List<Attributed> att = await AttributeAsync(c, campaign, Attribution.BenefitUsed, rows, treated, ct).ConfigureAwait(false);
        List<CampaignCostIngestDTO> costs = [.. (await CampaignCostsAsync(ct).ConfigureAwait(false)).Where(x => string.Equals(x.CampaignCode, campaign.Code, StringComparison.OrdinalIgnoreCase))];
        (CampaignLiftDTO lift, decimal? margin, decimal? coverage) = Lift(c, campaign, rows, treated, control);
        decimal d = att.Sum(a => a.Discount), v = att.Sum(a => a.BonusPoints) * c.S.PointCostBaht, k = costs.Sum(x => x.Amount);
        decimal sales = att.Sum(a => a.Sales);
        decimal costedNet = att.Sum(a => a.Row.Net != 0 ? a.Row.CostedNet * a.Sales / a.Row.Net : 0), cost = att.Sum(a => a.Row.Net != 0 ? a.Row.Cost * a.Sales / a.Row.Net : 0);
        decimal? cov = AnaMath.Pct(costedNet, sales);
        string? low = cov is null || cov < c.S.CostCoverageMinPct ? AnaFormula.CostCoverageLow : null;
        decimal? g = low is null ? sales - (costedNet > 0 ? cost * sales / costedNet : 0) : null;
        decimal spend = d + v + k;
        string? roiReason = !lift.HasControl ? AnaFormula.NoControlGroup : lift.UpliftSales.InsufficientReason
            ?? (coverage is null || coverage < c.S.CostCoverageMinPct || margin is null ? AnaFormula.CostCoverageLow : null);
        decimal? ge = roiReason is null ? lift.UpliftSales.Value * margin : null;
        var dto = new CampaignRoiDTO
        {
            Discount = c.M("roi.discount", d, MetricUnit.Baht), PointsCost = c.M("roi.pointsCost", v, MetricUnit.Baht, MetricKind.Computed),
            CampaignCost = c.M("roi.campaignCost", k, MetricUnit.Baht), CostIncluded = costs.Count > 0, CostCoveragePct = cov,
            GrossProfit = c.M("roi.grossProfit", g, MetricUnit.Baht, MetricKind.Computed, insufficient: low),
            GrossMarginPct = c.M("roi.grossMarginPct", g is decimal gg ? AnaMath.Pct(gg, sales) : null, MetricUnit.Percent, MetricKind.Computed, insufficient: low),
            Roi = c.M("roi.roi", ge is decimal e && spend > 0 ? AnaMath.Pct(e - k, spend) : null, MetricUnit.Percent, MetricKind.Estimate,
                insufficient: roiReason ?? (spend == 0 ? AnaFormula.ZeroDenominator : null)),
            BreakEvenSales = c.M("roi.breakEvenSales", margin is decimal m && m > 0 && coverage >= c.S.CostCoverageMinPct ? spend / m : null, MetricUnit.Baht, MetricKind.Estimate,
                insufficient: margin is null || coverage is null || coverage < c.S.CostCoverageMinPct ? AnaFormula.CostCoverageLow : null),
        };
        return await WrapAsync(c, dto, ct, AnaTables.Sales).ConfigureAwait(false);
    }

    /// <summary>AN-14 เปรียบเทียบ 2–5 แคมเปญ</summary>
    public async Task<AnalyticsResultDTO<CampaignCompareDTO>> CompareCampaignsAsync(MembershipCaller caller, IReadOnlyList<string> codes, AnalyticsFilterDTO? filter, Attribution attribution,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(codes);
        List<string> distinct = [.. codes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant()).Distinct()];
        if (distinct.Count is < 2 or > 5)
        {
            throw AnaErrors.Bad("compare_limit", "Choose 2 to 5 campaigns.");
        }

        var dto = new CampaignCompareDTO();
        Ctx? last = null;
        foreach (string code in distinct)
        {
            (Ctx c, CampaignDTO campaign, List<AnaMemberDayDTO> rows, HashSet<long> treated, _) = await CampaignContextAsync(caller, code, filter, ct).ConfigureAwait(false);
            List<Attributed> att = await AttributeAsync(c, campaign, attribution, rows, treated, ct).ConfigureAwait(false);
            bool hasCost = (await CampaignCostsAsync(ct).ConfigureAwait(false)).Any(x => string.Equals(x.CampaignCode, campaign.Code, StringComparison.OrdinalIgnoreCase));
            CampaignAnalyticsSummaryDTO s = Summary(campaign, att, treated.Count, hasCost);
            s.Kpis = await CampaignKpisAsync(c, campaign, att, treated, ct).ConfigureAwait(false);
            dto.Campaigns.Add(s);
            last = c;
        }

        static double Days(CampaignAnalyticsSummaryDTO s) => Math.Max((s.EndAt - s.StartAt).TotalDays, 1);
        List<CampaignAnalyticsSummaryDTO> cs = dto.Campaigns;
        if (cs.Max(Days) > 2 * cs.Min(Days)) { dto.Warnings.Add("duration_differs"); }
        if (cs.Max(x => x.EligibleMembers ?? 0) > 5 * Math.Max(cs.Min(x => x.EligibleMembers ?? 0), 1)) { dto.Warnings.Add("eligible_size_differs"); }
        if (cs.Select(x => x.RewardType).Distinct().Count() > 1) { dto.Warnings.Add("reward_type_differs"); }
        if (cs.Any(x => x.HoldoutPct > 0) && cs.Any(x => x.HoldoutPct == 0)) { dto.Warnings.Add("control_mismatch"); }
        return await WrapAsync(last!, dto, ct, AnaTables.Sales).ConfigureAwait(false);
    }

    // ================================================================== Funnel (§37)

    /// <summary>AN-16 ผล funnel</summary>
    public async Task<AnalyticsResultDTO<FunnelResultDTO>> FunnelResultAsync(MembershipCaller caller, Guid funnelId, AnalyticsFilterDTO? filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.PromotionRead);
        FunnelDTO funnel = (await _docs.GetAsync<FunnelDTO>(AnaCollections.Funnel, funnelId.ToString(), ct).ConfigureAwait(false))?.Value
            ?? throw new NotFoundException("Funnel not found.");
        Ctx c = await ContextAsync(caller, filter, ct).ConfigureAwait(false);
        HashSet<string> visible = [.. (await VisibleCampaignsAsync(caller, ct).ConfigureAwait(false)).Select(x => x.Code)];
        List<string> campaigns = [.. c.F.Applied.Campaign.Where(visible.Contains)];
        bool campaignFilter = c.F.Applied.Campaign.Count > 0;
        HashSet<long> members = [.. c.Members.Select(m => m.MemberId)];
        (DateTimeOffset from, DateTimeOffset to) = AnaTime.Range(c.F.From, c.F.To.AddDays(Math.Max(1, funnel.MaxGapHours / 24)));

        // Step 1: เวลาของเหตุการณ์แต่ละขั้นต่อสมาชิก
        var times = new Dictionary<FunnelStep, Dictionary<long, List<DateTimeOffset>>>();
        void Add(FunnelStep s, long m, DateTimeOffset at)
        {
            if (!members.Contains(m))
            {
                return;
            }

            if (!times.TryGetValue(s, out Dictionary<long, List<DateTimeOffset>>? d))
            {
                d = [];
                times[s] = d;
            }

            if (!d.TryGetValue(m, out List<DateTimeOffset>? l))
            {
                l = [];
                d[m] = l;
            }

            l.Add(at);
        }

        if (!c.Empty)
        {
            IEnumerable<MessageEventDTO> events = campaignFilter
                ? (await Task.WhenAll(campaigns.Select(x => _messages.ListEventsAsync(x, from, to, ct))).ConfigureAwait(false)).SelectMany(x => x)
                : await _messages.ListEventsAsync(null, from, to, ct).ConfigureAwait(false);
            foreach (MessageEventDTO e in events)
            {
                // เหตุการณ์ขั้นหลังของข้อความเดียวกันนับว่าผ่านขั้นก่อนหน้าด้วย (คลิก ⇒ เปิด ⇒ ได้รับ)
                switch (e.Type)
                {
                    case MessageEventType.Clicked:
                        Add(FunnelStep.MessageClicked, e.MemberId, e.OccurredAt);
                        Add(FunnelStep.MessageOpened, e.MemberId, e.OccurredAt);
                        Add(FunnelStep.MessageReceived, e.MemberId, e.OccurredAt);
                        break;
                    case MessageEventType.Opened:
                        Add(FunnelStep.MessageOpened, e.MemberId, e.OccurredAt);
                        Add(FunnelStep.MessageReceived, e.MemberId, e.OccurredAt);
                        break;
                    case MessageEventType.Delivered:
                    case MessageEventType.Sent:
                        Add(FunnelStep.MessageReceived, e.MemberId, e.OccurredAt);
                        break;
                    case MessageEventType.PortalView:
                        Add(FunnelStep.PortalView, e.MemberId, e.OccurredAt);
                        break;
                    default:
                        break;
                }
            }

            foreach (AnaMemberSnapshotDTO m in c.Members.Where(m => m.RegisteredAt >= from && m.RegisteredAt < to))
            {
                Add(FunnelStep.Registered, m.MemberId, m.RegisteredAt);
            }

            List<AnaMemberDayDTO> rows = await DaysAsync(c, c.F.From, AnaTime.Day(to.AddSeconds(-1)), ct).ConfigureAwait(false);
            foreach (IGrouping<long, AnaMemberDayDTO> g in rows.Where(r => r.Bills > 0).GroupBy(r => r.MemberId))
            {
                List<DateOnly> ds = [.. g.Select(r => r.Day).Distinct().Order()];
                // ยอดรายวัน: เวลาซื้อ = สิ้นวัน (ไม่ทำให้ซื้อก่อนเหตุการณ์ในวันเดียวกัน)
                foreach (DateOnly d in ds)
                {
                    Add(FunnelStep.Purchased, g.Key, AnaTime.Start(d.AddDays(1)).AddSeconds(-1));
                }

                foreach (DateOnly d in ds.Skip(1))
                {
                    Add(FunnelStep.RepeatPurchased, g.Key, AnaTime.Start(d.AddDays(1)).AddSeconds(-1));
                }
            }

            // คูปองที่ได้รับ (แจกจ่ายให้สมาชิก)
            if (funnel.Steps.Contains(FunnelStep.CouponReceived))
            {
                var distributed = new List<CouponEventDTO>();
                await foreach (CouponEventDTO e in CouponEventsAsync(from, to, ct).ConfigureAwait(false))
                {
                    if (e.FromStatus == CouponStatus.Available && e.ToStatus == CouponStatus.Available && e.Reference is not null && !e.Reference.StartsWith("merge:", StringComparison.Ordinal))
                    {
                        distributed.Add(e);
                    }
                }

                var owner = new Dictionary<string, CouponDTO>(StringComparer.Ordinal);
                foreach (string[] chunk in distributed.Select(e => e.Code).Distinct(StringComparer.Ordinal).Chunk(500))
                {
                    foreach (CouponDTO cp in await _coupons.GetByCodesAsync(chunk, ct).ConfigureAwait(false))
                    {
                        owner[cp.Code] = cp;
                    }
                }

                HashSet<long> campaignIds = campaignFilter
                    ? [.. (await _campaigns.ListByStatusAsync(Enum.GetValues<CampaignStatus>(), ct).ConfigureAwait(false)).Where(x => campaigns.Contains(x.Code)).Select(x => x.Id)]
                    : [];
                foreach (CouponEventDTO e in distributed)
                {
                    if (owner.TryGetValue(e.Code, out CouponDTO? cp) && cp.MemberId is long mid && (!campaignFilter || campaignIds.Contains(cp.CampaignId)))
                    {
                        Add(FunnelStep.CouponReceived, mid, e.At);
                    }
                }
            }
        }

        // Step 2: นับแบบเรียงลำดับ
        var result = new FunnelResultDTO();
        TimeSpan gap = TimeSpan.FromHours(funnel.MaxGapHours);
        DateTimeOffset pStart = AnaTime.Start(c.F.From), pEnd = AnaTime.Start(c.F.To.AddDays(1));
        Dictionary<long, DateTimeOffset>? prev = null;
        int? firstCount = null, lastCount = null;
        foreach (FunnelStep step in funnel.Steps)
        {
            if (!times.TryGetValue(step, out Dictionary<long, List<DateTimeOffset>>? t) || t.Count == 0)
            {
                result.Steps.Add(new FunnelStepResultDTO { Step = step, InsufficientReason = c.Empty ? AnaFormula.ScopeEmpty : AnaFormula.MissingSource });
                continue;
            }

            var passed = new Dictionary<long, DateTimeOffset>();
            var hours = new List<decimal>();
            foreach ((long m, List<DateTimeOffset> list) in t)
            {
                if (prev is null)
                {
                    DateTimeOffset? first = list.Where(x => x >= pStart && x < pEnd).Order().Cast<DateTimeOffset?>().FirstOrDefault();
                    if (first is DateTimeOffset f)
                    {
                        passed[m] = f;
                    }
                }
                else if (prev.TryGetValue(m, out DateTimeOffset p) && list.Where(x => x >= p && x - p <= gap).Order().Cast<DateTimeOffset?>().FirstOrDefault() is DateTimeOffset n)
                {
                    passed[m] = n;
                    hours.Add((decimal)(n - p).TotalHours);
                }
            }

            int? before = prev?.Count;
            result.Steps.Add(new FunnelStepResultDTO
            {
                Step = step, Members = passed.Count, ConversionFromPrev = before is int b ? AnaMath.Pct(passed.Count, b) : null,
                DropOff = before is int b2 && AnaMath.Pct(passed.Count, b2) is decimal cv ? 100 - cv : null, MedianHoursFromPrev = AnaMath.Median(hours) is decimal md ? Math.Round(md, 2) : null,
            });
            firstCount ??= passed.Count;
            lastCount = passed.Count;
            prev = passed;
        }

        result.OverallConversion = c.M("funnel.overall", firstCount is int fc && lastCount is int lc ? AnaMath.Pct(lc, fc) : null, MetricUnit.Percent, MetricKind.Computed,
            insufficient: firstCount is null ? AnaFormula.MissingSource : null);
        return await WrapAsync(c, result, ct, AnaTables.Sales).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<CouponEventDTO> CouponEventsAsync(DateTimeOffset from, DateTimeOffset to, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long after = 0;
        while (true)
        {
            IReadOnlyList<CouponEventDTO> page = await _source.ListCouponEventsAsync(from, to, after, 1000, ct).ConfigureAwait(false);
            foreach (CouponEventDTO e in page)
            {
                yield return e;
            }

            if (page.Count < 1000)
            {
                yield break;
            }

            after = page[^1].Id;
        }
    }
}
