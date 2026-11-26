using System.Security.Cryptography;
using System.Text;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>
/// POS / ระบบต้นทาง (Claude.MD §22, spec 2.5–2.7): lookup → quote → reserve → commit | release, บันทึกขาย offline, ยกเลิก, คืนสินค้า
/// ทุกการเขียนผ่าน unit of work เดียว (คะแนน คูปอง ตัวนับแคมเปญ รายการขาย outbox) และลองใหม่เมื่อชนกัน
/// </summary>
public sealed partial class CheckoutService
{
    private readonly IMemberDAO _members;
    private readonly ITierDAO _tiers;
    private readonly IPointDAO _points;
    private readonly ICampaignDAO _campaigns;
    private readonly ICouponDAO _coupons;
    private readonly ISegmentDAO _segments;
    private readonly ICheckoutDAO _checkout;
    private readonly IMembershipUnitOfWork _uow;
    private readonly PointLedgerService _ledger;
    private readonly PointService _pointService;
    private readonly TierService _tierService;
    private readonly CouponService _couponService;
    private readonly MemberService _memberService;
    private readonly MembershipRulesProvider _rules;
    private readonly TimeProvider _clock;
    private readonly ILogger<CheckoutService> _log;

    /// <summary>สร้าง</summary>
    public CheckoutService(IMemberDAO members, ITierDAO tiers, IPointDAO points, ICampaignDAO campaigns, ICouponDAO coupons, ISegmentDAO segments,
        ICheckoutDAO checkout, IMembershipUnitOfWork uow, PointLedgerService ledger, PointService pointService, TierService tierService,
        CouponService couponService, MemberService memberService, MembershipRulesProvider rules, TimeProvider clock, ILogger<CheckoutService> log)
    {
        _members = members;
        _tiers = tiers;
        _points = points;
        _campaigns = campaigns;
        _coupons = coupons;
        _segments = segments;
        _checkout = checkout;
        _uow = uow;
        _ledger = ledger;
        _pointService = pointService;
        _tierService = tierService;
        _couponService = couponService;
        _memberService = memberService;
        _rules = rules;
        _clock = clock;
        _log = log;
    }

    // ------------------------------------------------------------------ lookup / quote

    /// <summary>ค้นสมาชิกสำหรับแคชเชียร์ (เบอร์ / เลขสมาชิก / รหัสคูปอง)</summary>
    public async Task<PosMemberDTO> LookupAsync(MembershipCaller caller, string? phone, string? memberNo, string? couponCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PosLookup);
        MemberEntity? m = null;
        if (!string.IsNullOrWhiteSpace(memberNo))
        {
            m = await _members.GetByNoAsync(memberNo.Trim().ToUpperInvariant(), ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(phone))
        {
            m = await _memberService.FindByPhoneAsync(MemberRules.NormalizePhone(phone), ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(couponCode))
        {
            CouponDTO? c = await _coupons.GetByCodeAsync(CouponService.Normalize(couponCode), ct).ConfigureAwait(false);
            if (c?.MemberId is long owner)
            {
                m = await _members.GetAsync(owner, ct).ConfigureAwait(false);
            }
        }
        else
        {
            ValidationException.ThrowIf(true, "phone", "Specify phone, memberNo or couponCode.");
        }

        if (m is null || m.Status == MemberStatus.Closed)
        {
            throw new NotFoundException("Member not found.");
        }

        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        PointAccountDTO account = await _points.GetAccountAsync(m.Id, ct).ConfigureAwait(false);
        IReadOnlyList<CouponDTO> coupons = await _coupons.ListByMemberAsync(m.Id, CouponStatus.Available, ct).ConfigureAwait(false);
        Dictionary<long, CampaignDTO> campaigns = (await _campaigns.ListByIdsAsync([.. coupons.Select(c => c.CampaignId).Distinct()], ct).ConfigureAwait(false)).ToDictionary(c => c.Id);
        return new PosMemberDTO
        {
            MemberNo = m.MemberNo,
            DisplayName = MemberRules.ShortName(m.FirstName, m.LastName),
            Status = m.Status,
            TierCode = m.TierCode,
            PointsAvailable = account.Available,
            PointValue = rules.PointValue,
            MaxPointsPayPercent = rules.MaxPointsPayPercent,
            Coupons = [.. coupons.Where(c => c.ExpiresAt > now && campaigns.TryGetValue(c.CampaignId, out CampaignDTO? cp) && cp.Status == CampaignStatus.Active)
                .OrderBy(c => c.ExpiresAt)
                .Select(c => new PosCouponDTO { Code = c.Code, Name = campaigns[c.CampaignId].Name, ExpiresAt = c.ExpiresAt })],
        };
    }

    /// <summary>คำนวณตะกร้า (ไม่จองอะไร)</summary>
    public async Task<QuoteDTO> QuoteAsync(MembershipCaller caller, QuoteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PosCheckout);
        MemberEntity m = await LoadActiveMemberAsync(request.MemberNo, ct).ConfigureAwait(false);
        PointAccountDTO account = await _points.GetAccountAsync(m.Id, ct).ConfigureAwait(false);
        return (await CalculateAsync(request, m, account.Available, null, ct).ConfigureAwait(false)).Quote;
    }

