using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Core.Membership.Engine;

/// <summary>สิทธิ์คงเหลือของแคมเปญสำหรับสมาชิกคนนี้ (service คำนวณจากตัวนับก่อนเรียก engine)</summary>
public sealed class CampaignAllowance
{
    /// <summary>ยังใช้ได้ (ไม่เกินจำกัดสิทธิ)</summary>
    public bool Allowed { get; init; } = true;
    /// <summary>งบคงเหลือ (บาท, null = ไม่จำกัด)</summary>
    public decimal? BudgetRemaining { get; init; }
    /// <summary>เหตุผลที่ใช้ไม่ได้ (limit_reached / budget_exhausted)</summary>
    public string? Reason { get; init; }
}

/// <summary>คูปองที่ผ่านการตรวจสถานะแล้ว พร้อมแคมเปญของคูปอง</summary>
public sealed class CouponCandidate
{
    public string Code { get; init; } = string.Empty;
    public CampaignDTO Campaign { get; init; } = new();
}

/// <summary>ข้อมูลทั้งหมดที่ engine ต้องใช้ (ไม่มี I/O)</summary>
public sealed class BasketContext
{
    public QuoteRequest Request { get; init; } = new();
    public MemberEntity Member { get; init; } = new();
    public TierDTO? Tier { get; init; }
    public IReadOnlySet<long> SegmentIds { get; init; } = new HashSet<long>();
    /// <summary>แคมเปญที่สถานะใช้ได้แล้ว (Active หรือที่ระบุใน simulate)</summary>
    public IReadOnlyList<CampaignDTO> Campaigns { get; init; } = [];
    public IReadOnlyDictionary<long, CampaignAllowance> Allowances { get; init; } = new Dictionary<long, CampaignAllowance>();
    public IReadOnlyList<CouponCandidate> Coupons { get; init; } = [];
    /// <summary>คูปองที่ service ปฏิเสธไปแล้ว (หมดอายุ, ไม่ใช่ของสมาชิก ฯลฯ)</summary>
    public IReadOnlyList<RejectedCouponDTO> PreRejectedCoupons { get; init; } = [];
    public EarnRuleDTO? EarnRule { get; init; }
    public MembershipRulesDTO Rules { get; init; } = new();
    public int PointsAvailable { get; init; }
    public DateTimeOffset At { get; init; }
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;
}

/// <summary>
/// คำนวณตะกร้า: ส่วนลดโปรโมชัน 2 ชั้น (รายการ → บิล), คูปอง, ส่วนลดร้าน, จ่ายด้วยคะแนน และคะแนนที่ได้ — spec 2.1, 2.2, 2.5, 2.7
/// pure function: input เดิมได้ผลเดิมเสมอ (เรียงแคมเปญตาม priority แล้ว id)
/// </summary>
public static class PromotionEngine
{
    /// <summary>ผลของแคมเปญหนึ่งรายการ</summary>
    private sealed class Effect
    {
        public CampaignDTO Campaign { get; init; } = new();
        public decimal[] PerLine { get; set; } = [];
        public decimal Amount => PerLine.Sum();
        public int BonusPoints { get; set; }
        public string? CouponCode { get; init; }
        /// <summary>มูลค่าที่ใช้เปรียบเทียบ (ส่วนลด หรือ คะแนน × มูลค่า)</summary>
        public decimal Value { get; set; }
    }

