using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>ผู้นำผลการอนุมัติไปใช้ (แคมเปญ ปรับคะแนน รวมบัญชี กฎคะแนน)</summary>
public interface IApprovalHandler
{
    /// <summary>ชนิดที่รับผิดชอบ</summary>
    ApprovalType Type { get; }

    /// <summary>อนุมัติครบทุกขั้น</summary>
    Task OnApprovedAsync(MembershipCaller approver, ApprovalRequestDTO request, CancellationToken ct);

    /// <summary>ไม่อนุมัติ หรือผู้ยื่นยกเลิก</summary>
    Task OnRejectedAsync(MembershipCaller actor, ApprovalRequestDTO request, CancellationToken ct);
}

/// <summary>
/// คำขออนุมัติแบบ maker-checker (กฎข้อ 9, 10): ผู้ยื่นอนุมัติเองไม่ได้ (403 self_approval), 2 ขั้นต้องเป็นคนละคน,
/// Reject ต้องมีเหตุผล — อนุมัติครบแล้วเรียก <see cref="IApprovalHandler"/> ของชนิดนั้น
/// </summary>
public sealed class ApprovalService
{
    private readonly IApprovalDAO _dao;
    private readonly IMembershipAuditDAO _audit;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง (handler resolve ตอนใช้ เพื่อไม่ให้ DI วนกัน)</summary>
    public ApprovalService(IApprovalDAO dao, IMembershipAuditDAO audit, IServiceProvider services, TimeProvider clock)
    {
        _dao = dao;
        _audit = audit;
        _services = services;
        _clock = clock;
    }

