using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>Segment (SEG-001): เงื่อนไข And/Or, ดูตัวอย่าง, คำนวณสมาชิกทุกคืนและเมื่อสั่ง, RFM</summary>
public sealed class SegmentService
{
    private readonly ISegmentDAO _segments;
    private readonly IMemberDAO _members;
    private readonly IMembershipAuditDAO _audit;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public SegmentService(ISegmentDAO segments, IMemberDAO members, IMembershipAuditDAO audit, TimeProvider clock)
    {
        _segments = segments;
        _members = members;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>segment ทั้งหมด</summary>
    public Task<IReadOnlyList<SegmentDTO>> ListAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.SegmentRead);
        return _segments.ListAsync(ct);
    }

    /// <summary>segment หนึ่งรายการ</summary>
    public async Task<SegmentDTO> GetAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.SegmentRead);
        return await _segments.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Segment not found.");
    }

    /// <summary>สร้าง segment แล้วคำนวณสมาชิกทันที</summary>
    public async Task<SegmentDTO> CreateAsync(MembershipCaller caller, SegmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.SegmentWrite);
        SegmentRuleEvaluator.Validate(request.Rule);
        var s = new SegmentDTO { Name = request.Name.Trim(), Description = request.Description, Rule = request.Rule, RefreshDaily = request.RefreshDaily, CreatedBy = caller.User.Subject, Version = 1 };
        s.Id = await _segments.InsertAsync(s, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "segment.create", $"segment:{s.Id}", null, s, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        return await RefreshCoreAsync(s, ct).ConfigureAwait(false);
    }

    /// <summary>แก้ segment (ตรวจ version) แล้วคำนวณใหม่</summary>
    public async Task<SegmentDTO> UpdateAsync(MembershipCaller caller, long id, SegmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.SegmentWrite);
        MembershipEvents.RequireVersion(request.Version);
        SegmentRuleEvaluator.Validate(request.Rule);
        SegmentDTO before = await _segments.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Segment not found.");
        var s = new SegmentDTO
        {
            Id = id, Name = request.Name.Trim(), Description = request.Description, Rule = request.Rule, RefreshDaily = request.RefreshDaily,
            CreatedBy = before.CreatedBy, MemberCount = before.MemberCount, RefreshedAt = before.RefreshedAt, Version = request.Version!.Value,
        };
        if (!await _segments.UpdateAsync(s, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        s.Version++;
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "segment.update", $"segment:{id}", before, s, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        return await RefreshCoreAsync(s, ct).ConfigureAwait(false);
    }

    /// <summary>ดูจำนวนและตัวอย่าง 10 คน (ไม่บันทึก)</summary>
    public async Task<SegmentPreviewDTO> PreviewAsync(MembershipCaller caller, SegmentRuleDTO rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(rule);
        caller.Require(MembershipPermissions.SegmentRead);
        SegmentRuleEvaluator.Validate(rule);
        List<long> ids = await EvaluateAsync(rule, ct).ConfigureAwait(false);
        var sample = new List<MemberSummaryDTO>();
        foreach (long id in ids.Take(10))
        {
            if (await _members.GetAsync(id, ct).ConfigureAwait(false) is MemberEntity m)
            {
                sample.Add(MemberService.ToSummary(m));
            }
        }

        return new SegmentPreviewDTO { Count = ids.Count, Sample = sample };
    }

    /// <summary>คำนวณสมาชิกใหม่</summary>
    public async Task<JobDTO> RefreshAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.SegmentWrite);
        SegmentDTO s = await _segments.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Segment not found.");
        s = await RefreshCoreAsync(s, ct).ConfigureAwait(false);
        return new JobDTO { JobId = $"segment:{id}", Kind = "segment.refresh", Status = JobStatus.Done, Message = $"{s.MemberCount} members" };
    }

    /// <summary>สมาชิกใน segment</summary>
    public async Task<PagedResultDTO<MemberSummaryDTO>> ListMembersAsync(MembershipCaller caller, long id, int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.SegmentRead);
        PagedResultDTO<MemberEntity> result = await _members.SearchAsync(new MemberSearchCriteria { SegmentId = id, Page = page, PageSize = pageSize }, ct).ConfigureAwait(false);
        return new PagedResultDTO<MemberSummaryDTO>([.. result.Items.Select(MemberService.ToSummary)], result.Total, result.Page, result.PageSize);
    }

    /// <summary>งานรายคืน: คำนวณทุก segment ที่ตั้ง RefreshDaily</summary>
    public async Task<int> RefreshAllAsync(CancellationToken ct)
    {
        int n = 0;
        foreach (SegmentDTO s in (await _segments.ListAsync(ct).ConfigureAwait(false)).Where(s => s.RefreshDaily))
        {
            await RefreshCoreAsync(s, ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    private async Task<SegmentDTO> RefreshCoreAsync(SegmentDTO s, CancellationToken ct)
    {
        List<long> ids = await EvaluateAsync(s.Rule, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        await _segments.ReplaceMembersAsync(s.Id, ids, now, ct).ConfigureAwait(false);
        s.MemberCount = ids.Count;
        s.RefreshedAt = now;
        return s;
    }

    /// <summary>ประเมินเงื่อนไขกับสมาชิกทุกคน (ข้อเท็จจริงทีละ 5,000 คน, คำนวณ RFM เมื่อเงื่อนไขใช้)</summary>
    private async Task<List<long>> EvaluateAsync(SegmentRuleDTO rule, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        var facts = new List<MemberFactsDTO>();
        long after = 0;
        while (true)
        {
            IReadOnlyList<MemberFactsDTO> page = await _members.ListFactsAsync(after, 5000, now, ct).ConfigureAwait(false);
            facts.AddRange(page);
            if (page.Count < 5000)
            {
                break;
            }

            after = page[^1].MemberId;
        }

        if (UsesRfm(rule))
        {
            Func<decimal, int> r = SegmentRuleEvaluator.Quintile([.. facts.Select(f => (decimal)(f.RecencyDays ?? 9999))], invert: true);
            Func<decimal, int> fq = SegmentRuleEvaluator.Quintile([.. facts.Select(f => (decimal)f.Frequency12m)]);
            Func<decimal, int> m = SegmentRuleEvaluator.Quintile([.. facts.Select(f => f.Spend12m)]);
            foreach (MemberFactsDTO f in facts)
            {
                f.RfmR = r(f.RecencyDays ?? 9999);
                f.RfmF = fq(f.Frequency12m);
                f.RfmM = m(f.Spend12m);
            }
        }

        return [.. facts.Where(f => SegmentRuleEvaluator.Matches(rule, f)).Select(f => f.MemberId)];
    }

    private static bool UsesRfm(SegmentRuleDTO r) =>
        (r.Field?.StartsWith("rfm", StringComparison.OrdinalIgnoreCase) ?? false) || (r.Items?.Any(UsesRfm) ?? false);
}
