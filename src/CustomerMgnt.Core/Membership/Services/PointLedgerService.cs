using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>สถานะคะแนนของสมาชิก 1 คนระหว่างเตรียมบันทึก (โหลดครั้งเดียว แก้ในหน่วยความจำ แล้วใส่ change set)</summary>
public sealed class PointPosting
{
    internal PointPosting(PointAccountDTO account, List<PointLotDTO> lots)
    {
        ExpectedVersion = account.Version;
        Account = account.Clone();
        Lots = [.. lots.Select(l => l.Clone())];
        OriginalRemaining = lots.ToDictionary(l => l.Id, l => l.Remaining);
    }

    /// <summary>สมาชิก</summary>
    public long MemberId => Account.MemberId;
    /// <summary>ยอดหลังรายการที่เตรียมไว้</summary>
    public PointAccountDTO Account { get; }
    internal long ExpectedVersion { get; }
    internal List<PointLotDTO> Lots { get; }
    internal Dictionary<long, int> OriginalRemaining { get; }
    internal bool Finished { get; set; }
}

/// <summary>ข้อมูลประกอบรายการ ledger</summary>
/// <param name="Type">ชนิด</param>
/// <param name="Key">idempotency key (ไม่ซ้ำทั้งระบบ)</param>
/// <param name="Actor">ผู้กระทำ</param>
public sealed record LedgerContext(LedgerType Type, string Key, string Actor)
{
    public string? SourceSystem { get; init; }
    public string? TransactionId { get; init; }
    public string? Reference { get; init; }
    public string? Reason { get; init; }
    public string? RequestId { get; init; }
}

/// <summary>
/// เตรียมรายการคะแนนตาม spec 2.3–2.6: lot + FEFO + debt + ledger append-only — บันทึกจริงผ่าน <see cref="IMembershipUnitOfWork"/>
/// ทุกการเปลี่ยนยอดตรวจ version ของ <c>mbr_point_account</c> และคงเหลือเดิมของ lot (กันตัดซ้ำเมื่อทำพร้อมกัน)
/// </summary>
public sealed class PointLedgerService
{
    private readonly IPointDAO _points;

    /// <summary>สร้าง</summary>
    public PointLedgerService(IPointDAO points)
    {
        _points = points;
    }

    /// <summary>โหลดยอดและ lot ที่ยังมีคงเหลือ</summary>
    public async Task<PointPosting> OpenAsync(long memberId, DateTimeOffset now, CancellationToken ct)
    {
        PointAccountDTO account = await _points.GetAccountAsync(memberId, ct).ConfigureAwait(false);
        account.MemberId = memberId;
        IReadOnlyList<PointLotDTO> lots = await _points.ListLotsAsync(memberId, activeOnly: true, now, ct).ConfigureAwait(false);
        return new PointPosting(account, [.. lots]);
    }

    /// <summary>
    /// เพิ่มคะแนน: ชำระ debt ก่อน ส่วนที่เหลือเป็น lot ใหม่ (หมดอายุตาม spec 2.3 หรือวันที่กำหนด)
    /// </summary>
    /// <param name="p">สถานะ</param>
    /// <param name="cs">change set</param>
    /// <param name="points">คะแนน (&gt; 0)</param>
    /// <param name="ctx">ข้อมูล ledger</param>
    /// <param name="at">เวลาได้รับ</param>
    /// <param name="expiresAt">วันหมดอายุของ lot</param>
    /// <param name="countLifetime">นับเป็นคะแนนที่ได้รับสะสม</param>
    public static void Credit(PointPosting p, MembershipChangeSet cs, int points, LedgerContext ctx, DateTimeOffset at, DateTimeOffset expiresAt, bool countLifetime)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(ctx);
        if (points <= 0)
        {
            return;
        }

        // Step 1: ชำระ debt
        int payDebt = Math.Min(p.Account.Debt, points);
        p.Account.Debt -= payDebt;
        int rest = points - payDebt;

        // Step 2: lot ใหม่สำหรับส่วนที่เหลือ
        if (rest > 0)
        {
            cs.NewLots.Add(new PointLotDTO { MemberId = p.MemberId, Points = rest, Remaining = rest, EarnedAt = at, ExpiresAt = expiresAt, SourceKey = ctx.Key });
            p.Account.LotBalance += rest;
        }

        if (countLifetime)
        {
            p.Account.LifetimeEarned += points;
        }