    /// <summary>สิทธิ์อนุมัติของแต่ละชนิด</summary>
    public static string ApprovePermission(ApprovalType type) => type switch
    {
        ApprovalType.Campaign => MembershipPermissions.PromotionApprove,
        ApprovalType.Adjustment => MembershipPermissions.PointsAdjustApprove,
        ApprovalType.Merge => MembershipPermissions.MemberMergeApprove,
        ApprovalType.EarnRule => MembershipPermissions.PointsRuleApprove,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>ยื่นคำขอ (ใช้ภายใน service อื่น)</summary>
    public async Task<ApprovalRequestDTO> SubmitAsync(MembershipCaller caller, ApprovalType type, long entityId, string summary, int levels,
        object? before, object? after, object? payload, string? comment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        DateTimeOffset now = _clock.GetUtcNow();
        var req = new ApprovalRequestDTO
        {
            Type = type,
            EntityId = entityId,
            Summary = summary,
            LevelsRequired = Math.Clamp(levels, 1, 2),
            Status = ApprovalStatus.Pending,
            BeforeJson = before is null ? null : MembershipJson.Write(before),
            AfterJson = after is null ? null : MembershipJson.Write(after),
            PayloadJson = payload is null ? null : MembershipJson.Write(payload),
            RequiredPermission = ApprovePermission(type),
            RequestedBy = caller.User.Subject,
            RequestedByName = caller.DisplayName,
            RequestComment = comment,
            RequestedAt = now,
            Steps = [.. Enumerable.Range(1, Math.Clamp(levels, 1, 2)).Select(l => new ApprovalStepDTO { Level = l })],
            Version = 1,
        };
        req.Id = await _dao.InsertAsync(req, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "approval.submit", $"approval:{req.Id}", null, new { req.Type, req.EntityId, req.Summary, req.LevelsRequired }, now)], ct).ConfigureAwait(false);
        return req;
    }

    /// <summary>รายการคำขอ (เห็นเฉพาะชนิดที่ตัวเองมีสิทธิ์อนุมัติ หรือที่ตัวเองยื่น)</summary>
    public async Task<IReadOnlyList<ApprovalRequestDTO>> SearchAsync(MembershipCaller caller, ApprovalSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Mine)
        {
            criteria.RequestedBy = caller.User.Subject;
        }

        IReadOnlyList<ApprovalRequestDTO> all = await _dao.SearchAsync(criteria, ct).ConfigureAwait(false);
        return [.. all.Where(r => CanSee(caller, r))];
    }

    /// <summary>คำขอหนึ่งรายการ</summary>
    public async Task<ApprovalRequestDTO> GetAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ApprovalRequestDTO? r = await _dao.GetAsync(id, ct).ConfigureAwait(false);
        return r is not null && CanSee(caller, r) ? r : throw new NotFoundException("Approval request not found.");
    }

    /// <summary>อนุมัติ / ไม่อนุมัติ</summary>
    public async Task<ApprovalRequestDTO> DecideAsync(MembershipCaller caller, long id, ApprovalDecisionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ApprovalRequestDTO r = await _dao.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Approval request not found.");

        // Step 1: สิทธิ์ + maker-checker
        caller.Require(r.RequiredPermission);
        if (string.Equals(r.RequestedBy, caller.User.Subject, StringComparison.Ordinal))
        {
            throw new MembershipException(403, "self_approval", "You cannot approve your own request.");
        }

        if (r.Status != ApprovalStatus.Pending)
        {
            throw MembershipException.Conflict("invalid_transition", "This request has already been decided.");
        }

        ValidationException.ThrowIf(request.Decision == ApprovalDecision.Reject && string.IsNullOrWhiteSpace(request.Comment), "comment", "A comment is required when rejecting.");
        if (r.Steps.Any(s => string.Equals(s.DecidedBy, caller.User.Subject, StringComparison.Ordinal)))
        {
            throw new MembershipException(403, "self_approval", "Each approval level must be decided by a different person.");
        }

        // Step 2: บันทึกขั้น
        DateTimeOffset now = _clock.GetUtcNow();
        ApprovalStepDTO step = r.Steps.OrderBy(s => s.Level).First(s => s.Decision is null);
        step.DecidedBy = caller.User.Subject;
        step.DecidedByName = caller.DisplayName;
        step.Decision = request.Decision;
        step.Comment = request.Comment;
        step.DecidedAt = now;
        if (request.Decision == ApprovalDecision.Reject)
        {
            r.Status = ApprovalStatus.Rejected;
        }
        else
        {
            r.LevelsApproved++;
            if (r.LevelsApproved >= r.LevelsRequired)
            {
                r.Status = ApprovalStatus.Approved;
            }
        }

        if (!await _dao.UpdateAsync(r, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        r.Version++;
        await _audit.AppendAsync([MembershipEvents.Audit(caller, request.Decision == ApprovalDecision.Approve ? "approval.approve" : "approval.reject", $"approval:{r.Id}", null, new { step.Level, request.Comment, r.Status }, now)], ct).ConfigureAwait(false);

        // Step 3: นำผลไปใช้
        if (r.Status == ApprovalStatus.Approved)
        {
            await Handler(r.Type).OnApprovedAsync(caller, r, ct).ConfigureAwait(false);
        }
        else if (r.Status == ApprovalStatus.Rejected)
        {
            await Handler(r.Type).OnRejectedAsync(caller, r, ct).ConfigureAwait(false);
        }

        return r;
    }

    /// <summary>ผู้ยื่นยกเลิกคำขอที่ยังไม่ตัดสิน</summary>
    public async Task<ApprovalRequestDTO> CancelAsync(MembershipCaller caller, long id, ApprovalCancelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ApprovalRequestDTO r = await _dao.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Approval request not found.");
        if (!string.Equals(r.RequestedBy, caller.User.Subject, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the requester can cancel this request.");
        }

        if (r.Status != ApprovalStatus.Pending)
        {
            throw MembershipException.Conflict("invalid_transition", "This request has already been decided.");
        }

        r.Status = ApprovalStatus.Cancelled;
        r.RequestComment = string.IsNullOrWhiteSpace(request.Comment) ? r.RequestComment : r.RequestComment + " | cancel: " + request.Comment;
        if (!await _dao.UpdateAsync(r, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        r.Version++;
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "approval.cancel", $"approval:{r.Id}", null, new { request.Comment }, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        await Handler(r.Type).OnRejectedAsync(caller, r, ct).ConfigureAwait(false);
        return r;
    }

    /// <summary>จำนวนคำขอที่รอ (dashboard)</summary>
    public async Task<int> CountPendingAsync(MembershipCaller caller, CancellationToken ct)
    {
        IReadOnlyList<ApprovalRequestDTO> list = await SearchAsync(caller, new ApprovalSearchCriteria { Status = ApprovalStatus.Pending }, ct).ConfigureAwait(false);
        return list.Count(r => caller.Has(r.RequiredPermission));
    }

    private static bool CanSee(MembershipCaller caller, ApprovalRequestDTO r) =>
        caller.Has(r.RequiredPermission) || string.Equals(r.RequestedBy, caller.User.Subject, StringComparison.Ordinal);

    private IApprovalHandler Handler(ApprovalType type) =>
        _services.GetServices<IApprovalHandler>().FirstOrDefault(h => h.Type == type) ?? throw new InvalidOperationException($"No approval handler for {type}.");
}
