using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>รายการขาย: บันทึก offline, ยกเลิก, คืนสินค้า (spec 2.6), ค้นหา</summary>
public sealed partial class CheckoutService
{
    /// <summary>บันทึกการขายที่ไม่ได้ผ่าน checkout (ได้คะแนนพื้นฐานอย่างเดียว) — ส่งซ้ำ = คืนผลเดิม</summary>
    public Task<TransactionResultDTO> RecordTransactionAsync(MembershipCaller caller, SalesTransactionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PosTransaction);
        return MembershipEvents.RetryOnConflictAsync(async () =>
        {
            DateTimeOffset now = _clock.GetUtcNow();
            MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
            string source = caller.SourceSystem;
            if (await _checkout.GetTransactionAsync(source, request.TransactionId, ct).ConfigureAwait(false) is SalesTransactionEntity existing)
            {
                if (!string.Equals(existing.MemberNo, request.MemberNo.Trim(), StringComparison.OrdinalIgnoreCase)
                    || existing.Subtotal != PointMath.Money(request.Lines.Sum(l => l.UnitPrice * l.Qty)))
                {
                    throw MembershipException.Conflict("duplicate_transaction", "The transaction ID was already recorded with different data.");
                }

                TransactionResultDTO again = ToResult(existing, null);
                again.AlreadyRecorded = true;
                return again;
            }

            ValidationException.ThrowIf(request.PaidAt > now.AddMinutes(5), "paidAt", "paidAt cannot be in the future.");
            if (request.PaidAt < now.AddDays(-rules.PosLateSubmitDays))
            {
                throw MembershipException.Gone("late_submission", $"Transactions older than {rules.PosLateSubmitDays} days cannot be submitted.");
            }

            MemberEntity m = await _members.GetByNoAsync(request.MemberNo.Trim().ToUpperInvariant(), ct).ConfigureAwait(false) ?? throw new NotFoundException("Member not found.");
            if (m.Status is MemberStatus.Closed or MemberStatus.Suspended)
            {
                throw MembershipException.Conflict("member_not_active", "The member cannot earn points.");
            }

            // คิดคะแนนตามกฎ ณ เวลาที่ขาย (ไม่ใช้โปรโมชัน เพราะลูกค้าไม่ได้รับส่วนลดตอน offline)
            var qr = new QuoteRequest
            {
                MemberNo = m.MemberNo, BranchCode = request.BranchCode, Channel = request.Channel, OccurredAt = request.PaidAt,
                Lines = request.Lines, BillDiscount = request.BillDiscount,
            };
            TimeZoneInfo zone = PointMath.Zone(rules.TimeZoneId);
            TierDTO? tier = m.TierCode is null ? null
                : (await _tiers.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => string.Equals(t.Code, m.TierCode, StringComparison.OrdinalIgnoreCase));
            QuoteDTO q = PromotionEngine.Calculate(new BasketContext
            {
                Request = qr, Member = m, Tier = tier, Rules = rules, At = request.PaidAt, Zone = zone,
                EarnRule = await _pointService.ResolveRuleAsync(request.BranchCode, request.Channel, request.PaidAt, ct).ConfigureAwait(false),
            });

            var cs = new MembershipChangeSet();
            PointPosting posting = await _ledger.OpenAsync(m.Id, now, ct).ConfigureAwait(false);
            var ledger = new LedgerContext(LedgerType.Earn, $"earn:{source}:{request.TransactionId}", caller.Actor)
            {
                SourceSystem = source, TransactionId = request.TransactionId, RequestId = caller.RequestId, Reason = request.Offline ? "offline" : null,
            };
            PointLedgerService.Credit(posting, cs, q.PointsToEarn, ledger, now, PointMath.ExpiryFor(request.PaidAt, rules.ExpiryMonths, zone), countLifetime: true);
            PointLedgerService.Finish(posting, cs);

            SalesTransactionEntity txn = NewTransaction(source, request.TransactionId, request.ReceiptNo, m, request.BranchCode, request.Channel, request.PaidAt, q, request.Offline, now);
            txn.IsTest = request.IsTest;
            cs.NewTransaction = txn;
            if (m.LastPurchaseAt is null || m.LastPurchaseAt < request.PaidAt)
            {
                m.LastPurchaseAt = request.PaidAt;
            }

            m.UpdatedAt = now;
            cs.MemberUpdates.Add(m);
            await _tierService.ApplyUpgradeAsync(m, q.Eligible, cs, caller.Actor, ct).ConfigureAwait(false);
            AddPointEvents(cs, m, request.TransactionId, q.PointsToEarn, 0, now);
            await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
            return ToResult(txn, PointLedgerService.ToDto(posting.Account, m.Id));
        });
    }

    /// <summary>สถานะรายการขายของระบบต้นทาง</summary>
    public async Task<TransactionResultDTO> GetTransactionAsync(MembershipCaller caller, string transactionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PosTransaction);
        SalesTransactionEntity t = await _checkout.GetTransactionAsync(caller.SourceSystem, transactionId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Transaction not found.");
        PointAccountDTO a = await _points.GetAccountAsync(t.MemberId, ct).ConfigureAwait(false);
        return ToResult(t, PointLedgerService.ToDto(a, t.MemberId));
    }

    /// <summary>ยกเลิกบิล: ย้อนคะแนนที่ได้ทั้งหมด คืนคะแนนที่จ่าย และคืนคูปองตามกฎ</summary>
    public Task<TransactionResultDTO> CancelTransactionAsync(MembershipCaller caller, string transactionId, ReleaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PosTransaction);
        return MembershipEvents.RetryOnConflictAsync(async () =>
        {
            SalesTransactionEntity t = await _checkout.GetTransactionAsync(caller.SourceSystem, transactionId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Transaction not found.");
            if (t.Status == TransactionStatus.Cancelled)
            {
                return ToResult(t, null);
            }

            if (t.Status == TransactionStatus.Returned)
            {
                throw MembershipException.InvalidTransition(t.Status, TransactionStatus.Cancelled);
            }

            DateTimeOffset now = _clock.GetUtcNow();
            var cs = new MembershipChangeSet();
            List<TransactionLineDTO> lines = MembershipJson.Read<List<TransactionLineDTO>>(t.LinesJson);
            foreach (TransactionLineDTO l in lines)
            {
                l.ReturnedQty = l.Qty;
            }

            Reversal rv = await ReverseCumulativeAsync(caller, t, t.Eligible, t.NetPaid + t.PointsTender, full: true, $"cancel:{t.SourceSystem}:{t.TransactionId}", request.Reason, cs, ct).ConfigureAwait(false);
            SalesTransactionEntity next = t.Clone();
            next.Status = TransactionStatus.Cancelled;
            next.LinesJson = MembershipJson.Write(lines);
            next.ReturnedEligible = t.Eligible;
            next.ReturnedPaid = t.NetPaid + t.PointsTender;
            next.PointsReversed += rv.Reversed;
            next.PointsRestored += rv.Restored;
            cs.TransactionUpdate = next;
            cs.Audit.Add(MembershipEvents.Audit(caller, "transaction.cancel", $"transaction:{t.Id}", new { t.Status }, new { next.Status, request.Reason, rv.Reversed, rv.Restored }, now));
            await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
            return ToResult(next, rv.Balance);
        });
    }

    /// <summary>
    /// คืนสินค้า (spec 2.6): ย้อนคะแนนแบบสะสม reverseₖ = ⌊E × Rₖ / B⌋ − Σ ก่อนหน้า, คืนคะแนนที่จ่ายเป็น lot ใหม่ตามวันหมดอายุเดิม
    /// รายการขายยังไม่มา = เก็บไว้ประมวลผลภายหลัง (PendingOriginal)
    /// </summary>
    public async Task<ReturnResultDTO> ReturnAsync(MembershipCaller caller, string transactionId, ReturnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PosTransaction);
        ReturnResultDTO? result = await MembershipEvents.RetryOnConflictAsync(() => ReturnCoreAsync(caller, caller.SourceSystem, transactionId, request, ct)).ConfigureAwait(false);
        if (result is not null)
        {
            return result;
        }

        await _checkout.InsertPendingReturnAsync(new PendingReturnEntity
        {
            SourceSystem = caller.SourceSystem, TransactionId = transactionId, ReturnId = request.ReturnId, RequestJson = MembershipJson.Write(request), ReceivedAt = _clock.GetUtcNow(),
        }, ct).ConfigureAwait(false);
        return new ReturnResultDTO { ReturnId = request.ReturnId, TransactionId = transactionId, Status = "PendingOriginal" };
    }

    /// <summary>งานทุก 5 นาที: ประมวลผลการคืนที่มาก่อนรายการขาย — ค้างเกิน 7 วันแจ้งเตือน</summary>
    public async Task<int> ProcessPendingReturnsAsync(CancellationToken ct)
    {
        int done = 0;
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (PendingReturnEntity p in await _checkout.ListPendingReturnsAsync(200, ct).ConfigureAwait(false))
        {
            var caller = new MembershipCaller(Common.Core.Domain.UserContextDTO.System, p.SourceSystem, null);
            ReturnRequest req = MembershipJson.Read<ReturnRequest>(p.RequestJson);
            ReturnResultDTO? r;
            try
            {
                r = await MembershipEvents.RetryOnConflictAsync(() => ReturnCoreAsync(caller, p.SourceSystem, p.TransactionId, req, ct)).ConfigureAwait(false);
            }
            catch (CommonException ex)
            {
                // ข้อมูลการคืนใช้ไม่ได้ (เช่น บรรทัดไม่ตรง) — ปิดรายการและบันทึกเหตุผล
                LogPendingReturnFailed(_log, p.SourceSystem, p.TransactionId, p.ReturnId, ex.ErrorCode);
                r = new ReturnResultDTO();
            }

            p.Attempts++;
            if (r is not null)
            {
                p.ProcessedAt = now;
                done++;
            }
            else if (!p.Alerted && p.ReceivedAt < now.AddDays(-7))
            {
                LogPendingReturnStale(_log, p.SourceSystem, p.TransactionId, p.ReturnId);
                p.Alerted = true;
            }

            await _checkout.UpdatePendingReturnAsync(p, ct).ConfigureAwait(false);
        }

        return done;
    }

    // ------------------------------------------------------------------ back office

    /// <summary>ค้นหารายการขาย</summary>
    public async Task<PagedResultDTO<TransactionSummaryDTO>> SearchAsync(MembershipCaller caller, TransactionSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(criteria);
        caller.Require(MembershipPermissions.TxnRead);
        PagedResultDTO<SalesTransactionEntity> r = await _checkout.SearchTransactionsAsync(criteria, ct).ConfigureAwait(false);
        return new PagedResultDTO<TransactionSummaryDTO>([.. r.Items.Select(ToSummary)], r.Total, r.Page, r.PageSize);
    }

    /// <summary>รายละเอียดรายการขาย</summary>
    public async Task<TransactionDetailDTO> GetDetailAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.TxnRead);
        SalesTransactionEntity t = await _checkout.GetTransactionByIdAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Transaction not found.");
        PagedResultDTO<PointLedgerDTO> ledger = await _points.SearchLedgerAsync(new LedgerCriteria { MemberId = t.MemberId, TransactionId = t.TransactionId, PageSize = 100 }, ct).ConfigureAwait(false);
        return new TransactionDetailDTO
        {
            Summary = ToSummary(t),
            Subtotal = t.Subtotal,
            Eligible = t.Eligible,
            PointsTender = t.PointsTender,
            PointsReversed = t.PointsReversed,
            PointsRestored = t.PointsRestored,
            Lines = MembershipJson.Read<List<TransactionLineDTO>>(t.LinesJson),
            Benefits = MembershipJson.Read<List<BenefitDTO>>(t.BenefitsJson),
            Coupons = SplitCodes(t.CouponCodes),
            Returns = [.. await _checkout.ListReturnsAsync(t.SourceSystem, t.TransactionId, ct).ConfigureAwait(false)],
            Ledger = [.. ledger.Items.Where(l => l.SourceSystem is null || string.Equals(l.SourceSystem, t.SourceSystem, StringComparison.Ordinal))],
        };
    }

    /// <summary>ประวัติซื้อของสมาชิก (Portal)</summary>
    internal async Task<PagedResultDTO<TransactionSummaryDTO>> ListByMemberAsync(long memberId, int page, int pageSize, CancellationToken ct)
    {
        PagedResultDTO<SalesTransactionEntity> r = await _checkout.SearchTransactionsAsync(new TransactionSearchCriteria { MemberId = memberId, Page = page, PageSize = pageSize }, ct).ConfigureAwait(false);
        return new PagedResultDTO<TransactionSummaryDTO>([.. r.Items.Select(ToSummary)], r.Total, r.Page, r.PageSize);
    }

    internal static TransactionSummaryDTO ToSummary(SalesTransactionEntity t) => new()
    {
        Id = t.Id, SourceSystem = t.SourceSystem, TransactionId = t.TransactionId, ReceiptNo = t.ReceiptNo, MemberId = t.MemberId, MemberNo = t.MemberNo,
        BranchCode = t.BranchCode, Channel = t.Channel, OccurredAt = t.OccurredAt, NetPaid = t.NetPaid, DiscountTotal = t.DiscountTotal,
        PointsEarned = t.PointsEarned, PointsRedeemed = t.PointsRedeemed, Status = t.Status, Offline = t.Offline, IsTest = t.IsTest,
    };

    // ------------------------------------------------------------------ internals

    /// <summary>ผลการย้อนคะแนน</summary>
    private sealed record Reversal(int Reversed, int Restored, List<string> CouponsRestored, PointBalanceDTO Balance);

    /// <returns>null = ยังไม่พบรายการขาย</returns>
    private async Task<ReturnResultDTO?> ReturnCoreAsync(MembershipCaller caller, string source, string transactionId, ReturnRequest request, CancellationToken ct)
    {
        SalesTransactionEntity? t = await _checkout.GetTransactionAsync(source, transactionId, ct).ConfigureAwait(false);
        if (t is null)
        {
            return null;
        }

        // Step 1: ส่งซ้ำ = ผลเดิม
        IReadOnlyList<SalesReturnDTO> previous = await _checkout.ListReturnsAsync(source, transactionId, ct).ConfigureAwait(false);
        if (previous.FirstOrDefault(r => string.Equals(r.ReturnId, request.ReturnId, StringComparison.Ordinal)) is SalesReturnDTO done)
        {
            return new ReturnResultDTO { ReturnId = done.ReturnId, TransactionId = transactionId, PointsReversed = done.PointsReversed, PointsRestored = done.PointsRestored };
        }

        if (t.Status is TransactionStatus.Cancelled or TransactionStatus.Returned)
        {
            throw MembershipException.InvalidTransition(t.Status, TransactionStatus.Returned);
        }

        // Step 2: จำนวนที่คืนต่อบรรทัด (ว่าง = คืนส่วนที่เหลือทั้งบิล)
        List<TransactionLineDTO> lines = MembershipJson.Read<List<TransactionLineDTO>>(t.LinesJson);
        var byNo = lines.ToDictionary(l => l.LineNo);
        List<ReturnLineRequest> req = request.Lines.Count == 0
            ? [.. lines.Where(l => l.Qty > l.ReturnedQty).Select(l => new ReturnLineRequest { LineNo = l.LineNo, Qty = l.Qty - l.ReturnedQty })]
            : request.Lines;
        decimal paid = 0, eligibleGross = 0;
        foreach (ReturnLineRequest rl in req)
        {
            ValidationException.ThrowIf(!byNo.ContainsKey(rl.LineNo), "lines", $"Line {rl.LineNo} is not in the transaction.");
            ValidationException.ThrowIf(rl.Qty > byNo[rl.LineNo].Qty - byNo[rl.LineNo].ReturnedQty, "lines", $"Line {rl.LineNo}: return quantity exceeds the remaining quantity.");
            TransactionLineDTO line = byNo[rl.LineNo];
            decimal value = line.Qty <= 0 ? 0 : PointMath.Money(line.LinePaid * rl.Qty / line.Qty);
            paid += value;
            if (line.Eligible)
            {
                eligibleGross += value;
            }

            line.ReturnedQty += rl.Qty;
        }

        ValidationException.ThrowIf(req.Count == 0, "lines", "Nothing left to return.");
        bool full = lines.All(l => l.ReturnedQty >= l.Qty);

        // Step 3: แปลงยอด eligible ที่คืนเป็นฐานเดียวกับ B (หลังหักส่วนที่จ่ายด้วยคะแนน)
        decimal totalEligibleGross = lines.Where(l => l.Eligible).Sum(l => l.LinePaid);
        decimal returnedEligible = totalEligibleGross <= 0 ? 0 : PointMath.Money(eligibleGross * t.Eligible / totalEligibleGross);
        decimal cumEligible = full ? t.Eligible : Math.Min(t.ReturnedEligible + returnedEligible, t.Eligible);
        decimal basePaid = t.NetPaid + t.PointsTender;
        decimal cumPaid = full ? basePaid : Math.Min(t.ReturnedPaid + paid, basePaid);

        DateTimeOffset now = _clock.GetUtcNow();
        var cs = new MembershipChangeSet();
        Reversal rv = await ReverseCumulativeAsync(caller, t, cumEligible, cumPaid, full, $"return:{source}:{transactionId}:{request.ReturnId}", request.Reason, cs, ct).ConfigureAwait(false);

        SalesTransactionEntity next = t.Clone();
        next.LinesJson = MembershipJson.Write(lines);
        next.ReturnedEligible = cumEligible;
        next.ReturnedPaid = cumPaid;
        next.PointsReversed += rv.Reversed;
        next.PointsRestored += rv.Restored;
        next.Status = full ? TransactionStatus.Returned : TransactionStatus.PartiallyReturned;
        cs.TransactionUpdate = next;
        cs.Returns.Add(new SalesReturnDTO
        {
            SourceSystem = source, TransactionId = transactionId, ReturnId = request.ReturnId, ReturnedEligible = cumEligible - t.ReturnedEligible,
            ReturnedPaid = cumPaid - t.ReturnedPaid, PointsReversed = rv.Reversed, PointsRestored = rv.Restored, LinesJson = MembershipJson.Write(req),
            Reason = request.Reason, OccurredAt = request.OccurredAt ?? now, CreatedAt = now,
        });
        await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
        return new ReturnResultDTO
        {
            ReturnId = request.ReturnId, TransactionId = transactionId, PointsReversed = rv.Reversed, PointsRestored = rv.Restored,
            CouponsRestored = rv.CouponsRestored, Balance = rv.Balance,
        };
    }

    private async Task<Reversal> ReverseCumulativeAsync(MembershipCaller caller, SalesTransactionEntity t, decimal cumEligible, decimal cumPaid, bool full, string key, string? reason,
        MembershipChangeSet cs, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        PointPosting posting = await _ledger.OpenAsync(t.MemberId, now, ct).ConfigureAwait(false);
        var ctx = new LedgerContext(LedgerType.Reverse, key, caller.Actor) { SourceSystem = t.SourceSystem, TransactionId = t.TransactionId, Reason = reason, RequestId = caller.RequestId };

        // Step 1: ย้อนคะแนนที่ได้
        int reverse = PointMath.CumulativeReverse(t.PointsEarned, cumEligible, t.Eligible, t.PointsReversed);
        if (!rules.ReturnAllowNegativeBalance)
        {
            reverse = Math.Min(reverse, Math.Max(posting.Lots.Where(l => l.ExpiresAt > now).Sum(l => l.Remaining) - posting.Account.Reserved, 0));
        }

        PointLedgerService.Debit(posting, cs, reverse, ctx, now, allowDebt: rules.ReturnAllowNegativeBalance);

        // Step 2: คืนคะแนนที่จ่ายเป็น lot ใหม่ตามวันหมดอายุเดิม (เรียง FEFO ตามที่ตัดไป ข้ามส่วนที่คืนแล้ว)
        int restore = PointMath.CumulativeReverse(t.PointsRedeemed, cumPaid, t.NetPaid + t.PointsTender, t.PointsRestored);
        int skip = t.PointsRestored, left = restore, part = 0;
        foreach (ConsumedLotDTO lot in MembershipJson.Read<List<ConsumedLotDTO>>(t.ConsumedLotsJson))
        {
            int avail = lot.Points;
            int s = Math.Min(skip, avail);
            skip -= s;
            avail -= s;
            int take = Math.Min(avail, left);
            if (take > 0)
            {
                PointLedgerService.Credit(posting, cs, take, ctx with { Key = $"{key}:restore:{++part}", Reason = reason ?? "restore" }, now, lot.ExpiresAt, countLifetime: false);
                left -= take;
            }

            if (left == 0)
            {
                break;
            }
        }

        PointLedgerService.Finish(posting, cs);

        // Step 3: คืนคูปองเมื่อคืนทั้งบิล (ตามกฎ)
        var restored = new List<string>();
        if (full && rules.ReturnRestoreCouponOnFullReturn)
        {
            foreach (CouponDTO c in await _coupons.GetByCodesAsync(SplitCodes(t.CouponCodes), ct).ConfigureAwait(false))
            {
                if (c.Status != CouponStatus.Redeemed)
                {
                    continue;
                }

                CouponDTO next = c.Clone();
                next.Status = c.ExpiresAt <= now ? CouponStatus.Expired : CouponStatus.Reversed;
                next.RedeemedTransactionId = null;
                next.ReservationId = null;
                cs.Coupons.Add(new CouponChange(next, c.Version));
                cs.CouponEvents.Add(new CouponEventDTO { Code = c.Code, FromStatus = c.Status, ToStatus = next.Status, Reference = key, Actor = caller.Actor, At = now });
                if (next.Status == CouponStatus.Reversed)
                {
                    restored.Add(c.Code);
                }
            }
        }

        return new Reversal(reverse, restore - left, restored, PointLedgerService.ToDto(posting.Account, t.MemberId));
    }

    private static TransactionResultDTO ToResult(SalesTransactionEntity t, PointBalanceDTO? balance) => new()
    {
        TransactionId = t.TransactionId,
        MemberNo = t.MemberNo,
        Status = t.Status,
        PointsEarned = t.PointsEarned,
        PointsRedeemed = t.PointsRedeemed,
        PointsReversed = t.PointsReversed,
        PointsRestored = t.PointsRestored,
        Balance = balance,
    };

    [LoggerMessage(EventId = 5201, Level = LogLevel.Warning, Message = "Pending return {Source}/{TransactionId}/{ReturnId} has waited more than 7 days for its original transaction")]
    private static partial void LogPendingReturnStale(ILogger logger, string source, string transactionId, string returnId);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Warning, Message = "Pending return {Source}/{TransactionId}/{ReturnId} could not be applied: {Code}")]
    private static partial void LogPendingReturnFailed(ILogger logger, string source, string transactionId, string returnId, string? code);
}