    /// <summary>ทดสอบแคมเปญ (รวม Draft) กับตะกร้าตัวอย่าง — ไม่บันทึก</summary>
    public async Task<QuoteDTO> SimulateAsync(MembershipCaller caller, QuoteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PromotionRead);
        MemberEntity m = await _members.GetByNoAsync(request.MemberNo.Trim().ToUpperInvariant(), ct).ConfigureAwait(false) ?? throw new NotFoundException("Member not found.");
        PointAccountDTO account = await _points.GetAccountAsync(m.Id, ct).ConfigureAwait(false);
        IReadOnlyList<CampaignDTO>? campaigns = request.CampaignIds is { Count: > 0 } ids
            ? await _campaigns.ListByIdsAsync([.. ids.Distinct()], ct).ConfigureAwait(false)
            : null;
        return (await CalculateAsync(request, m, account.Available, campaigns, ct).ConfigureAwait(false)).Quote;
    }

    // ------------------------------------------------------------------ reserve / commit / release

    /// <summary>จองคะแนน คูปอง และสิทธิ์แคมเปญตามผลคำนวณ — 409 quote_changed ถ้าผลต่างจาก quote ที่แสดงลูกค้า</summary>
    public Task<ReservationDTO> ReserveAsync(MembershipCaller caller, QuoteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PosCheckout);
        return MembershipEvents.RetryOnConflictAsync(async () =>
        {
            DateTimeOffset now = _clock.GetUtcNow();
            MemberEntity m = await LoadActiveMemberAsync(request.MemberNo, ct).ConfigureAwait(false);
            PointPosting posting = await _ledger.OpenAsync(m.Id, now, ct).ConfigureAwait(false);
            Calculation calc = await CalculateAsync(request, m, posting.Account.Available, null, ct).ConfigureAwait(false);
            QuoteDTO quote = calc.Quote;
            if (!string.IsNullOrEmpty(request.QuoteId) && !string.Equals(request.QuoteId, quote.QuoteId, StringComparison.Ordinal))
            {
                throw new MembershipException(409, "quote_changed", "The quote changed. Show the new quote to the customer.") { Payload = quote };
            }

            var cs = new MembershipChangeSet();
            string id = MembershipEvents.NewId("rsv");

            // Step 1: คะแนน
            PointLedgerService.Reserve(posting, quote.PointsRedeemed);
            PointLedgerService.Finish(posting, cs);

            // Step 2: คูปอง
            List<string> couponCodes = [.. quote.Benefits.Where(b => b.CouponCode is not null).Select(b => b.CouponCode!).Distinct(StringComparer.OrdinalIgnoreCase)];
            foreach (string code in couponCodes)
            {
                CouponDTO c = calc.Coupons[code];
                CouponDTO next = c.Clone();
                next.Status = CouponStatus.Reserved;
                next.ReservationId = id;
                cs.Coupons.Add(new CouponChange(next, c.Version));
                cs.CouponEvents.Add(new CouponEventDTO { Code = c.Code, FromStatus = c.Status, ToStatus = CouponStatus.Reserved, Reference = id, Actor = caller.Actor, At = now });
            }

            // Step 3: ตัวนับสิทธิ์ / งบ (ตรวจไม่เกินจำกัดใน transaction)
            List<CampaignCounterDelta> deltas = CounterDeltas(quote, calc.CampaignsById, m.Id, calc.LocalDate, calc.Rules);
            cs.Counters.AddRange(deltas);

            cs.NewReservation = new ReservationEntity
            {
                Id = id,
                SourceSystem = caller.SourceSystem,
                MemberId = m.Id,
                Status = ReservationStatus.Reserved,
                RequestJson = MembershipJson.Write(request),
                QuoteJson = MembershipJson.Write(quote),
                PointsReserved = quote.PointsRedeemed,
                CouponCodes = couponCodes.Count == 0 ? null : string.Join(',', couponCodes),
                CountersJson = MembershipJson.Write(deltas),
                ExpiresAt = now.AddMinutes(calc.Rules.ReserveTtlMinutes),
                CreatedAt = now,
                Version = 1,
            };

            try
            {
                await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
            }
            catch (MembershipLimitException ex)
            {
                throw MembershipException.Conflict(ex.Code, "Campaign limit or budget reached. Request a new quote.");
            }

            quote.ExpiresAt = cs.NewReservation.ExpiresAt;
            return new ReservationDTO { ReservationId = id, ExpiresAt = cs.NewReservation.ExpiresAt, Quote = quote };
        });
    }

    /// <summary>ยืนยันการขาย: ตัดคะแนน FEFO, ให้คะแนน, คูปอง Redeemed, ออกคูปองรางวัล, ขึ้น Tier — 410 เมื่อการจองหมดอายุ</summary>
    public Task<CheckoutResultDTO> CommitAsync(MembershipCaller caller, string reservationId, CommitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PosCheckout);
        return MembershipEvents.RetryOnConflictAsync(async () =>
        {
            DateTimeOffset now = _clock.GetUtcNow();
            ReservationEntity r = await LoadReservationAsync(caller, reservationId, ct).ConfigureAwait(false);
            if (r.Status == ReservationStatus.Committed)
            {
                if (!string.Equals(r.TransactionId, request.TransactionId, StringComparison.Ordinal))
                {
                    throw MembershipException.Conflict("duplicate_transaction", "The reservation was committed with another transaction.");
                }

                return await ExistingCheckoutResultAsync(r, ct).ConfigureAwait(false);
            }

            if (r.Status == ReservationStatus.Released || r.ExpiresAt <= now)
            {
                throw MembershipException.Gone("reservation_expired", "The reservation has expired. Request a new quote.");
            }

            if (await _checkout.GetTransactionAsync(r.SourceSystem, request.TransactionId, ct).ConfigureAwait(false) is not null)
            {
                throw MembershipException.Conflict("duplicate_transaction", "The transaction ID was already recorded.");
            }

            MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
            TimeZoneInfo zone = PointMath.Zone(rules.TimeZoneId);
            QuoteDTO quote = MembershipJson.Read<QuoteDTO>(r.QuoteJson);
            QuoteRequest qr = MembershipJson.Read<QuoteRequest>(r.RequestJson);
            MemberEntity m = await _members.GetAsync(r.MemberId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Member not found.");
            var cs = new MembershipChangeSet();
            var ledger = new LedgerContext(LedgerType.Redeem, $"redeem:{r.SourceSystem}:{request.TransactionId}", caller.Actor)
            {
                SourceSystem = r.SourceSystem, TransactionId = request.TransactionId, Reference = r.Id, RequestId = caller.RequestId,
            };

            // Step 1: คะแนน — ปล่อยที่จอง แล้วตัดจริง (FEFO) และให้คะแนน
            PointPosting posting = await _ledger.OpenAsync(m.Id, now, ct).ConfigureAwait(false);
            PointLedgerService.Unreserve(posting, r.PointsReserved);
            List<ConsumedLotDTO> consumed = PointLedgerService.Debit(posting, cs, quote.PointsRedeemed, ledger, now, allowDebt: false);
            PointLedgerService.Credit(posting, cs, quote.PointsToEarn, ledger with { Type = LedgerType.Earn, Key = $"earn:{r.SourceSystem}:{request.TransactionId}" },
                now, PointMath.ExpiryFor(now, rules.ExpiryMonths, zone), countLifetime: true);
            PointLedgerService.Finish(posting, cs);

            // Step 2: คูปองที่จอง → Redeemed
            List<string> codes = SplitCodes(r.CouponCodes);
            foreach (CouponDTO c in await _coupons.GetByCodesAsync(codes, ct).ConfigureAwait(false))
            {
                if (c.Status != CouponStatus.Reserved || !string.Equals(c.ReservationId, r.Id, StringComparison.Ordinal))
                {
                    throw MembershipException.Conflict("coupon_unavailable", "A reserved coupon is no longer reserved for this checkout.");
                }

                CouponDTO next = c.Clone();
                next.Status = CouponStatus.Redeemed;
                next.RedeemedTransactionId = request.TransactionId;
                cs.Coupons.Add(new CouponChange(next, c.Version));
                cs.CouponEvents.Add(new CouponEventDTO { Code = c.Code, FromStatus = c.Status, ToStatus = CouponStatus.Redeemed, Reference = request.TransactionId, Actor = caller.Actor, At = now });
            }

            // Step 3: ตัวนับ — ย้ายจาก reserved เป็น used
            foreach (CampaignCounterDelta d in MembershipJson.Read<List<CampaignCounterDelta>>(r.CountersJson))
            {
                cs.Counters.Add(new CampaignCounterDelta
                {
                    CampaignId = d.CampaignId, Scope = d.Scope, UsesDelta = d.ReservedDelta, ReservedDelta = -d.ReservedDelta,
                    BudgetDelta = d.BudgetReservedDelta, BudgetReservedDelta = -d.BudgetReservedDelta,
                });
            }

            // Step 4: ออกคูปองรางวัล (IssueCoupon)
            var issued = new List<string>();
            foreach (long batchId in quote.Benefits.Where(b => b.IssueCouponBatchId is not null).Select(b => b.IssueCouponBatchId!.Value).Distinct())
            {
                if (await _coupons.GetBatchAsync(batchId, ct).ConfigureAwait(false) is CouponBatchDTO batch && batch.ExpiresAt > now)
                {
                    issued.Add((await _couponService.IssueToMemberAsync(batch, m.Id, caller.Actor, $"txn:{request.TransactionId}", cs, ct).ConfigureAwait(false)).Code);
                }
            }

            // Step 5: รายการขาย + สมาชิก + Tier
            SalesTransactionEntity txn = NewTransaction(r.SourceSystem, request.TransactionId, request.ReceiptNo, m, qr.BranchCode, qr.Channel, request.PaidAt ?? now, quote, offline: false, now);
            txn.ReservationId = r.Id;
            txn.CouponCodes = codes.Count == 0 ? null : string.Join(',', codes);
            txn.ConsumedLotsJson = MembershipJson.Write(consumed);
            cs.NewTransaction = txn;
            m.LastPurchaseAt = now;
            m.UpdatedAt = now;
            cs.MemberUpdates.Add(m);
            string? tier = await _tierService.ApplyUpgradeAsync(m, quote.Eligible, cs, caller.Actor, ct).ConfigureAwait(false);

            ReservationEntity closed = r.Clone();
            closed.Status = ReservationStatus.Committed;
            closed.TransactionId = request.TransactionId;
            closed.ClosedAt = now;
            cs.ReservationUpdate = closed;
            AddPointEvents(cs, m, request.TransactionId, quote.PointsToEarn, quote.PointsRedeemed, now);
            await _uow.CommitAsync(cs, ct).ConfigureAwait(false);

            return new CheckoutResultDTO
            {
                TransactionId = request.TransactionId,
                MemberNo = m.MemberNo,
                PointsEarned = quote.PointsToEarn,
                PointsRedeemed = quote.PointsRedeemed,
                Balance = PointLedgerService.ToDto(posting.Account, m.Id),
                CouponsRedeemed = codes,
                CouponsIssued = issued,
                TierChanged = tier,
            };
        });
    }

    /// <summary>ปล่อยการจอง (ส่งซ้ำได้)</summary>
    public async Task ReleaseAsync(MembershipCaller caller, string reservationId, ReleaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PosCheckout);
        await MembershipEvents.RetryOnConflictAsync(async () =>
        {
            ReservationEntity r = await LoadReservationAsync(caller, reservationId, ct).ConfigureAwait(false);
            if (r.Status == ReservationStatus.Committed)
            {
                throw MembershipException.InvalidTransition(r.Status, ReservationStatus.Released);
            }

            await ReleaseCoreAsync(r, request.Reason ?? "released", caller.Actor, ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>งานทุก 1 นาที: ปล่อยการจองที่หมดอายุ</summary>
    public async Task<int> ReleaseExpiredAsync(CancellationToken ct)
    {
        int n = 0;
        foreach (ReservationEntity r in await _checkout.ListExpiredReservationsAsync(_clock.GetUtcNow(), 200, ct).ConfigureAwait(false))
        {
            try
            {
                await ReleaseCoreAsync(r, "expired", "system", ct).ConfigureAwait(false);
                n++;
            }
            catch (MembershipConcurrencyException)
            {
                // ถูก commit / release พร้อมกัน — รอบหน้าค่อยดู
            }
        }

        return n;
    }

    private async Task ReleaseCoreAsync(ReservationEntity r, string reason, string actor, CancellationToken ct)
    {
        if (r.Status != ReservationStatus.Reserved)
        {
            return;
        }

        DateTimeOffset now = _clock.GetUtcNow();
        var cs = new MembershipChangeSet();
        PointPosting posting = await _ledger.OpenAsync(r.MemberId, now, ct).ConfigureAwait(false);
        PointLedgerService.Unreserve(posting, r.PointsReserved);
        PointLedgerService.Finish(posting, cs);
        foreach (CouponDTO c in await _coupons.GetByCodesAsync(SplitCodes(r.CouponCodes), ct).ConfigureAwait(false))
        {
            if (c.Status == CouponStatus.Reserved && string.Equals(c.ReservationId, r.Id, StringComparison.Ordinal))
            {
                CouponDTO next = c.Clone();
                next.Status = c.ExpiresAt <= now ? CouponStatus.Expired : CouponStatus.Available;
                next.ReservationId = null;
                cs.Coupons.Add(new CouponChange(next, c.Version));
                cs.CouponEvents.Add(new CouponEventDTO { Code = c.Code, FromStatus = c.Status, ToStatus = next.Status, Reference = r.Id, Actor = actor, At = now });
            }
        }

        foreach (CampaignCounterDelta d in MembershipJson.Read<List<CampaignCounterDelta>>(r.CountersJson))
        {
            cs.Counters.Add(new CampaignCounterDelta { CampaignId = d.CampaignId, Scope = d.Scope, ReservedDelta = -d.ReservedDelta, BudgetReservedDelta = -d.BudgetReservedDelta });
        }

        ReservationEntity closed = r.Clone();
        closed.Status = ReservationStatus.Released;
        closed.ClosedAt = now;
        closed.CloseReason = reason.Length > 500 ? reason[..500] : reason;
        cs.ReservationUpdate = closed;
        await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>ผลคำนวณพร้อมข้อมูลที่ใช้จอง</summary>
    private sealed record Calculation(QuoteDTO Quote, Dictionary<string, CouponDTO> Coupons, Dictionary<long, CampaignDTO> CampaignsById, string LocalDate, MembershipRulesDTO Rules);

    private async Task<Calculation> CalculateAsync(QuoteRequest req, MemberEntity m, int pointsAvailable, IReadOnlyList<CampaignDTO>? simulate, CancellationToken ct)
    {
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        TimeZoneInfo zone = PointMath.Zone(rules.TimeZoneId);
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset at = req.OccurredAt is DateTimeOffset o && o <= now.AddMinutes(5) ? o : now;
        ValidationException.ThrowIf(req.Lines.Select(l => l.LineNo).Distinct().Count() != req.Lines.Count, "lines", "Line numbers must be unique.");

        // Step 1: แคมเปญ
        List<CampaignDTO> campaigns = simulate is null
            ? [.. await _campaigns.ListByStatusAsync([CampaignStatus.Active], ct).ConfigureAwait(false)]
            : [.. simulate];
        var byId = campaigns.ToDictionary(c => c.Id);

        // Step 2: คูปอง — ตรวจเจ้าของ / หมดอายุ / สถานะ ก่อนส่งให้ engine
        var rejected = new List<RejectedCouponDTO>();
        var candidates = new List<CouponCandidate>();
        var couponMap = new Dictionary<string, CouponDTO>(StringComparer.OrdinalIgnoreCase);
        List<string> codes = [.. req.CouponCodes.Select(CouponService.Normalize).Where(c => c.Length > 0).Distinct()];
        if (codes.Count > 0)
        {
            Dictionary<string, CouponDTO> found = (await _coupons.GetByCodesAsync(codes, ct).ConfigureAwait(false)).ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
            List<long> missingCampaigns = [.. found.Values.Select(c => c.CampaignId).Where(id => !byId.ContainsKey(id)).Distinct()];
            if (missingCampaigns.Count > 0)
            {
                foreach (CampaignDTO c in await _campaigns.ListByIdsAsync(missingCampaigns, ct).ConfigureAwait(false))
                {
                    byId[c.Id] = c;
                }
            }

            foreach (string code in codes)
            {
                string? reason = !found.TryGetValue(code, out CouponDTO? c) ? "coupon_unavailable"
                    : c.MemberId is long owner && owner != m.Id ? "coupon_not_owned"
                    : c.ExpiresAt <= at || c.Status == CouponStatus.Expired ? "coupon_expired"
                    : c.Status is not (CouponStatus.Available or CouponStatus.Reversed) ? "coupon_unavailable"
                    : !byId.TryGetValue(c.CampaignId, out CampaignDTO? cp) || (simulate is null && cp.Status != CampaignStatus.Active) ? "coupon_not_applicable"
                    : null;
                if (reason is not null)
                {
                    rejected.Add(new RejectedCouponDTO { Code = code, Reason = reason });
                    continue;
                }

                couponMap[code] = c!;
                candidates.Add(new CouponCandidate { Code = c!.Code, Campaign = byId[c.CampaignId] });
            }
        }

        // Step 3: สิทธิ์คงเหลือจากตัวนับ
        string localDate = PointMath.LocalDate(at, zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        IReadOnlyCollection<CampaignDTO> relevant = [.. campaigns.Where(c => !c.RequiresCoupon).Concat(candidates.Select(c => c.Campaign)).DistinctBy(c => c.Id)];
        Dictionary<long, CampaignAllowance> allowances = await AllowancesAsync(relevant, m.Id, localDate, ct).ConfigureAwait(false);

        // Step 4: Tier, segment, กฎคะแนน
        TierDTO? tier = m.TierCode is null ? null
            : (await _tiers.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => string.Equals(t.Code, m.TierCode, StringComparison.OrdinalIgnoreCase));
        IReadOnlySet<long> segments = await _segments.ListSegmentIdsOfMemberAsync(m.Id, ct).ConfigureAwait(false);
        EarnRuleDTO? rule = await _pointService.ResolveRuleAsync(req.BranchCode, req.Channel, at, ct).ConfigureAwait(false);

        QuoteDTO quote = PromotionEngine.Calculate(new BasketContext
        {
            Request = req,
            Member = m,
            Tier = tier,
            SegmentIds = segments,
            Campaigns = [.. relevant.Where(c => !c.RequiresCoupon)],
            Allowances = allowances,
            Coupons = candidates,
            PreRejectedCoupons = rejected,
            EarnRule = rule,
            Rules = rules,
            PointsAvailable = pointsAvailable,
            At = at,
            Zone = zone,
        });
        quote.MemberNo = m.MemberNo;
        quote.QuoteId = QuoteIdOf(m.MemberNo, quote);
        quote.ExpiresAt = now.AddMinutes(rules.ReserveTtlMinutes);
        return new Calculation(quote, couponMap, byId, localDate, rules);
    }

    /// <summary>quoteId = hash ของผลคำนวณ (ไม่ต้องเก็บ quote — reserve คำนวณใหม่แล้วเทียบ)</summary>
    private static string QuoteIdOf(string memberNo, QuoteDTO q)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(memberNo + "|" + q.Fingerprint()));
        return "q_" + Convert.ToBase64String(hash, 0, 18).Replace('+', '-').Replace('/', '_');
    }

    private async Task<Dictionary<long, CampaignAllowance>> AllowancesAsync(IReadOnlyCollection<CampaignDTO> campaigns, long memberId, string localDate, CancellationToken ct)
    {
        var result = new Dictionary<long, CampaignAllowance>();
        if (campaigns.Count == 0)
        {
            return result;
        }

        string mScope = $"m:{memberId}";
        string dScope = $"d:{memberId}:{localDate}";
        IReadOnlyDictionary<(long CampaignId, string Scope), CampaignUsageDTO> counters =
            await _campaigns.GetCountersAsync([.. campaigns.Select(c => c.Id)], ["total", mScope, dScope], ct).ConfigureAwait(false);
        CampaignUsageDTO Get(long id, string scope) => counters.TryGetValue((id, scope), out CampaignUsageDTO? u) ? u : new CampaignUsageDTO();
        foreach (CampaignDTO c in campaigns)
        {
            CampaignUsageDTO total = Get(c.Id, "total");
            string? reason =
                (c.Limits.TotalUses is int tu && total.Uses + total.Reserved >= tu)
                || (c.Limits.PerMemberTotal is int pm && Get(c.Id, mScope) is var mu && mu.Uses + mu.Reserved >= pm)
                || (c.Limits.PerMemberPerDay is int pd && Get(c.Id, dScope) is var du && du.Uses + du.Reserved >= pd)
                    ? "limit_reached" : null;
            decimal? remaining = c.Limits.Budget is decimal b ? Math.Max(b - total.BudgetUsed - total.BudgetReserved, 0m) : null;
            if (reason is null && remaining is 0m)
            {
                reason = "budget_exhausted";
            }

            result[c.Id] = new CampaignAllowance { Allowed = reason is null, BudgetRemaining = remaining, Reason = reason };
        }

        return result;
    }

    /// <summary>ตัวนับที่ต้องจองต่อแคมเปญที่ได้สิทธิ์: total / ต่อสมาชิก / ต่อวัน — ต้นทุนงบ = ส่วนลด + คะแนนพิเศษ × มูลค่าคะแนน</summary>
    private static List<CampaignCounterDelta> CounterDeltas(QuoteDTO quote, Dictionary<long, CampaignDTO> campaigns, long memberId, string localDate, MembershipRulesDTO rules)
    {
        var list = new List<CampaignCounterDelta>();
        foreach (IGrouping<long, BenefitDTO> g in quote.Benefits.GroupBy(b => b.CampaignId))
        {
            if (!campaigns.TryGetValue(g.Key, out CampaignDTO? c))
            {
                continue;
            }

            decimal cost = PointMath.Money(g.Sum(b => b.Amount + b.BonusPoints * rules.PointValue));
            list.Add(new CampaignCounterDelta { CampaignId = c.Id, Scope = "total", ReservedDelta = 1, BudgetReservedDelta = cost, Limit = c.Limits.TotalUses, BudgetLimit = c.Limits.Budget });
            list.Add(new CampaignCounterDelta { CampaignId = c.Id, Scope = $"m:{memberId}", ReservedDelta = 1, Limit = c.Limits.PerMemberTotal });
            list.Add(new CampaignCounterDelta { CampaignId = c.Id, Scope = $"d:{memberId}:{localDate}", ReservedDelta = 1, Limit = c.Limits.PerMemberPerDay });
        }

        return list;
    }

    private async Task<MemberEntity> LoadActiveMemberAsync(string memberNo, CancellationToken ct)
    {
        MemberEntity m = await _members.GetByNoAsync(memberNo.Trim().ToUpperInvariant(), ct).ConfigureAwait(false) ?? throw new NotFoundException("Member not found.");
        if (m.Status != MemberStatus.Active)
        {
            throw MembershipException.Conflict("member_not_active", "The member is not active.");
        }

        return m;
    }

    private async Task<ReservationEntity> LoadReservationAsync(MembershipCaller caller, string id, CancellationToken ct)
    {
        ReservationEntity? r = await _checkout.GetReservationAsync(id, ct).ConfigureAwait(false);
        if (r is null || !string.Equals(r.SourceSystem, caller.SourceSystem, StringComparison.Ordinal))
        {
            throw new NotFoundException("Reservation not found.");
        }

        return r;
    }

    private async Task<CheckoutResultDTO> ExistingCheckoutResultAsync(ReservationEntity r, CancellationToken ct)
    {
        SalesTransactionEntity t = await _checkout.GetTransactionAsync(r.SourceSystem, r.TransactionId!, ct).ConfigureAwait(false) ?? throw new NotFoundException("Transaction not found.");
        PointAccountDTO a = await _points.GetAccountAsync(t.MemberId, ct).ConfigureAwait(false);
        List<BenefitDTO> benefits = MembershipJson.Read<List<BenefitDTO>>(t.BenefitsJson);
        IReadOnlyList<CouponDTO> member = await _coupons.ListByMemberAsync(t.MemberId, null, ct).ConfigureAwait(false);
        HashSet<long> batches = [.. benefits.Where(b => b.IssueCouponBatchId is not null).Select(b => b.IssueCouponBatchId!.Value)];
        return new CheckoutResultDTO
        {
            TransactionId = t.TransactionId,
            MemberNo = t.MemberNo,
            PointsEarned = t.PointsEarned,
            PointsRedeemed = t.PointsRedeemed,
            Balance = PointLedgerService.ToDto(a, t.MemberId),
            CouponsRedeemed = SplitCodes(t.CouponCodes),
            CouponsIssued = [.. member.Where(c => batches.Contains(c.BatchId) && c.CreatedAt >= t.CreatedAt.AddSeconds(-5) && c.CreatedAt <= t.CreatedAt.AddSeconds(5)).Select(c => c.Code)],
        };
    }

    private static SalesTransactionEntity NewTransaction(string source, string transactionId, string? receiptNo, MemberEntity m, string? branch, SalesChannel channel,
        DateTimeOffset occurredAt, QuoteDTO q, bool offline, DateTimeOffset now) => new()
        {
            SourceSystem = source,
            TransactionId = transactionId,
            ReceiptNo = receiptNo,
            MemberId = m.Id,
            MemberNo = m.MemberNo,
            BranchCode = branch,
            Channel = channel,
            OccurredAt = occurredAt,
            Subtotal = q.Subtotal,
            DiscountTotal = PointMath.Money(q.LineDiscounts + q.PromotionDiscount + q.CouponDiscount + q.BillDiscount),
            PointsRedeemed = q.PointsRedeemed,
            PointsTender = q.PointsTender,
            NetPaid = q.NetPayable,
            Eligible = q.Eligible,
            PointsEarned = q.PointsToEarn,
            Status = TransactionStatus.Completed,
            Offline = offline,
            LinesJson = MembershipJson.Write(q.Lines.Select(l => new TransactionLineDTO { LineNo = l.LineNo, ItemCode = l.ItemCode, Category = l.Category, Qty = l.Qty, LinePaid = l.LinePaid, Eligible = l.Eligible })),
            BenefitsJson = MembershipJson.Write(q.Benefits),
            CreatedAt = now,
            Version = 1,
        };

    private static void AddPointEvents(MembershipChangeSet cs, MemberEntity m, string transactionId, int earned, int redeemed, DateTimeOffset now)
    {
        if (earned > 0)
        {
            cs.Outbox.Add(MembershipEvents.Outbox("points.earned", new { memberId = m.Id, m.MemberNo, transactionId, points = earned }, now));
        }

        if (redeemed > 0)
        {
            cs.Outbox.Add(MembershipEvents.Outbox("points.redeemed", new { memberId = m.Id, m.MemberNo, transactionId, points = redeemed }, now));
        }
    }

    private static List<string> SplitCodes(string? csv) =>
        string.IsNullOrEmpty(csv) ? [] : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