    /// <summary>คำนวณตะกร้า</summary>
    /// <param name="ctx">ข้อมูล</param>
    /// <returns>ผลคำนวณ (ยังไม่มี QuoteId / ExpiresAt)</returns>
    public static QuoteDTO Calculate(BasketContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        QuoteRequest req = ctx.Request;
        List<QuoteLineRequest> lines = [.. req.Lines.OrderBy(l => l.LineNo)];
        int n = lines.Count;

        // Step 1: lineNet ของแต่ละบรรทัด
        decimal[] lineNet = [.. lines.Select(l => Math.Max(PointMath.Money(l.UnitPrice * l.Qty - l.LineDiscount), 0m))];
        var rejected = new List<RejectedCouponDTO>(ctx.PreRejectedCoupons);

        // Step 2: แคมเปญอัตโนมัติที่เข้าเงื่อนไขพื้นฐาน (เวลา สาขา ช่องทาง เป้าหมาย สิทธิคงเหลือ)
        List<CampaignDTO> auto = [.. ctx.Campaigns.Where(c => !c.RequiresCoupon && IsApplicable(c, ctx)).OrderBy(c => c.Priority).ThenBy(c => c.Id)];

        // Step 3: คูปอง (สูงสุดตามกฎ) — ตรวจเงื่อนไขของแคมเปญคูปองด้วย
        var coupons = new List<CouponCandidate>();
        foreach (CouponCandidate cc in ctx.Coupons)
        {
            if (coupons.Count >= ctx.Rules.MaxCouponsPerBill)
            {
                rejected.Add(new RejectedCouponDTO { Code = cc.Code, Reason = "coupon_limit_per_bill" });
            }
            else if (!IsApplicable(cc.Campaign, ctx))
            {
                rejected.Add(new RejectedCouponDTO { Code = cc.Code, Reason = ctx.Allowances.TryGetValue(cc.Campaign.Id, out CampaignAllowance? a) && a.Reason is not null ? a.Reason : "coupon_not_applicable" });
            }
            else
            {
                coupons.Add(cc);
            }
        }

        // Step 4: คำนวณส่วนลด — ถ้ามีคูปอง Exclusive เทียบ 2 ทาง (ใช้คูปอง vs โปรอัตโนมัติ) แล้วเลือกที่ลดมากกว่าให้สมาชิก
        List<CouponCandidate> discountCoupons = [.. coupons.Where(c => c.Campaign.IsDiscount)];
        List<CouponCandidate> exclusiveCoupons = [.. discountCoupons.Where(c => c.Campaign.StackMode == StackMode.Exclusive)];
        DiscountResult chosen = ComputeDiscounts(ctx, lines, lineNet, auto, discountCoupons, suppressAutoLevelFor: null);
        foreach (CouponCandidate ex in exclusiveCoupons)
        {
            DiscountResult withoutCoupon = ComputeDiscounts(ctx, lines, lineNet, auto, [.. discountCoupons.Where(c => c != ex)], suppressAutoLevelFor: null);
            DiscountResult couponAlone = ComputeDiscounts(ctx, lines, lineNet, auto, [.. discountCoupons.Where(c => c == ex || c.Campaign.StackMode != StackMode.Exclusive)], suppressAutoLevelFor: EffectiveLevel(ex.Campaign));
            if (couponAlone.Total > withoutCoupon.Total)
            {
                chosen = couponAlone;
            }
            else
            {
                chosen = withoutCoupon;
                discountCoupons.Remove(ex);
                rejected.Add(new RejectedCouponDTO { Code = ex.Code, Reason = "coupon_not_combinable" });
            }
        }

        rejected.AddRange(chosen.Rejected);
        var usedCoupons = new HashSet<string>(chosen.Effects.Where(e => e.CouponCode is not null).Select(e => e.CouponCode!), StringComparer.OrdinalIgnoreCase);

        // Step 5: ส่วนลดท้ายบิลของร้าน (กระจายตามสัดส่วน spec 2.1)
        decimal[] afterPromo = chosen.LineAfter;
        decimal shopDiscount = Math.Min(PointMath.Money(req.BillDiscount), afterPromo.Sum());
        decimal[] shopAlloc = PointMath.Allocate(shopDiscount, afterPromo);
        decimal[] linePaid = new decimal[n];
        for (int i = 0; i < n; i++)
        {
            linePaid[i] = Math.Max(afterPromo[i] - shopAlloc[i], 0m);
        }

        decimal netBeforePoints = linePaid.Sum();

        // Step 6: จ่ายด้วยคะแนน (spec 2.5)
        int points = Math.Max(req.PointsToRedeem, 0);
        string? pointsReason = null;
        bool couponUsed = usedCoupons.Count > 0 || coupons.Any(c => !c.Campaign.IsDiscount);
        if (points > 0 && couponUsed && !ctx.Rules.CouponWithPoints)
        {
            points = 0;
            pointsReason = "coupon_with_points_not_allowed";
        }

        if (points > 0)
        {
            decimal maxTender = PointMath.Money(netBeforePoints * ctx.Rules.MaxPointsPayPercent / 100m);
            int maxByTender = ctx.Rules.PointValue <= 0 ? 0 : PointMath.Floor(maxTender / ctx.Rules.PointValue);
            int allowed = Math.Min(Math.Min(points, Math.Max(ctx.PointsAvailable, 0)), maxByTender);
            if (allowed < points)
            {
                pointsReason = allowed == ctx.PointsAvailable && ctx.PointsAvailable < maxByTender ? "insufficient_points" : "max_points_pay_percent";
            }

            points = allowed;
        }

        decimal tender = PointMath.Money(points * ctx.Rules.PointValue);

        // Step 7: ฐานคะแนน (spec 2.2) — ยอดหลังส่วนลดของสินค้าที่ได้คะแนน หักส่วนที่จ่ายด้วยคะแนน
        EarnRuleDTO? rule = ctx.EarnRule;
        bool[] eligibleLine = new bool[n];
        for (int i = 0; i < n; i++)
        {
            eligibleLine[i] = rule is not null && IsEarningItem(lines[i], rule, ctx.Rules);
        }

        decimal eligibleGross = Enumerable.Range(0, n).Where(i => eligibleLine[i]).Sum(i => linePaid[i]);
        decimal eligible = Math.Max(eligibleGross - tender, 0m);
        decimal eligibleRatio = eligibleGross <= 0 ? 0 : eligible / eligibleGross;
        decimal tierMultiplier = ctx.Tier?.PointMultiplier ?? 1m;
        int basePoints = rule is null ? 0 : PointMath.BasePoints(eligible, rule.SpendPerPoint, rule.MinSpend, tierMultiplier);

        // Step 8: คะแนนพิเศษ / ออกคูปอง (อัตโนมัติ + จากคูปอง)
        var pointEffects = new List<Effect>();
        var issueEffects = new List<Effect>();
        IEnumerable<(CampaignDTO Campaign, string? Coupon)> nonDiscount = auto.Where(c => !c.IsDiscount).Select(c => (c, (string?)null))
            .Concat(coupons.Where(c => !c.Campaign.IsDiscount).Select(c => (c.Campaign, (string?)c.Code)));
        foreach ((CampaignDTO c, string? couponCode) in nonDiscount)
        {
            decimal matchedPaid = Enumerable.Range(0, n).Where(i => c.Conditions.MatchesItem(lines[i].ItemCode, lines[i].Category)).Sum(i => linePaid[i]);
            if (matchedPaid <= 0 || matchedPaid < c.Conditions.MinSpend)
            {
                if (couponCode is not null)
                {
                    rejected.Add(new RejectedCouponDTO { Code = couponCode, Reason = "coupon_min_spend_not_met" });
                }

                continue;
            }

            var e = new Effect { Campaign = c, PerLine = new decimal[n], CouponCode = couponCode };
            if (c.RewardType == RewardType.IssueCoupon)
            {
                issueEffects.Add(e);
                continue;
            }

            decimal matchedEligible = Enumerable.Range(0, n).Where(i => eligibleLine[i] && c.Conditions.MatchesItem(lines[i].ItemCode, lines[i].Category)).Sum(i => linePaid[i]) * eligibleRatio;
            int bonus = c.RewardType == RewardType.BonusPoints
                ? c.Reward.BonusPoints ?? 0
                : rule is null ? 0 : PointMath.MultiplierBonus(matchedEligible, rule.SpendPerPoint, c.Reward.Multiplier ?? 1m);
            if (ctx.Allowances.TryGetValue(c.Id, out CampaignAllowance? al) && al.BudgetRemaining is decimal budget && ctx.Rules.PointValue > 0)
            {
                bonus = Math.Min(bonus, PointMath.Floor(budget / ctx.Rules.PointValue));
            }

            if (bonus <= 0)
            {
                continue;
            }

            e.BonusPoints = bonus;
            e.Value = bonus * ctx.Rules.PointValue;
            pointEffects.Add(e);
        }

        List<Effect> selectedPoints = SelectBest(pointEffects);
        foreach (Effect e in pointEffects.Where(e => e.CouponCode is not null && !selectedPoints.Contains(e)))
        {
            rejected.Add(new RejectedCouponDTO { Code = e.CouponCode!, Reason = "coupon_not_combinable" });
        }

        int bonusTotal = selectedPoints.Sum(e => e.BonusPoints);
        int earned = rule is null ? 0 : PointMath.TotalEarn(basePoints, bonusTotal, rule.MaxPointsPerTransaction);

        // Step 9: ประกอบผลลัพธ์
        var quote = new QuoteDTO
        {
            MemberNo = req.MemberNo,
            Subtotal = PointMath.Money(lines.Sum(l => l.UnitPrice * l.Qty)),
            LineDiscounts = PointMath.Money(lines.Sum(l => l.LineDiscount)),
            PromotionDiscount = chosen.Effects.Where(e => e.CouponCode is null).Sum(e => e.Amount),
            CouponDiscount = chosen.Effects.Where(e => e.CouponCode is not null).Sum(e => e.Amount),
            BillDiscount = shopDiscount,
            PointsRedeemed = points,
            PointsTender = tender,
            NetPayable = PointMath.Money(netBeforePoints - tender),
            Eligible = PointMath.Money(eligible),
            BasePoints = basePoints,
            PointsToEarn = earned,
            PointsAdjustedReason = pointsReason,
            EarnRuleCode = rule?.Code,
            RejectedCoupons = rejected,
        };

        foreach (Effect e in chosen.Effects.Concat(selectedPoints).Concat(issueEffects))
        {
            quote.Benefits.Add(new BenefitDTO
            {
                CampaignId = e.Campaign.Id,
                CampaignCode = e.Campaign.Code,
                Kind = e.Campaign.RewardType,
                Amount = PointMath.Money(e.Amount),
                BonusPoints = e.BonusPoints,
                LineNos = [.. Enumerable.Range(0, n).Where(i => e.PerLine.Length > i && e.PerLine[i] > 0).Select(i => lines[i].LineNo)],
                CouponCode = e.CouponCode,
                IssueCouponBatchId = e.Campaign.RewardType == RewardType.IssueCoupon ? e.Campaign.Reward.CouponBatchId : null,
            });
        }

        for (int i = 0; i < n; i++)
        {
            quote.Lines.Add(new QuoteLineResultDTO
            {
                LineNo = lines[i].LineNo,
                ItemCode = lines[i].ItemCode,
                Category = lines[i].Category,
                Qty = lines[i].Qty,
                LineNet = lineNet[i],
                PromotionDiscount = chosen.LineLevel[i],
                AllocatedBillDiscount = PointMath.Money(chosen.BillLevel[i] + shopAlloc[i]),
                LinePaid = linePaid[i],
                Eligible = eligibleLine[i],
            });
        }

        return quote;
    }

