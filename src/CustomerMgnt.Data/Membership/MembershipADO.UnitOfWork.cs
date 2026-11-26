using System.Data.Common;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Data.Infrastructure;
using Dapper;

namespace CustomerMgnt.Data.Membership;

/// <summary>บันทึกชุดการเปลี่ยนแปลงใน transaction เดียว (optimistic concurrency)</summary>
public sealed partial class MembershipADO : IMembershipUnitOfWork
{
    /// <inheritdoc />
    public async Task CommitAsync(MembershipChangeSet changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.IsEmpty)
        {
            return;
        }

        try
        {
            await RunAsync("membership.commit", async c =>
            {
                await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
                await ApplyAsync(c, tx, changes, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (ConflictException)
        {
            // unique violation (idempotency key / รายการขายซ้ำ / แถวใหม่ชนกัน) = ให้ผู้เรียกอ่านใหม่แล้วลองอีกครั้ง
            throw new MembershipConcurrencyException("unique");
        }
    }

    private static void Expect(int rows, string what)
    {
        if (rows != 1)
        {
            throw new MembershipConcurrencyException(what);
        }
    }

    private async Task ApplyAsync(DbConnection c, DbTransaction tx, MembershipChangeSet cs, CancellationToken ct)
    {
        foreach (AccountChange a in cs.Accounts)
        {
            PointAccountDTO x = a.NewState;
            var args = new { x.MemberId, x.LotBalance, x.Reserved, x.Debt, x.LifetimeEarned, Version = a.ExpectedVersion + 1, Expected = a.ExpectedVersion };
            Expect(await c.ExecuteAsync(Cmd(a.ExpectedVersion == 0
                ? "INSERT INTO mbr_point_account (member_id, lot_balance, reserved, debt, lifetime_earned, version) VALUES (@MemberId, @LotBalance, @Reserved, @Debt, @LifetimeEarned, @Version)"
                : """
                  UPDATE mbr_point_account SET lot_balance = @LotBalance, reserved = @Reserved, debt = @Debt, lifetime_earned = @LifetimeEarned, version = @Version
                  WHERE member_id = @MemberId AND version = @Expected
                  """, args, ct, tx)).ConfigureAwait(false), "account");
        }

        foreach (LotChange l in cs.LotChanges)
        {
            Expect(await c.ExecuteAsync(Cmd("UPDATE mbr_point_lot SET remaining = @NewRemaining WHERE id = @LotId AND remaining = @ExpectedRemaining", l, ct, tx)).ConfigureAwait(false), "lot");
        }

        if (cs.NewLots.Count > 0)
        {
            await c.ExecuteAsync(Cmd("""
                INSERT INTO mbr_point_lot (member_id, points, remaining, earned_at, expires_at, source_key)
                VALUES (@MemberId, @Points, @Remaining, @EarnedAt, @ExpiresAt, @SourceKey)
                """, cs.NewLots, ct, tx)).ConfigureAwait(false);
        }

        if (cs.Ledger.Count > 0)
        {
            await c.ExecuteAsync(Cmd("""
                INSERT INTO mbr_point_ledger (member_id, type, points, balance_after, source_system, transaction_id, reference, reason, actor, request_id, idempotency_key, created_at)
                VALUES (@MemberId, @Type, @Points, @BalanceAfter, @SourceSystem, @TransactionId, @Reference, @Reason, @Actor, @RequestId, @IdempotencyKey, @CreatedAt)
                """, cs.Ledger.Select(l => new
            {
                l.MemberId, Type = l.Type.ToString(), l.Points, l.BalanceAfter, l.SourceSystem, l.TransactionId, l.Reference, l.Reason, l.Actor, l.RequestId, l.IdempotencyKey, l.CreatedAt,
            }), ct, tx)).ConfigureAwait(false);
        }

        if (cs.Allocations.Count > 0)
        {
            await c.ExecuteAsync(Cmd("INSERT INTO mbr_lot_allocation (ledger_key, lot_id, points) VALUES (@LedgerKey, @LotId, @Points)", cs.Allocations, ct, tx)).ConfigureAwait(false);
        }

        foreach (CouponChange cc in cs.Coupons)
        {
            Expect(await c.ExecuteAsync(Cmd(cc.ExpectedVersion == 0
                ? CouponInsert
                : """
                  UPDATE mbr_coupon SET batch_id = @BatchId, campaign_id = @CampaignId, member_id = @MemberId, status = @Status, expires_at = @ExpiresAt, version = @Version, data = @Data
                  WHERE code = @Code AND version = @Expected
                  """, CouponArgs(cc.NewState, cc.ExpectedVersion), ct, tx)).ConfigureAwait(false), "coupon");
        }

        if (cs.CouponEvents.Count > 0)
        {
            await InsertCouponEventsAsync(c, tx, cs.CouponEvents, ct).ConfigureAwait(false);
        }

        await ApplyCountersAsync(c, tx, cs.Counters, ct).ConfigureAwait(false);

        if (cs.NewReservation is ReservationEntity nr)
        {
            await c.ExecuteAsync(Cmd("INSERT INTO mbr_reservation (id, status, expires_at, version, data) VALUES (@Id, @Status, @ExpiresAt, @Version, @Data)",
                new { nr.Id, Status = nr.Status.ToString(), nr.ExpiresAt, nr.Version, Data = J(nr) }, ct, tx)).ConfigureAwait(false);
        }

        if (cs.ReservationUpdate is ReservationEntity ru)
        {
            Expect(await c.ExecuteAsync(Cmd("UPDATE mbr_reservation SET status = @Status, expires_at = @ExpiresAt, version = version + 1, data = @Data WHERE id = @Id AND version = @Version",
                new { ru.Id, Status = ru.Status.ToString(), ru.ExpiresAt, ru.Version, Data = J(ru) }, ct, tx)).ConfigureAwait(false), "reservation");
        }

        if (cs.NewTransaction is SalesTransactionEntity nt)
        {
            nt.Version = 1;
            nt.Id = await c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("mbr_transaction",
                "source_system, transaction_id, member_id, branch_code, occurred_at, status, net_paid, eligible, returned_eligible, is_test, version, data",
                "@SourceSystem, @TransactionId, @MemberId, @BranchCode, @OccurredAt, @Status, @NetPaid, @Eligible, @ReturnedEligible, @IsTest, 1, @Data"), TxnArgs(nt), ct, tx)).ConfigureAwait(false);
            await c.ExecuteAsync(Cmd("UPDATE mbr_transaction SET data = @Data WHERE id = @Id", new { nt.Id, Data = J(nt) }, ct, tx)).ConfigureAwait(false);
        }

        if (cs.TransactionUpdate is SalesTransactionEntity tu)
        {
            Expect(await c.ExecuteAsync(Cmd("""
                UPDATE mbr_transaction SET status = @Status, net_paid = @NetPaid, eligible = @Eligible, returned_eligible = @ReturnedEligible, version = version + 1, data = @Data
                WHERE id = @Id AND version = @Version
                """, TxnArgs(tu), ct, tx)).ConfigureAwait(false), "transaction");
        }

        if (cs.Returns.Count > 0)
        {
            await c.ExecuteAsync(Cmd("INSERT INTO mbr_return (source_system, transaction_id, return_id, data) VALUES (@SourceSystem, @TransactionId, @ReturnId, @Data)",
                cs.Returns.Select(r => new { r.SourceSystem, r.TransactionId, r.ReturnId, Data = J(r) }), ct, tx)).ConfigureAwait(false);
        }

        foreach (MemberEntity m in cs.MemberUpdates)
        {
            if (!await UpdateMemberAsync(c, tx, m, ct).ConfigureAwait(false))
            {
                throw new MembershipConcurrencyException("member");
            }
        }

        if (cs.TierHistory.Count > 0)
        {
            await c.ExecuteAsync(Cmd("INSERT INTO mbr_tier_history (member_id, changed_at, data) VALUES (@MemberId, @ChangedAt, @Data)",
                cs.TierHistory.Select(h => new { h.MemberId, h.ChangedAt, Data = J(h) }), ct, tx)).ConfigureAwait(false);
        }

        if (cs.AdjustmentUpdate is AdjustmentDTO adj)
        {
            Expect(await UpdateAdjustmentAsync(c, tx, adj, ct).ConfigureAwait(false), "adjustment");
        }

        if (cs.Outbox.Count > 0)
        {
            await InsertOutboxAsync(c, tx, cs.Outbox, ct).ConfigureAwait(false);
        }

        // audit ล็อกทั้งตาราง จึงทำเป็นขั้นสุดท้ายให้ถือ lock สั้นที่สุด
        if (cs.Audit.Count > 0)
        {
            await AppendAuditAsync(c, tx, cs.Audit, ct).ConfigureAwait(false);
        }
    }

    private static object TxnArgs(SalesTransactionEntity t) => new
    {
        t.Id, t.SourceSystem, t.TransactionId, t.MemberId, t.BranchCode, t.OccurredAt, Status = t.Status.ToString(), t.NetPaid, t.Eligible, t.ReturnedEligible, t.IsTest, t.Version, Data = J(t),
    };

    /// <summary>รวม delta ต่อ (campaign, scope) แล้วล็อกแถว ตรวจจำกัด และบันทึก</summary>
    private async Task ApplyCountersAsync(DbConnection c, DbTransaction tx, IReadOnlyList<CampaignCounterDelta> deltas, CancellationToken ct)
    {
        string select = Dialect.IsSqlServer()
            ? "SELECT uses, reserved, budget_used, budget_reserved FROM mbr_campaign_counter WITH (UPDLOCK, HOLDLOCK) WHERE campaign_id = @CampaignId AND scope = @Scope"
            : "SELECT uses, reserved, budget_used, budget_reserved FROM mbr_campaign_counter WHERE campaign_id = @CampaignId AND scope = @Scope FOR UPDATE";
        foreach (IGrouping<(long CampaignId, string Scope), CampaignCounterDelta> g in deltas.GroupBy(d => (d.CampaignId, d.Scope)).OrderBy(g => g.Key.CampaignId).ThenBy(g => g.Key.Scope, StringComparer.Ordinal))
        {
            var key = new { g.Key.CampaignId, g.Key.Scope };
            CampaignUsageDTO? cur = await c.QuerySingleOrDefaultAsync<CampaignUsageDTO>(Cmd(select, key, ct, tx)).ConfigureAwait(false);
            CampaignUsageDTO u = cur ?? new CampaignUsageDTO();
            int uses = g.Sum(d => d.UsesDelta + d.ReservedDelta);
            decimal budget = g.Sum(d => d.BudgetDelta + d.BudgetReservedDelta);
            int? limit = g.Select(d => d.Limit).FirstOrDefault(l => l is not null);
            decimal? budgetLimit = g.Select(d => d.BudgetLimit).FirstOrDefault(l => l is not null);
            if (uses > 0 && limit is int lim && u.Uses + u.Reserved + uses > lim)
            {
                throw new MembershipLimitException("limit_reached");
            }

            if (budget > 0 && budgetLimit is decimal bl && u.BudgetUsed + u.BudgetReserved + budget > bl)
            {
                throw new MembershipLimitException("budget_exhausted");
            }

            var next = new
            {
                key.CampaignId,
                key.Scope,
                Uses = Math.Max(u.Uses + g.Sum(d => d.UsesDelta), 0),
                Reserved = Math.Max(u.Reserved + g.Sum(d => d.ReservedDelta), 0),
                BudgetUsed = Math.Max(u.BudgetUsed + g.Sum(d => d.BudgetDelta), 0),
                BudgetReserved = Math.Max(u.BudgetReserved + g.Sum(d => d.BudgetReservedDelta), 0),
            };
            await c.ExecuteAsync(Cmd(cur is null
                ? "INSERT INTO mbr_campaign_counter (campaign_id, scope, uses, reserved, budget_used, budget_reserved) VALUES (@CampaignId, @Scope, @Uses, @Reserved, @BudgetUsed, @BudgetReserved)"
                : "UPDATE mbr_campaign_counter SET uses = @Uses, reserved = @Reserved, budget_used = @BudgetUsed, budget_reserved = @BudgetReserved WHERE campaign_id = @CampaignId AND scope = @Scope",
                next, ct, tx)).ConfigureAwait(false);
        }
    }
}
