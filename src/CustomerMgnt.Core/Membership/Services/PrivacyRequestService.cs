using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>คำขอตาม PDPA: ขอดู / แก้ไข / ลบ / ปิดบัญชี — ต้องดำเนินการภายใน 30 วัน</summary>
public sealed class PrivacyRequestService
{
    /// <summary>กำหนดเวลาตอบคำขอ (วัน)</summary>
    public const int DueDays = 30;

    private readonly IMemberPrivacyDAO _privacy;
    private readonly IMemberDAO _members;
    private readonly IMembershipAuditDAO _audit;
    private readonly IOutboxDAO _outbox;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public PrivacyRequestService(IMemberPrivacyDAO privacy, IMemberDAO members, IMembershipAuditDAO audit, IOutboxDAO outbox, TimeProvider clock)
    {
        _privacy = privacy;
        _members = members;
        _audit = audit;
        _outbox = outbox;
        _clock = clock;
    }

    /// <summary>คำขอทั้งหมด (เจ้าหน้าที่)</summary>
    public Task<IReadOnlyList<PrivacyRequestDTO>> ListAsync(MembershipCaller caller, PrivacyRequestStatus? status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PrivacyManage);
        return _privacy.ListPrivacyRequestsAsync(null, status, ct);
    }

    /// <summary>คำขอหนึ่งรายการ</summary>
    public async Task<PrivacyRequestDTO> GetAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.PrivacyManage);
        return await _privacy.GetPrivacyRequestAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Privacy request not found.");
    }

    /// <summary>สมาชิกยื่นคำขอ (Portal)</summary>
    internal async Task<PrivacyRequestDTO> CreateAsync(MembershipCaller caller, MemberEntity member, CreatePrivacyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<PrivacyRequestDTO> open = await _privacy.ListPrivacyRequestsAsync(member.Id, PrivacyRequestStatus.Open, ct).ConfigureAwait(false);
        if (open.Any(r => r.Type == request.Type))
        {
            throw MembershipException.Conflict("conflict", "A request of this type is already open.");
        }

        DateTimeOffset now = _clock.GetUtcNow();
        var r = new PrivacyRequestDTO
        {
            MemberId = member.Id,
            MemberNo = member.MemberNo,
            Type = request.Type,
            Detail = request.Detail?.Trim(),
            Status = PrivacyRequestStatus.Open,
            RequestedAt = now,
            DueAt = now.AddDays(DueDays),
        };
        r.Id = await _privacy.InsertPrivacyRequestAsync(r, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "privacy.request", $"member:{member.Id}", null, new { r.Id, r.Type }, now)], ct).ConfigureAwait(false);
        return r;
    }

    /// <summary>คำขอของสมาชิก</summary>
    internal Task<IReadOnlyList<PrivacyRequestDTO>> ListByMemberAsync(long memberId, CancellationToken ct) => _privacy.ListPrivacyRequestsAsync(memberId, null, ct);

    /// <summary>
    /// ปิดคำขอ: Erase = ลบข้อมูลส่วนบุคคล (คงประวัติธุรกรรมแบบไม่ระบุตัวตน) แล้วปิดบัญชี, Close = ปิดบัญชี
    /// </summary>
    public async Task<PrivacyRequestDTO> CompleteAsync(MembershipCaller caller, long id, CompletePrivacyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.PrivacyManage);
        ValidationException.ThrowIf(request.Outcome == PrivacyRequestStatus.Open, "outcome", "Outcome must be Completed or Rejected.");
        PrivacyRequestDTO r = await _privacy.GetPrivacyRequestAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Privacy request not found.");
        if (r.Status != PrivacyRequestStatus.Open)
        {
            throw MembershipException.InvalidTransition(r.Status, request.Outcome);
        }

        DateTimeOffset now = _clock.GetUtcNow();
        if (request.Outcome == PrivacyRequestStatus.Completed && r.Type is PrivacyRequestType.Erase or PrivacyRequestType.Close)
        {
            MemberEntity m = await _members.GetAsync(r.MemberId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Member not found.");
            string before = m.Status.ToString();
            m.Status = MemberStatus.Closed;
            m.StatusReason = $"privacy:{r.Type}";
            m.AuthSubject = null;
            m.MarketingConsent = false;
            m.MarketingChannels = null;
            m.UpdatedAt = now;
            if (r.Type == PrivacyRequestType.Erase)
            {
                m.FirstName = "ลบข้อมูลแล้ว";
                m.LastName = string.Empty;
                m.PhoneEnvelope = m.PhoneHash = m.PhoneMasked = null;
                m.EmailEnvelope = m.EmailHash = m.EmailMasked = null;
                m.BirthDateEnvelope = m.BirthMonthDay = null;
                m.BirthYear = null;
                m.Province = null;
                m.Gender = default;
            }

            if (!await _members.UpdateAsync(m, ct).ConfigureAwait(false))
            {
                throw MembershipException.VersionConflict();
            }

            await _outbox.EnqueueAsync([MembershipEvents.Outbox("member.updated", new { memberId = m.Id, m.MemberNo, status = m.Status, privacy = r.Type }, now)], ct).ConfigureAwait(false);
            await _audit.AppendAsync([MembershipEvents.Audit(caller, r.Type == PrivacyRequestType.Erase ? "member.erase" : "member.close", $"member:{m.Id}", new { status = before }, new { m.Status, requestId = r.Id }, now)], ct).ConfigureAwait(false);
        }

        r.Status = request.Outcome;
        r.Outcome = request.Outcome.ToString();
        r.Note = request.Note.Trim();
        r.CompletedAt = now;
        r.CompletedBy = caller.Actor;
        await _privacy.UpdatePrivacyRequestAsync(r, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "privacy.complete", $"privacy_request:{r.Id}", null, new { r.Type, r.Status }, now)], ct).ConfigureAwait(false);
        return r;
    }
}