    /// <summary>ชั้นที่แคมเปญคำนวณจริง (ซื้อ X แถม Y = รายการ, ซื้อครบยอด = บิล)</summary>
    /// <param name="c">แคมเปญ</param>
    /// <returns>ชั้น</returns>
    public static PromotionLevel EffectiveLevel(CampaignDTO c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.RewardType switch
        {
            RewardType.BuyXGetY => PromotionLevel.Line,
            RewardType.SpendThreshold => PromotionLevel.Bill,
            _ => c.Level,
        };
    }

    /// <summary>แคมเปญใช้กับตะกร้า/สมาชิกนี้ได้หรือไม่ (ยังไม่ตรวจยอดขั้นต่ำ)</summary>
    /// <param name="c">แคมเปญ</param>
    /// <param name="ctx">ข้อมูลตะกร้า</param>
    /// <returns>true ถ้าใช้ได้</returns>
    public static bool IsApplicable(CampaignDTO c, BasketContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(ctx);
        DateTimeOffset at = ctx.At;
        if (at < c.StartAt || at >= c.EndAt)
        {
            return false;
        }

        if (ctx.Allowances.TryGetValue(c.Id, out CampaignAllowance? a) && (!a.Allowed || a.BudgetRemaining <= 0))
        {
            return false;
        }

        CampaignConditionsDTO cond = c.Conditions;
        string? branch = ctx.Request.BranchCode;
        if (cond.Branches.Count > 0 && (branch is null || !cond.Branches.Contains(branch, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (cond.Channels.Count > 0 && !cond.Channels.Contains(ctx.Request.Channel))
        {
            return false;
        }

        DateTime local = TimeZoneInfo.ConvertTime(at, ctx.Zone).DateTime;
        if (cond.DaysOfWeek.Count > 0 && !cond.DaysOfWeek.Contains(local.DayOfWeek))
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(local);
        if ((cond.TimeFrom is TimeOnly from && time < from) || (cond.TimeTo is TimeOnly to && time > to))
        {
            return false;
        }

        if (cond.BirthdayMonthOnly && (ctx.Member.BirthMonthDay is not { Length: >= 2 } md || int.Parse(md[..2], System.Globalization.CultureInfo.InvariantCulture) != local.Month))
        {
            return false;
        }

        // กลุ่มควบคุมของแคมเปญไม่ได้รับสิทธิ์ (สูตรข้อ 4)
        if (MemberAnalytics.Engine.HoldoutAssigner.IsControl(c.Code, ctx.Member.MemberNo, c.HoldoutPct))
        {
            return false;
        }

        CampaignTargetsDTO t = c.Targets;
        if (!t.AllMembers)
        {
            bool tierHit = ctx.Member.TierCode is not null && t.TierCodes.Contains(ctx.Member.TierCode, StringComparer.OrdinalIgnoreCase);
            bool segHit = t.SegmentIds.Any(ctx.SegmentIds.Contains);
            if (!tierHit && !segHit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>สินค้าได้คะแนนหรือไม่ (non-earning, หมวดของกฎ, สินค้าที่ยกเว้น)</summary>
    private static bool IsEarningItem(QuoteLineRequest line, EarnRuleDTO rule, MembershipRulesDTO rules) =>
        !rules.NonEarningItemCodes.Contains(line.ItemCode, StringComparer.OrdinalIgnoreCase)
        && !rule.ExcludeItemCodes.Contains(line.ItemCode, StringComparer.OrdinalIgnoreCase)
        && (rule.IncludeCategories.Count == 0 || (line.Category is not null && rule.IncludeCategories.Contains(line.Category, StringComparer.OrdinalIgnoreCase)));

    /// <summary>ผลของการคำนวณส่วนลดทั้งสองชั้น</summary>
    private sealed class DiscountResult
    {
        public List<Effect> Effects { get; } = [];
        public decimal[] LineLevel { get; set; } = [];
        public decimal[] BillLevel { get; set; } = [];
        public decimal[] LineAfter { get; set; } = [];
        public List<RejectedCouponDTO> Rejected { get; } = [];
        public decimal Total => Effects.Sum(e => e.Amount);
    }

    /// <summary>คำนวณส่วนลดชั้นรายการแล้วชั้นบิล (โปรอัตโนมัติก่อน แล้วคูปอง)</summary>
    private static DiscountResult ComputeDiscounts(BasketContext ctx, List<QuoteLineRequest> lines, decimal[] lineNet, List<CampaignDTO> auto,
        List<CouponCandidate> coupons, PromotionLevel? suppressAutoLevelFor)
    {
        int n = lines.Count;
        var result = new DiscountResult { LineLevel = new decimal[n], BillLevel = new decimal[n] };
        decimal[] current = [.. lineNet];

        foreach (PromotionLevel level in new[] { PromotionLevel.Line, PromotionLevel.Bill })
        {
            // Step 1: โปรอัตโนมัติของชั้นนี้ เลือกชุดที่ดีที่สุด
            if (suppressAutoLevelFor != level)
            {
                List<Effect> candidates = [.. auto.Where(c => c.IsDiscount && EffectiveLevel(c) == level)
                    .Select(c => Evaluate(c, null, lines, current, ctx)).OfType<Effect>()];
                foreach (Effect e in SelectBest(candidates))
                {
                    Apply(e, current, level == PromotionLevel.Line ? result.LineLevel : result.BillLevel);
                    if (e.Amount > 0)
                    {
                        result.Effects.Add(e);
                    }
                }
            }

            // Step 2: คูปองของชั้นนี้ คิดจากยอดหลังโปรอัตโนมัติ
            foreach (CouponCandidate cc in coupons.Where(c => EffectiveLevel(c.Campaign) == level))
            {
                Effect? e = Evaluate(cc.Campaign, cc.Code, lines, current, ctx);
                if (e is null || e.Value <= 0)
                {
                    result.Rejected.Add(new RejectedCouponDTO { Code = cc.Code, Reason = "coupon_min_spend_not_met" });
                    continue;
                }

                Apply(e, current, level == PromotionLevel.Line ? result.LineLevel : result.BillLevel);
                result.Effects.Add(e);
            }
        }

        result.LineAfter = current;
        return result;
    }

    /// <summary>หักส่วนลดของแคมเปญจากยอดบรรทัด (ไม่ให้ติดลบ — ปรับยอดของ effect ตามที่หักได้จริง)</summary>
    private static void Apply(Effect e, decimal[] current, decimal[] bucket)
    {
        for (int i = 0; i < current.Length; i++)
        {
            decimal take = Math.Min(e.PerLine[i], current[i]);
            e.PerLine[i] = take;
            current[i] -= take;
            bucket[i] += take;
        }

        e.Value = e.Amount;
    }

    /// <summary>คำนวณส่วนลดของแคมเปญบนยอดปัจจุบัน (null = ไม่เข้าเงื่อนไข)</summary>
    private static Effect? Evaluate(CampaignDTO c, string? couponCode, List<QuoteLineRequest> lines, decimal[] baseAmounts, BasketContext ctx)
    {
        int n = lines.Count;
        bool[] match = [.. Enumerable.Range(0, n).Select(i => baseAmounts[i] > 0 && c.Conditions.MatchesItem(lines[i].ItemCode, lines[i].Category))];
        decimal matchedBase = Enumerable.Range(0, n).Where(i => match[i]).Sum(i => baseAmounts[i]);
        if (matchedBase <= 0 || matchedBase < c.Conditions.MinSpend)
        {
            return null;
        }

        var perLine = new decimal[n];
        CampaignRewardDTO r = c.Reward;
        PromotionLevel level = EffectiveLevel(c);
        decimal[] weights = [.. Enumerable.Range(0, n).Select(i => match[i] ? baseAmounts[i] : 0m)];
        switch (c.RewardType)
        {
            case RewardType.PercentDiscount when level == PromotionLevel.Line:
                for (int i = 0; i < n; i++)
                {
                    perLine[i] = match[i] ? PointMath.Money(baseAmounts[i] * (r.Percent ?? 0) / 100m) : 0;
                }

                if (r.Cap is decimal lineCap && perLine.Sum() > lineCap)
                {
                    perLine = PointMath.Allocate(lineCap, perLine);
                }

                break;
            case RewardType.PercentDiscount:
                decimal pct = PointMath.Money(matchedBase * (r.Percent ?? 0) / 100m);
                perLine = PointMath.Allocate(r.Cap is decimal cap ? Math.Min(pct, cap) : pct, weights);
                break;
            case RewardType.AmountDiscount when level == PromotionLevel.Line:
                for (int i = 0; i < n; i++)
                {
                    perLine[i] = match[i] ? Math.Min(PointMath.Money((r.Amount ?? 0) * lines[i].Qty), baseAmounts[i]) : 0;
                }

                break;
            case RewardType.AmountDiscount:
                perLine = PointMath.Allocate(Math.Min(r.Amount ?? 0, matchedBase), weights);
                break;
            case RewardType.SpendThreshold:
                if (matchedBase >= (r.Threshold ?? 0))
                {
                    perLine = PointMath.Allocate(Math.Min(r.Amount ?? 0, matchedBase), weights);
                }

                break;
            case RewardType.BuyXGetY:
                perLine = BuyXGetY(lines, baseAmounts, match, r.BuyQty ?? 0, r.GetQty ?? 0);
                break;
            default:
                return null;
        }

        // งบคงเหลือ: ลดส่วนลดไม่ให้เกินงบ
        if (ctx.Allowances.TryGetValue(c.Id, out CampaignAllowance? a) && a.BudgetRemaining is decimal budget && perLine.Sum() > budget)
        {
            perLine = PointMath.Allocate(Math.Max(budget, 0), perLine);
        }

        var e = new Effect { Campaign = c, PerLine = perLine, CouponCode = couponCode };
        e.Value = e.Amount;
        return e.Value > 0 ? e : null;
    }

    /// <summary>ซื้อ X แถม Y: <c>floor(qty / (X + Y)) × Y</c> ชิ้นที่ราคาต่ำสุดในกลุ่มสินค้าที่เข้าเงื่อนไข</summary>
    private static decimal[] BuyXGetY(List<QuoteLineRequest> lines, decimal[] baseAmounts, bool[] match, int x, int y)
    {
        int n = lines.Count;
        var perLine = new decimal[n];
        if (x <= 0 || y <= 0)
        {
            return perLine;
        }

        var units = new List<(int Line, decimal Price)>();
        for (int i = 0; i < n; i++)
        {
            int whole = (int)Math.Floor(lines[i].Qty);
            if (!match[i] || whole <= 0)
            {
                continue;
            }

            decimal unit = baseAmounts[i] / lines[i].Qty;
            for (int k = 0; k < whole; k++)
            {
                units.Add((i, unit));
            }
        }

        int free = units.Count / (x + y) * y;
        foreach ((int line, decimal price) in units.OrderBy(u => u.Price).ThenBy(u => u.Line).Take(free))
        {
            perLine[line] += price;
        }

        for (int i = 0; i < n; i++)
        {
            perLine[i] = Math.Min(PointMath.Money(perLine[i]), baseAmounts[i]);
        }

        return perLine;
    }

    /// <summary>
    /// เลือกชุดแคมเปญที่ดีที่สุด (spec 2.7 ข้อ 4): Exclusive ที่ดีที่สุด 1 อัน เทียบกับ Stackable ทั้งหมด + ตัวดีที่สุดของแต่ละกลุ่ม
    /// เท่ากัน = ใช้ priority แล้ว campaignId ที่น้อยกว่า
    /// </summary>
    private static List<Effect> SelectBest(List<Effect> effects)
    {
        if (effects.Count == 0)
        {
            return [];
        }

        static IOrderedEnumerable<Effect> Rank(IEnumerable<Effect> es) =>
            es.OrderByDescending(e => e.Value).ThenBy(e => e.Campaign.Priority).ThenBy(e => e.Campaign.Id);

        Effect? bestExclusive = Rank(effects.Where(e => e.Campaign.StackMode == StackMode.Exclusive)).FirstOrDefault();
        var stack = effects.Where(e => e.Campaign.StackMode == StackMode.Stackable).ToList();
        stack.AddRange(effects.Where(e => e.Campaign.StackMode == StackMode.Group)
            .GroupBy(e => e.Campaign.ExclusionGroup ?? $"#{e.Campaign.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(g => Rank(g).First()));

        decimal stackValue = stack.Sum(e => e.Value);
        List<Effect> stackOrdered = [.. stack.OrderBy(e => e.Campaign.Priority).ThenBy(e => e.Campaign.Id)];
        if (bestExclusive is null)
        {
            return stackOrdered;
        }

        if (stack.Count == 0 || bestExclusive.Value > stackValue)
        {
            return [bestExclusive];
        }

        if (stackValue > bestExclusive.Value)
        {
            return stackOrdered;
        }

        // เท่ากัน: ฝั่งที่มี priority / id น้อยกว่า
        Effect stackFirst = stackOrdered[0];
        bool exclusiveWins = bestExclusive.Campaign.Priority < stackFirst.Campaign.Priority
            || (bestExclusive.Campaign.Priority == stackFirst.Campaign.Priority && bestExclusive.Campaign.Id < stackFirst.Campaign.Id);
        return exclusiveWins ? [bestExclusive] : stackOrdered;
    }
}