        AddLedger(p, cs, ctx, points, at);
    }

    /// <summary>
    /// ตัดคะแนนแบบ FEFO (spec 2.4) — คะแนนไม่พอ: <paramref name="allowDebt"/> = เป็น debt, ไม่เช่นนั้น 409 insufficient_points
    /// </summary>
    /// <returns>lot ที่ถูกตัด (ใช้คืนคะแนนตามวันหมดอายุเดิม)</returns>
    public static List<ConsumedLotDTO> Debit(PointPosting p, MembershipChangeSet cs, int points, LedgerContext ctx, DateTimeOffset now, bool allowDebt)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(ctx);
        var consumed = new List<ConsumedLotDTO>();
        if (points <= 0)
        {
            return consumed;
        }

        // Step 1: FEFO
        (List<(PointLotDTO Lot, int Take)> allocations, int shortfall) = PointMath.AllocateFefo(p.Lots, points, now);
        if (shortfall > 0 && !allowDebt)
        {
            throw MembershipException.Conflict("insufficient_points", "Not enough points.");
        }

        foreach ((PointLotDTO lot, int take) in allocations)
        {
            lot.Remaining -= take;
            cs.Allocations.Add(new LotAllocationDTO { LedgerKey = ctx.Key, LotId = lot.Id, Points = take });
            consumed.Add(new ConsumedLotDTO { LotId = lot.Id, Points = take, ExpiresAt = lot.ExpiresAt });
        }

        // Step 2: ยอดสรุป
        p.Account.LotBalance -= points - shortfall;
        p.Account.Debt += shortfall;
        AddLedger(p, cs, ctx, -points, now);
        return consumed;
    }

    /// <summary>จองคะแนน (checkout reserve / แลกของรางวัล)</summary>
    public static void Reserve(PointPosting p, int points)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (points <= 0)
        {
            return;
        }

        if (points > p.Account.Available)
        {
            throw MembershipException.Conflict("insufficient_points", "Not enough points.");
        }

        p.Account.Reserved += points;
    }

    /// <summary>ปล่อยคะแนนที่จอง</summary>
    public static void Unreserve(PointPosting p, int points)
    {
        ArgumentNullException.ThrowIfNull(p);
        p.Account.Reserved = Math.Max(p.Account.Reserved - Math.Max(points, 0), 0);
    }

    /// <summary>ตัดคะแนนของ lot ที่หมดอายุ (idempotency key <c>expire:{lotId}</c>)</summary>
    public static void ExpireLot(PointPosting p, MembershipChangeSet cs, PointLotDTO lot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(lot);
        if (lot.Remaining <= 0)
        {
            return;
        }

        cs.LotChanges.Add(new LotChange(lot.Id, lot.Remaining, 0));
        p.Account.LotBalance -= lot.Remaining;
        AddLedger(p, cs, new LedgerContext(LedgerType.Expire, $"expire:{lot.Id}", "system") { Reference = $"lot:{lot.Id}" }, -lot.Remaining, now);
        lot.Remaining = 0;
    }

    /// <summary>ใส่ยอดและ lot ที่เปลี่ยนลง change set (เรียกครั้งเดียวต่อ posting)</summary>
    public static void Finish(PointPosting p, MembershipChangeSet cs)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(cs);
        if (p.Finished)
        {
            return;
        }

        p.Finished = true;
        cs.Accounts.Add(new AccountChange(p.Account.Clone(), p.ExpectedVersion));
        foreach (PointLotDTO lot in p.Lots.Where(l => p.OriginalRemaining.TryGetValue(l.Id, out int o) && o != l.Remaining))
        {
            cs.LotChanges.Add(new LotChange(lot.Id, p.OriginalRemaining[lot.Id], lot.Remaining));
        }
    }

    /// <summary>
    /// แปลงยอดสรุปเป็น DTO ของ API พร้อมคะแนนใกล้หมดอายุ
    /// </summary>
    public async Task<PointBalanceDTO> GetBalanceAsync(long memberId, DateTimeOffset now, int notifyDays, CancellationToken ct)
    {
        PointAccountDTO a = await _points.GetAccountAsync(memberId, ct).ConfigureAwait(false);
        IReadOnlyList<ExpiringPointsDTO> expiring = await _points.ListExpiringAsync(memberId, now, now.AddDays(Math.Max(notifyDays, 30)), ct).ConfigureAwait(false);
        return ToDto(a, memberId, expiring);
    }

    /// <summary>ยอด DTO จากยอดสรุป (ไม่อ่านฐานข้อมูล)</summary>
    public static PointBalanceDTO ToDto(PointAccountDTO a, long memberId, IReadOnlyList<ExpiringPointsDTO>? expiring = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new PointBalanceDTO
        {
            MemberId = memberId,
            Available = a.Available,
            Reserved = a.Reserved,
            Debt = a.Debt,
            LifetimeEarned = a.LifetimeEarned,
            Version = a.Version,
            ExpiringSoon = expiring is null ? [] : [.. expiring],
        };
    }

    private static void AddLedger(PointPosting p, MembershipChangeSet cs, LedgerContext ctx, int points, DateTimeOffset at) =>
        cs.Ledger.Add(new PointLedgerDTO
        {
            MemberId = p.MemberId,
            Type = ctx.Type,
            Points = points,
            BalanceAfter = p.Account.Available,
            SourceSystem = ctx.SourceSystem,
            TransactionId = ctx.TransactionId,
            Reference = ctx.Reference,
            Reason = ctx.Reason,
            Actor = ctx.Actor,
            RequestId = ctx.RequestId,
            IdempotencyKey = ctx.Key,
            CreatedAt = at,
        });
}
