using System.Security.Cryptography;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>คูปอง (PRO-004): ชุดคูปอง สร้างรหัส ออกให้สมาชิก ยกเลิก หมดอายุ</summary>
public sealed class CouponService
{
    /// <summary>ตัวอักษรของรหัส (ไม่มี 0/O/1/I ที่อ่านสับสน)</summary>
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>สร้างรหัสใน request เมื่อจำนวนไม่เกินนี้ ที่เหลือให้งานเบื้องหลัง</summary>
    internal const int InlineGenerateLimit = 5000;

    private readonly ICouponDAO _coupons;
    private readonly ICampaignDAO _campaigns;
    private readonly ISegmentDAO _segments;
    private readonly IMemberDAO _members;
    private readonly IMembershipAuditDAO _audit;
    private readonly IMembershipUnitOfWork _uow;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public CouponService(ICouponDAO coupons, ICampaignDAO campaigns, ISegmentDAO segments, IMemberDAO members, IMembershipAuditDAO audit, IMembershipUnitOfWork uow, TimeProvider clock)
    {
        _coupons = coupons;
        _campaigns = campaigns;
        _segments = segments;
        _members = members;
        _audit = audit;
        _uow = uow;
        _clock = clock;
    }

    /// <summary>ชุดคูปอง</summary>
    public Task<IReadOnlyList<CouponBatchDTO>> ListBatchesAsync(MembershipCaller caller, long? campaignId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.CouponRead);
        return _coupons.ListBatchesAsync(campaignId, ct);
    }

    /// <summary>สร้างชุดคูปอง — สร้างรหัสทันทีถ้าไม่เกิน 5,000 ใบ ไม่เช่นนั้นให้งานเบื้องหลังสร้าง</summary>
    public async Task<JobDTO> CreateBatchAsync(MembershipCaller caller, CouponBatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.CouponWrite);
        _ = await _campaigns.GetAsync(request.CampaignId, ct).ConfigureAwait(false) ?? throw new ValidationException("Campaign not found.", new Dictionary<string, string[]> { ["campaignId"] = ["Campaign not found."] });
        DateTimeOffset now = _clock.GetUtcNow();
        ValidationException.ThrowIf(request.ExpiresAt <= now, "expiresAt", "Expiry must be in the future.");
        var batch = new CouponBatchDTO
        {
            CampaignId = request.CampaignId, Name = request.Name.Trim(), Quantity = request.Quantity, CodePrefix = request.CodePrefix, CodeLength = request.CodeLength,
            ExpiresAt = request.ExpiresAt, PerMember = request.PerMember, PointsCost = request.PointsCost,
            Status = request.Quantity == 0 ? JobStatus.Done : JobStatus.Queued, CreatedBy = caller.User.Subject, CreatedAt = now,
        };
        batch.Id = await _coupons.InsertBatchAsync(batch, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "coupon.batch_create", $"coupon_batch:{batch.Id}", null, batch, now)], ct).ConfigureAwait(false);
        if (batch.Quantity > 0 && batch.Quantity <= InlineGenerateLimit)
        {
            await GenerateAsync(batch, ct).ConfigureAwait(false);
        }

        return new JobDTO { JobId = $"coupon_batch:{batch.Id}", Kind = "coupon.generate", Status = batch.Status, Message = $"{batch.Generated}/{batch.Quantity}" };
    }

    /// <summary>สร้างรหัสที่ยังขาดของชุด (งานเบื้องหลังเรียกซ้ำได้)</summary>
    public async Task GenerateAsync(CouponBatchDTO batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        batch.Status = JobStatus.Running;
        await _coupons.UpdateBatchAsync(batch, ct).ConfigureAwait(false);
        while (batch.Generated < batch.Quantity)
        {
            int chunk = Math.Min(1000, batch.Quantity - batch.Generated);
            List<CouponDTO> coupons = [.. Enumerable.Range(0, chunk).Select(_ => NewCoupon(batch, null))];
            batch.Generated += await _coupons.InsertManyAsync(coupons, ct).ConfigureAwait(false);
            await _coupons.UpdateBatchAsync(batch, ct).ConfigureAwait(false);
        }

        batch.Status = JobStatus.Done;
        await _coupons.UpdateBatchAsync(batch, ct).ConfigureAwait(false);
    }

    /// <summary>ค้นหาคูปอง</summary>
    public async Task<PagedResultDTO<CouponDTO>> SearchAsync(MembershipCaller caller, CouponSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.CouponRead);
        return await _coupons.SearchAsync(criteria, ct).ConfigureAwait(false);
    }

    /// <summary>คูปองพร้อมประวัติ</summary>
    public async Task<CouponDetailDTO> GetAsync(MembershipCaller caller, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.CouponRead);
        CouponDTO c = await _coupons.GetByCodeAsync(Normalize(code), ct).ConfigureAwait(false) ?? throw new NotFoundException("Coupon not found.");
        return new CouponDetailDTO { Coupon = c, History = [.. await _coupons.ListEventsAsync(c.Code, ct).ConfigureAwait(false)] };
    }

    /// <summary>ยกเลิกคูปองที่ยังไม่ใช้</summary>
    public async Task<CouponDTO> CancelAsync(MembershipCaller caller, string code, CancelCouponRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.CouponWrite);
        CouponDTO c = await _coupons.GetByCodeAsync(Normalize(code), ct).ConfigureAwait(false) ?? throw new NotFoundException("Coupon not found.");
        if (c.Status is not (CouponStatus.Available or CouponStatus.Reversed))
        {
            throw MembershipException.InvalidTransition(c.Status, CouponStatus.Cancelled);
        }

        DateTimeOffset now = _clock.GetUtcNow();
        CouponDTO next = c.Clone();
        next.Status = CouponStatus.Cancelled;
        var cs = new MembershipChangeSet();
        cs.Coupons.Add(new CouponChange(next, c.Version));
        cs.CouponEvents.Add(new CouponEventDTO { Code = c.Code, FromStatus = c.Status, ToStatus = CouponStatus.Cancelled, Reference = request.Reason, Actor = caller.Actor, At = now });
        cs.Audit.Add(MembershipEvents.Audit(caller, "coupon.cancel", $"coupon:{c.Code}", new { c.Status }, new { next.Status, request.Reason }, now));
        await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
        next.Version++;
        return next;
    }

    /// <summary>ออกคูปองของชุดให้สมาชิก (ระบุรายคน หรือทั้ง segment)</summary>
    public async Task<JobDTO> IssueAsync(MembershipCaller caller, IssueCouponsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.CouponWrite);
        CouponBatchDTO batch = await _coupons.GetBatchAsync(request.BatchId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Coupon batch not found.");
        var memberIds = new List<long>(request.MemberIds ?? []);
        if (request.SegmentId is long segId)
        {
            for (int page = 1; ; page++)
            {
                PagedResultDTO<long> ids = await _segments.PageMemberIdsAsync(segId, page, 1000, ct).ConfigureAwait(false);
                memberIds.AddRange(ids.Items);
                if (ids.Items.Count < 1000)
                {
                    break;
                }
            }
        }

        ValidationException.ThrowIf(memberIds.Count == 0, "memberIds", "Choose members or a segment.");
        int issued = 0;
        foreach (long id in memberIds.Distinct())
        {
            if (await _members.GetAsync(id, ct).ConfigureAwait(false) is { Status: MemberStatus.Active })
            {
                await IssueToMemberAsync(batch, id, caller.Actor, $"issue:{caller.User.Subject}", null, ct).ConfigureAwait(false);
                issued++;
            }
        }

        await _audit.AppendAsync([MembershipEvents.Audit(caller, "coupon.issue", $"coupon_batch:{batch.Id}", null, new { issued, request.SegmentId }, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        return new JobDTO { JobId = $"coupon_issue:{batch.Id}:{_clock.GetUtcNow():yyyyMMddHHmmss}", Kind = "coupon.issue", Status = JobStatus.Done, Message = $"{issued} coupons issued" };
    }

    /// <summary>ออกคูปอง 1 ใบให้สมาชิก (ใช้ใบว่างของชุดก่อน ไม่มีจึงสร้างใหม่)</summary>
    internal async Task<CouponDTO> IssueToMemberAsync(CouponBatchDTO batch, long memberId, string actor, string reference, MembershipChangeSet? into, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        CouponDTO? pooled = (await _coupons.TakeUnassignedAsync(batch.Id, 1, ct).ConfigureAwait(false)).FirstOrDefault();
        CouponDTO coupon;
        var cs = into ?? new MembershipChangeSet();
        if (pooled is not null)
        {
            coupon = pooled.Clone();
            coupon.MemberId = memberId;
            cs.Coupons.Add(new CouponChange(coupon, pooled.Version));
        }
        else
        {
            coupon = NewCoupon(batch, memberId);
            cs.Coupons.Add(new CouponChange(coupon, 0));
        }

        cs.CouponEvents.Add(new CouponEventDTO { Code = coupon.Code, FromStatus = CouponStatus.Available, ToStatus = CouponStatus.Available, Reference = reference, Actor = actor, At = now });
        cs.Outbox.Add(MembershipEvents.Outbox("coupon.issued", new { memberId, coupon.Code, coupon.CampaignId, coupon.ExpiresAt }, now));
        if (into is null)
        {
            await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
        }

        return coupon;
    }

    /// <summary>คูปองใหม่ (รหัสสุ่มจาก CSPRNG)</summary>
    internal CouponDTO NewCoupon(CouponBatchDTO batch, long? memberId) => new()
    {
        Code = batch.CodePrefix + RandomCode(batch.CodeLength),
        BatchId = batch.Id,
        CampaignId = batch.CampaignId,
        MemberId = memberId,
        Status = CouponStatus.Available,
        ExpiresAt = batch.ExpiresAt,
        CreatedAt = _clock.GetUtcNow(),
        Version = 1,
    };

    /// <summary>รหัสคูปองแบบมาตรฐาน (ตัวพิมพ์ใหญ่ ไม่มีช่องว่าง)</summary>
    public static string Normalize(string code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    private static string RandomCode(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
