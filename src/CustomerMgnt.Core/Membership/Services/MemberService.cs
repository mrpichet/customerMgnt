using System.Globalization;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>
/// สมาชิก (MEM-001..005): สมัคร ยืนยัน OTP แก้ไข สถานะ ผูก Customer ดูข้อมูลส่วนบุคคล รวมบัญชี
/// ข้อมูลส่วนบุคคลเก็บแบบเข้ารหัส + blind index และส่งออกแบบ mask เว้นแต่มีสิทธิ์ member.pii (บันทึก audit ทุกครั้ง)
/// </summary>
public sealed class MemberService : IApprovalHandler
{
    internal const string PhoneContext = "member.phone";
    internal const string EmailContext = "member.email";
    internal const string BirthContext = "member.birth_date";

    private readonly IMemberDAO _members;
    private readonly ITierDAO _tiers;
    private readonly IPointDAO _points;
    private readonly ICouponDAO _coupons;
    private readonly ICheckoutDAO _checkout;
    private readonly IMembershipSettingsDAO _settings;
    private readonly IMembershipAuditDAO _audit;
    private readonly IMembershipUnitOfWork _uow;
    private readonly PointLedgerService _ledger;
    private readonly ApprovalService _approvals;
    private readonly OtpService _otp;
    private readonly PrivacyService _privacy;
    private readonly CustomerService _customers;
    private readonly MembershipRulesProvider _rules;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public MemberService(IMemberDAO members, ITierDAO tiers, IPointDAO points, ICouponDAO coupons, ICheckoutDAO checkout, IMembershipSettingsDAO settings,
        IMembershipAuditDAO audit, IMembershipUnitOfWork uow, PointLedgerService ledger, ApprovalService approvals, OtpService otp, PrivacyService privacy,
        CustomerService customers, MembershipRulesProvider rules, TimeProvider clock)
    {
        _members = members;
        _tiers = tiers;
        _points = points;
        _coupons = coupons;
        _checkout = checkout;
        _settings = settings;
        _audit = audit;
        _uow = uow;
        _ledger = ledger;
        _approvals = approvals;
        _otp = otp;
        _privacy = privacy;
        _customers = customers;
        _rules = rules;
        _clock = clock;
    }

    /// <inheritdoc />
    public ApprovalType Type => ApprovalType.Merge;

    // ------------------------------------------------------------------ ค้นหา / ดู

    /// <summary>ค้นหาสมาชิก</summary>
    public async Task<PagedResultDTO<MemberSummaryDTO>> SearchAsync(MembershipCaller caller, MemberSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(criteria);
        caller.Require(MembershipPermissions.MemberRead);
        criteria.PhoneHash = string.IsNullOrWhiteSpace(criteria.Phone) ? null : _otp.PhoneHash(MemberRules.NormalizePhone(criteria.Phone));
        criteria.EmailHash = MemberRules.NormalizeEmail(criteria.Email) is string e ? EmailHash(e) : null;
        PagedResultDTO<MemberEntity> page = await _members.SearchAsync(criteria, ct).ConfigureAwait(false);
        return new PagedResultDTO<MemberSummaryDTO>([.. page.Items.Select(ToSummary)], page.Total, page.Page, page.PageSize);
    }

    /// <summary>สมาชิกตามรหัส</summary>
    public async Task<MemberDTO> GetAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.MemberRead);
        return await ToDtoAsync(await LoadAsync(id, ct).ConfigureAwait(false), unmasked: false, ct).ConfigureAwait(false);
    }

    /// <summary>สมาชิกตามเลขสมาชิก</summary>
    public async Task<MemberDTO> GetByNoAsync(MembershipCaller caller, string memberNo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.MemberRead);
        MemberEntity m = await _members.GetByNoAsync(memberNo.Trim().ToUpperInvariant(), ct).ConfigureAwait(false) ?? throw new NotFoundException("Member not found.");
        return await ToDtoAsync(m, unmasked: false, ct).ConfigureAwait(false);
    }

    /// <summary>ดูข้อมูลส่วนบุคคลเต็ม (บันทึก audit)</summary>
    public async Task<MemberPiiDTO> RevealAsync(MembershipCaller caller, long id, RevealPiiRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.MemberPii);
        MemberEntity m = await LoadAsync(id, ct).ConfigureAwait(false);
        var fields = new HashSet<string>(request.Fields, StringComparer.OrdinalIgnoreCase);
        var pii = new MemberPiiDTO
        {
            MemberId = m.Id,
            Phone = fields.Contains("phone") && m.PhoneEnvelope is not null ? _privacy.Open(m.PhoneEnvelope, PhoneContext) : null,
            Email = fields.Contains("email") && m.EmailEnvelope is not null ? _privacy.Open(m.EmailEnvelope, EmailContext) : null,
            BirthDate = fields.Contains("birthDate") && m.BirthDateEnvelope is not null ? DateOnly.ParseExact(_privacy.Open(m.BirthDateEnvelope, BirthContext), "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        };
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "member.pii.reveal", $"member:{m.Id}", null, new { fields = request.Fields, request.Reason }, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        return pii;
    }

    /// <summary>ประวัติ Tier</summary>
    public async Task<IReadOnlyList<TierHistoryDTO>> GetTierHistoryAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.MemberRead);
        await LoadAsync(id, ct).ConfigureAwait(false);
        return await _members.ListTierHistoryAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>ประวัติ consent</summary>
    public async Task<IReadOnlyList<MemberConsentDTO>> GetConsentsAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.MemberRead);
        await LoadAsync(id, ct).ConfigureAwait(false);
        return await _members.ListConsentsAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>audit ของสมาชิก</summary>
    public async Task<PagedResultDTO<MembershipAuditDTO>> GetAuditAsync(MembershipCaller caller, long id, int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.AuditRead);
        return await _audit.SearchAsync(new AuditSearchCriteria { Target = $"member:{id}", Page = page, PageSize = pageSize }, ct).ConfigureAwait(false);
    }

    /// <summary>ตรวจเบอร์/อีเมลซ้ำ</summary>
    public async Task<DuplicateCheckDTO> CheckDuplicateAsync(MembershipCaller caller, DuplicateCheckRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.MemberWrite);
        MemberEntity? hit = await FindDuplicateAsync(
            string.IsNullOrWhiteSpace(request.Phone) ? null : MemberRules.NormalizePhone(request.Phone),
            MemberRules.NormalizeEmail(request.Email), excludeId: null, ct).ConfigureAwait(false);
        return hit is null
            ? new DuplicateCheckDTO()
            : new DuplicateCheckDTO { Exists = true, MemberId = hit.Id, MemberNoMasked = hit.MemberNo.Length > 4 ? hit.MemberNo[..2] + new string('x', hit.MemberNo.Length - 4) + hit.MemberNo[^2..] : hit.MemberNo };
    }

    // ------------------------------------------------------------------ สมัคร / ยืนยัน

    /// <summary>เจ้าหน้าที่สมัครสมาชิกแทน → PendingVerification + ส่ง OTP</summary>
    public async Task<MemberRegistrationResultDTO> RegisterAsync(MembershipCaller caller, RegisterMemberRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.MemberWrite);
        (MemberEntity created, string phone) = await CreateMemberAsync(caller, request, RegisteredChannel.BackOffice, authSubject: null, activate: false, ct).ConfigureAwait(false);
        OtpChallengeDTO challenge = await _otp.IssueAsync($"member:{created.Id}", created.Id, phone, null, ct).ConfigureAwait(false);
        return new MemberRegistrationResultDTO { Member = await ToDtoAsync(created, false, ct).ConfigureAwait(false), Challenge = challenge };
    }

    /// <summary>ส่ง OTP ใหม่ให้สมาชิกที่รอยืนยัน</summary>
    public async Task<OtpChallengeDTO> ResendOtpAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.MemberWrite);
        MemberEntity m = await LoadAsync(id, ct).ConfigureAwait(false);
        if (m.Status != MemberStatus.PendingVerification || m.PhoneEnvelope is null)
        {
            throw MembershipException.Conflict("invalid_transition", "The member is already verified.");
        }

        return await _otp.IssueAsync($"member:{m.Id}", m.Id, _privacy.Open(m.PhoneEnvelope, PhoneContext), null, ct).ConfigureAwait(false);
    }

    /// <summary>ยืนยัน OTP (สมัครใหม่ หรือเปลี่ยนเบอร์)</summary>
    public async Task<MemberDTO> VerifyAsync(MembershipCaller caller, long id, VerifyOtpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.MemberWrite);
        MemberEntity m = await ApplyVerifiedChallengeAsync(caller, id, request, ct).ConfigureAwait(false);
        return await ToDtoAsync(m, false, ct).ConfigureAwait(false);
    }

    /// <summary>ยืนยันสมาชิกแทน OTP (ตรวจบัตรต่อหน้า)</summary>
    public async Task<MemberDTO> VerifyOverrideAsync(MembershipCaller caller, long id, VerifyOverrideRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.MemberVerifyOverride);
        MemberEntity m = await LoadAsync(id, ct).ConfigureAwait(false);
        if (m.Status != MemberStatus.PendingVerification)
        {
            throw MembershipException.InvalidTransition(m.Status, MemberStatus.Active);
        }

        m = await ActivateAsync(caller, m, "verify_override", new { request.Reason, request.DocumentRef }, ct).ConfigureAwait(false);
        return await ToDtoAsync(m, false, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ แก้ไข

    /// <summary>แก้ไขข้อมูล — เปลี่ยนเบอร์ต้องยืนยัน OTP ของเบอร์ใหม่ก่อน</summary>
    public async Task<UpdateMemberResultDTO> UpdateAsync(MembershipCaller caller, long id, UpdateMemberRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.MemberWrite);
        return await UpdateCoreAsync(caller, await LoadAsync(id, ct).ConfigureAwait(false), request, unmasked: false, ct).ConfigureAwait(false);
    }

    /// <summary>เปลี่ยนสถานะ</summary>
    public async Task<MemberDTO> ChangeStatusAsync(MembershipCaller caller, long id, ChangeMemberStatusRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.MemberStatus);
        MembershipEvents.RequireVersion(request.Version);
        MemberEntity m = await LoadAsync(id, ct).ConfigureAwait(false);
        if (m.Version != request.Version)
        {
            throw MembershipException.VersionConflict();
        }

        if (request.Status == MemberStatus.Active && m.Status == MemberStatus.PendingVerification)
        {
            throw MembershipException.Conflict("invalid_transition", "Use OTP verification or verify-override to activate a member.");
        }

        if (!MemberRules.CanTransition(m.Status, request.Status))
        {
            throw MembershipException.InvalidTransition(m.Status, request.Status);
        }

        MemberStatus before = m.Status;
        m.Status = request.Status;
        m.StatusReason = request.Reason;
        m = await SaveAsync(caller, m, "member.status", new { status = before }, new { status = m.Status, request.Reason }, ct).ConfigureAwait(false);
        await _uow.CommitAsync(OutboxOnly("member.updated", new { memberId = m.Id, m.MemberNo, status = m.Status }), ct).ConfigureAwait(false);
        return await ToDtoAsync(m, false, ct).ConfigureAwait(false);
    }

    /// <summary>ผูก Customer ที่มีอยู่ หรือให้ระบบสร้าง Customer บุคคลใหม่ (customerCode = null)</summary>
    public async Task<MemberDTO> LinkCustomerAsync(MembershipCaller caller, long id, LinkCustomerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.MemberWrite);
        MembershipEvents.RequireVersion(request.Version);
        MemberEntity m = await LoadAsync(id, ct).ConfigureAwait(false);
        if (m.Version != request.Version)
        {
            throw MembershipException.VersionConflict();
        }

        if (request.CustomerCode is null)
        {
            // ให้ worker สร้าง Customer ใน CustomerMgnt (ผ่าน outbox — ทำซ้ำได้ปลอดภัย)
            await _uow.CommitAsync(OutboxOnly("customer.create", new { memberId = m.Id }), ct).ConfigureAwait(false);
            return await ToDtoAsync(m, false, ct).ConfigureAwait(false);
        }

        string code = await RequireCustomerAsync(request.CustomerCode, ct).ConfigureAwait(false);
        string? before = m.CustomerCode;
        m.CustomerCode = code;
        m = await SaveAsync(caller, m, "member.customer_link", new { customerCode = before }, new { customerCode = code }, ct).ConfigureAwait(false);
        return await ToDtoAsync(m, false, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ รวมบัญชี

    /// <summary>ตัวอย่างผลการรวมบัญชี</summary>
    public async Task<MergePreviewDTO> PreviewMergeAsync(MembershipCaller caller, long primaryId, long secondaryId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.MemberMerge);
        ValidationException.ThrowIf(primaryId == secondaryId, "secondaryId", "Choose two different members.");
        MemberEntity p = await LoadAsync(primaryId, ct).ConfigureAwait(false);
        MemberEntity s = await LoadAsync(secondaryId, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        PointAccountDTO sa = await _points.GetAccountAsync(s.Id, ct).ConfigureAwait(false);
        IReadOnlyList<CouponDTO> coupons = await _coupons.ListByMemberAsync(s.Id, CouponStatus.Available, ct).ConfigureAwait(false);
        PagedResultDTO<SalesTransactionEntity> txns = await _checkout.SearchTransactionsAsync(new TransactionSearchCriteria { MemberId = s.Id, PageSize = 1 }, ct).ConfigureAwait(false);
        var preview = new MergePreviewDTO
        {
            Primary = await ToDtoAsync(p, false, ct).ConfigureAwait(false),
            Secondary = await ToDtoAsync(s, false, ct).ConfigureAwait(false),
            PointsToMove = Math.Max(sa.LotBalance - sa.Debt, 0),
            CouponsToMove = coupons.Count(c => c.ExpiresAt > now),
            TransactionsToMove = (int)txns.Total,
        };
        if (s.Status == MemberStatus.Closed || p.Status == MemberStatus.Closed)
        {
            preview.Warnings.Add("A closed member cannot be merged.");
        }

        if (sa.Reserved > 0)
        {
            preview.Warnings.Add("The secondary member has points reserved by an open checkout.");
        }

        if (s.AuthSubject is not null && p.AuthSubject is not null)
        {
            preview.Warnings.Add("Both members have a Portal login; the secondary login will be unlinked.");
        }

        return preview;
    }

    /// <summary>ยื่นรวมบัญชี (ต้องอนุมัติ)</summary>
    public async Task<ApprovalRequestDTO> RequestMergeAsync(MembershipCaller caller, MergeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        MergePreviewDTO preview = await PreviewMergeAsync(caller, request.PrimaryId, request.SecondaryId, ct).ConfigureAwait(false);
        ValidationException.ThrowIf(preview.Warnings.Any(w => w.StartsWith("A closed", StringComparison.Ordinal)), "secondaryId", "A closed member cannot be merged.");
        return await _approvals.SubmitAsync(caller, ApprovalType.Merge, request.PrimaryId,
            $"รวมบัญชี {preview.Secondary.MemberNo} เข้า {preview.Primary.MemberNo}", 1, preview.Secondary, preview.Primary, request, request.Reason, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task OnApprovedAsync(MembershipCaller approver, ApprovalRequestDTO request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        MergeRequest merge = MembershipJson.Read<MergeRequest>(request.PayloadJson);
        await MembershipEvents.RetryOnConflictAsync(async () =>
        {
            await MergeAsync(approver, merge, request.Id, ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task OnRejectedAsync(MembershipCaller actor, ApprovalRequestDTO request, CancellationToken ct) => Task.CompletedTask;

    private async Task MergeAsync(MembershipCaller approver, MergeRequest merge, long approvalId, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        MemberEntity p = await LoadAsync(merge.PrimaryId, ct).ConfigureAwait(false);
        MemberEntity s = await LoadAsync(merge.SecondaryId, ct).ConfigureAwait(false);
        if (s.Status == MemberStatus.Closed)
        {
            return; // รวมไปแล้ว
        }

        var cs = new MembershipChangeSet();

        // Step 1: ย้ายคะแนนทีละ lot (คงวันหมดอายุเดิม) และ debt
        PointPosting sp = await _ledger.OpenAsync(s.Id, now, ct).ConfigureAwait(false);
        PointPosting pp = await _ledger.OpenAsync(p.Id, now, ct).ConfigureAwait(false);
        if (sp.Account.Reserved > 0)
        {
            throw MembershipException.Conflict("request_in_progress", "The secondary member has an open checkout. Try again later.");
        }

        foreach (PointLotDTO lot in sp.Lots.Where(l => l.Remaining > 0 && l.ExpiresAt > now).ToList())
        {
            int pts = lot.Remaining;
            string key = $"merge:{approvalId}:lot:{lot.Id}";
            PointLedgerService.Debit(sp, cs, pts, new LedgerContext(LedgerType.Adjustment, key + ":out", approver.Actor) { Reference = $"merge:{p.MemberNo}", Reason = merge.Reason }, now, allowDebt: false);
            PointLedgerService.Credit(pp, cs, pts, new LedgerContext(LedgerType.Adjustment, key + ":in", approver.Actor) { Reference = $"merge:{s.MemberNo}", Reason = merge.Reason }, now, lot.ExpiresAt, countLifetime: false);
        }

        pp.Account.Debt += sp.Account.Debt;
        pp.Account.LifetimeEarned += sp.Account.LifetimeEarned;
        sp.Account.Debt = 0;
        PointLedgerService.Finish(sp, cs);
        PointLedgerService.Finish(pp, cs);

        // Step 2: ย้ายคูปองที่ยังใช้ได้
        foreach (CouponDTO c in await _coupons.ListByMemberAsync(s.Id, CouponStatus.Available, ct).ConfigureAwait(false))
        {
            CouponDTO moved = c.Clone();
            moved.MemberId = p.Id;
            cs.Coupons.Add(new CouponChange(moved, c.Version));
            cs.CouponEvents.Add(new CouponEventDTO { Code = c.Code, FromStatus = c.Status, ToStatus = c.Status, Reference = $"merge:{p.MemberNo}", Actor = approver.Actor, At = now });
        }

        // Step 3: field ที่เลือกจาก secondary + ปิด secondary
        var take = new HashSet<string>(merge.TakeFromSecondary, StringComparer.OrdinalIgnoreCase);
        if (take.Contains("firstName")) p.FirstName = s.FirstName;
        if (take.Contains("lastName")) p.LastName = s.LastName;
        if (take.Contains("email")) (p.EmailEnvelope, p.EmailHash, p.EmailMasked) = (s.EmailEnvelope, s.EmailHash, s.EmailMasked);
        if (take.Contains("birthDate")) (p.BirthDateEnvelope, p.BirthMonthDay, p.BirthYear) = (s.BirthDateEnvelope, s.BirthMonthDay, s.BirthYear);
        if (take.Contains("province")) p.Province = s.Province;
        p.CustomerCode ??= s.CustomerCode;
        p.AuthSubject ??= s.AuthSubject;
        p.UpdatedAt = now;
        s.Status = MemberStatus.Closed;
        s.StatusReason = $"merged into {p.MemberNo}";
        s.AuthSubject = null;
        s.UpdatedAt = now;
        cs.MemberUpdates.Add(p);
        cs.MemberUpdates.Add(s);
        cs.Audit.Add(MembershipEvents.Audit(approver, "member.merge", $"member:{p.Id}", new { secondary = s.MemberNo }, new { primary = p.MemberNo, approvalId }, now));
        cs.Outbox.Add(MembershipEvents.Outbox("member.updated", new { memberId = p.Id, p.MemberNo, mergedFrom = s.MemberNo }, now));
        await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ ใช้ร่วมกับ Portal

    /// <summary>สร้างสมาชิก (ตรวจซ้ำ, consent, Tier เริ่มต้น, เข้ารหัสข้อมูลส่วนบุคคล)</summary>
    internal async Task<(MemberEntity Member, string Phone)> CreateMemberAsync(MembershipCaller caller, RegisterMemberRequest request, RegisteredChannel channel,
        string? authSubject, bool activate, CancellationToken ct)
    {
        // Step 1: normalize + ตรวจซ้ำ
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        string phone = MemberRules.NormalizePhone(request.Phone);
        string? email = MemberRules.NormalizeEmail(request.Email);
        if (await FindDuplicateAsync(phone, email, excludeId: null, ct, rules).ConfigureAwait(false) is not null)
        {
            throw MembershipException.Conflict("duplicate_member", "A member with this phone or email already exists.");
        }

        // Step 2: consent ฉบับที่เผยแพร่อยู่ (บังคับเมื่อมีฉบับเผยแพร่)
        ConsentVersionDTO? current = await CurrentConsentAsync(ct).ConfigureAwait(false);
        if (current is not null)
        {
            ValidationException.ThrowIf(request.ConsentVersionId != current.Id, "consentVersionId", "Please accept the current privacy notice.");
        }

        string? customerCode = request.CustomerCode is null ? null : await RequireCustomerAsync(request.CustomerCode, ct).ConfigureAwait(false);
        TierDTO? tier = TierRules.Initial(await _tiers.ListAsync(ct).ConfigureAwait(false));
        DateTimeOffset now = _clock.GetUtcNow();
        ValidationException.ThrowIf(request.BirthDate is DateOnly bd && (bd > DateOnly.FromDateTime(now.UtcDateTime) || bd.Year < 1900), "birthDate", "Invalid birth date.");

        // Step 3: entity
        var m = new MemberEntity
        {
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Gender = request.Gender,
            Province = request.Province?.Trim(),
            Status = activate ? MemberStatus.Active : MemberStatus.PendingVerification,
            RegisteredChannel = channel,
            TierCode = tier?.Code,
            TierSince = tier is null ? null : now,
            TierReviewAt = tier is null ? null : now.AddMonths(rules.TierWindowMonths),
            CustomerCode = customerCode,
            AuthSubject = authSubject,
            MarketingConsent = request.MarketingConsent,
            MarketingChannels = request.MarketingConsent ? string.Join(',', (request.MarketingChannels is { Count: > 0 } ch ? ch : [MarketingChannel.Sms, MarketingChannel.Email, MarketingChannel.Line]).Distinct()) : null,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        SetPhone(m, phone);
        SetEmail(m, email);
        SetBirthDate(m, request.BirthDate);
        MemberConsentDTO? consent = current is null ? null : new MemberConsentDTO
        {
            ConsentVersionId = current.Id,
            Accepted = true,
            MarketingConsent = request.MarketingConsent,
            MarketingChannels = [.. ParseChannels(m.MarketingChannels)],
            Channel = channel.ToString(),
            At = now,
        };

        // Step 4: บันทึกพร้อม outbox
        var outbox = new List<OutboxMessageDTO> { MembershipEvents.Outbox("member.registered", new { channel, tier = tier?.Code }, now) };
        if (activate && customerCode is null)
        {
            outbox.Add(MembershipEvents.Outbox("customer.create", new { }, now));
        }

        MemberEntity created = await _members.InsertAsync(m, consent, outbox, ct).ConfigureAwait(false);
        var audit = new List<MembershipAuditDTO> { MembershipEvents.Audit(caller, "member.register", $"member:{created.Id}", null, MaskedView(created), now) };
        if (tier is not null)
        {
            var cs = new MembershipChangeSet();
            cs.TierHistory.Add(new TierHistoryDTO { MemberId = created.Id, ToTier = tier.Code, Reason = "Initial", ChangedAt = now });
            cs.Audit.AddRange(audit);
            await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
        }
        else
        {
            await _audit.AppendAsync(audit, ct).ConfigureAwait(false);
        }

        return (created, phone);
    }

    /// <summary>ยืนยัน OTP ของสมาชิก (purpose member:{id} หรือ phone-change:{id})</summary>
    internal async Task<MemberEntity> ApplyVerifiedChallengeAsync(MembershipCaller caller, long id, VerifyOtpRequest request, CancellationToken ct)
    {
        MemberEntity m = await LoadAsync(id, ct).ConfigureAwait(false);
        OtpChallengeEntity c = await _otp.VerifyAsync(request.ChallengeId, request.Code, string.Empty, ct).ConfigureAwait(false);
        if (c.Purpose == $"member:{m.Id}")
        {
            if (m.Status != MemberStatus.PendingVerification)
            {
                return m;
            }

            return await ActivateAsync(caller, m, "otp", null, ct).ConfigureAwait(false);
        }

        if (c.Purpose == $"phone-change:{m.Id}" && c.PayloadJson is not null)
        {
            PhoneChangePayload payload = MembershipJson.Read<PhoneChangePayload>(c.PayloadJson);
            string phone = _privacy.Open(payload.PhoneEnvelope, PhoneContext);
            if (await FindDuplicateAsync(phone, null, m.Id, ct).ConfigureAwait(false) is not null)
            {
                throw MembershipException.Conflict("duplicate_member", "A member with this phone already exists.");
            }

            string? before = m.PhoneMasked;
            SetPhone(m, phone);
            return await SaveAsync(caller, m, "member.phone_change", new { phone = before }, new { phone = m.PhoneMasked }, ct).ConfigureAwait(false);
        }

        throw MembershipException.Unprocessable("otp_invalid", "The OTP is invalid.");
    }

    /// <summary>แก้ไขข้อมูล (เจ้าหน้าที่ / สมาชิก)</summary>
    internal async Task<UpdateMemberResultDTO> UpdateCoreAsync(MembershipCaller caller, MemberEntity m, UpdateMemberRequest request, bool unmasked, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        MembershipEvents.RequireVersion(request.Version);
        if (m.Version != request.Version)
        {
            throw MembershipException.VersionConflict();
        }

        if (m.Status == MemberStatus.Closed)
        {
            throw MembershipException.Conflict("invalid_transition", "A closed member cannot be changed.");
        }

        object before = MaskedView(m);
        if (request.FirstName is not null) m.FirstName = request.FirstName.Trim();
        if (request.LastName is not null) m.LastName = request.LastName.Trim();
        if (request.Gender is Gender g) m.Gender = g;
        if (request.Province is not null) m.Province = request.Province.Trim();
        if (request.BirthDate is not null) SetBirthDate(m, request.BirthDate);
        if (request.Email is not null)
        {
            string? email = MemberRules.NormalizeEmail(request.Email);
            if (email is not null && await FindDuplicateAsync(null, email, m.Id, ct).ConfigureAwait(false) is not null)
            {
                throw MembershipException.Conflict("duplicate_member", "A member with this email already exists.");
            }

            SetEmail(m, email);
        }

        // เปลี่ยนเบอร์: ส่ง OTP ไปเบอร์ใหม่ ยังไม่เปลี่ยนจนกว่าจะยืนยัน
        OtpChallengeDTO? challenge = null;
        if (request.Phone is not null)
        {
            string phone = MemberRules.NormalizePhone(request.Phone);
            if (_otp.PhoneHash(phone) != m.PhoneHash)
            {
                if (await FindDuplicateAsync(phone, null, m.Id, ct).ConfigureAwait(false) is not null)
                {
                    throw MembershipException.Conflict("duplicate_member", "A member with this phone already exists.");
                }

                challenge = await _otp.IssueAsync($"phone-change:{m.Id}", m.Id, phone, MembershipJson.Write(new PhoneChangePayload { PhoneEnvelope = _privacy.Seal(phone, PhoneContext) }), ct).ConfigureAwait(false);
            }
        }

        MemberEntity saved = await SaveAsync(caller, m, "member.update", before, MaskedView(m), ct).ConfigureAwait(false);
        await _uow.CommitAsync(OutboxOnly("member.updated", new { memberId = saved.Id, saved.MemberNo }), ct).ConfigureAwait(false);
        return new UpdateMemberResultDTO { Member = await ToDtoAsync(saved, unmasked, ct).ConfigureAwait(false), Challenge = challenge };
    }

    /// <summary>แปลงเป็น DTO (unmasked = ข้อมูลของตัวเองใน Portal)</summary>
    internal async Task<MemberDTO> ToDtoAsync(MemberEntity m, bool unmasked, CancellationToken ct)
    {
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        IReadOnlyList<TierDTO> tiers = await _tiers.ListAsync(ct).ConfigureAwait(false);
        TierDTO? tier = tiers.FirstOrDefault(t => string.Equals(t.Code, m.TierCode, StringComparison.OrdinalIgnoreCase));
        PointBalanceDTO balance = await _ledger.GetBalanceAsync(m.Id, _clock.GetUtcNow(), rules.ExpiryNotifyDays, ct).ConfigureAwait(false);
        return new MemberDTO
        {
            Id = m.Id,
            MemberNo = m.MemberNo,
            FirstName = m.FirstName,
            LastName = m.LastName,
            DisplayName = $"{m.FirstName} {m.LastName}".Trim(),
            PhoneMasked = unmasked && m.PhoneEnvelope is not null ? _privacy.Open(m.PhoneEnvelope, PhoneContext) : m.PhoneMasked,
            EmailMasked = unmasked && m.EmailEnvelope is not null ? _privacy.Open(m.EmailEnvelope, EmailContext) : m.EmailMasked,
            BirthDateMasked = unmasked && m.BirthDateEnvelope is not null ? _privacy.Open(m.BirthDateEnvelope, BirthContext) : m.BirthMonthDay is null ? null : "xxxx-" + m.BirthMonthDay,
            Gender = m.Gender,
            Province = m.Province,
            Status = m.Status,
            StatusReason = m.StatusReason,
            RegisteredChannel = m.RegisteredChannel,
            Tier = m.TierCode is null ? null : new MemberTierInfoDTO { Code = m.TierCode, Name = tier?.Name ?? m.TierCode, Since = m.TierSince, ReviewAt = m.TierReviewAt },
            Points = balance,
            CustomerCode = m.CustomerCode,
            HasPortalAccount = m.AuthSubject is not null,
            MarketingConsent = m.MarketingConsent,
            CreatedAt = m.CreatedAt,
            UpdatedAt = m.UpdatedAt,
            Version = m.Version,
        };
    }

    /// <summary>แถวผลค้นหา</summary>
    internal static MemberSummaryDTO ToSummary(MemberEntity m) => new()
    {
        Id = m.Id,
        MemberNo = m.MemberNo,
        DisplayName = MemberRules.ShortName(m.FirstName, m.LastName),
        PhoneMasked = m.PhoneMasked,
        Status = m.Status,
        TierCode = m.TierCode,
        PointsAvailable = m.PointsAvailable,
        CustomerCode = m.CustomerCode,
        CreatedAt = m.CreatedAt,
    };

    /// <summary>ข้อมูลที่ mask แล้วสำหรับ audit</summary>
    internal static object MaskedView(MemberEntity m) => new
    {
        m.MemberNo, m.FirstName, lastName = MemberRules.ShortName(string.Empty, m.LastName).Trim(), phone = m.PhoneMasked, email = m.EmailMasked,
        m.Gender, m.Province, m.Status, m.TierCode, m.CustomerCode, m.MarketingConsent,
    };

    /// <summary>โหลดสมาชิก (ไม่พบ = 404)</summary>
    internal async Task<MemberEntity> LoadAsync(long id, CancellationToken ct) =>
        await _members.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Member not found.");

    /// <summary>ฉบับ consent ที่มีผลปัจจุบัน</summary>
    internal async Task<ConsentVersionDTO?> CurrentConsentAsync(CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        return (await _settings.ListConsentVersionsAsync(ct).ConfigureAwait(false))
            .Where(v => v.IsPublished && v.EffectiveAt <= now).OrderByDescending(v => v.EffectiveAt).ThenByDescending(v => v.Id).FirstOrDefault();
    }

    /// <summary>หาสมาชิกที่ยังไม่ Closed ที่ใช้เบอร์/อีเมลนี้</summary>
    internal async Task<MemberEntity?> FindDuplicateAsync(string? phone, string? email, long? excludeId, CancellationToken ct, MembershipRulesDTO? rules = null)
    {
        rules ??= await _rules.GetAsync(ct).ConfigureAwait(false);
        if (phone is not null && rules.UniquePhone)
        {
            MemberEntity? hit = (await _members.FindByPhoneHashAsync(_otp.PhoneHash(phone), ct).ConfigureAwait(false)).FirstOrDefault(m => m.Status != MemberStatus.Closed && m.Id != excludeId);
            if (hit is not null)
            {
                return hit;
            }
        }

        if (email is not null && rules.UniqueEmail)
        {
            return (await _members.FindByEmailHashAsync(EmailHash(email), ct).ConfigureAwait(false)).FirstOrDefault(m => m.Status != MemberStatus.Closed && m.Id != excludeId);
        }

        return null;
    }

    /// <summary>หาสมาชิกที่ยังไม่ Closed จากเบอร์ (Portal ใช้ผูกกับสมาชิกที่เจ้าหน้าที่สมัครให้)</summary>
    internal async Task<MemberEntity?> FindByPhoneAsync(string phone, CancellationToken ct) =>
        (await _members.FindByPhoneHashAsync(_otp.PhoneHash(phone), ct).ConfigureAwait(false)).FirstOrDefault(m => m.Status != MemberStatus.Closed);

    /// <summary>บันทึก (ตรวจ version) + audit</summary>
    internal async Task<MemberEntity> SaveAsync(MembershipCaller caller, MemberEntity m, string action, object? before, object? after, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        m.UpdatedAt = now;
        if (!await _members.UpdateAsync(m, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        m.Version++;
        await _audit.AppendAsync([MembershipEvents.Audit(caller, action, $"member:{m.Id}", before, after, now)], ct).ConfigureAwait(false);
        return m;
    }

    /// <summary>เปลี่ยนเป็น Active + สร้าง Customer (ถ้ายังไม่มี) ผ่าน outbox</summary>
    internal async Task<MemberEntity> ActivateAsync(MembershipCaller caller, MemberEntity m, string method, object? detail, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        m.Status = MemberStatus.Active;
        m.StatusReason = null;
        m.UpdatedAt = now;
        var cs = new MembershipChangeSet();
        cs.MemberUpdates.Add(m);
        cs.Audit.Add(MembershipEvents.Audit(caller, "member.activate", $"member:{m.Id}", new { status = MemberStatus.PendingVerification }, new { status = MemberStatus.Active, method, detail }, now));
        cs.Outbox.Add(MembershipEvents.Outbox("member.updated", new { memberId = m.Id, m.MemberNo, status = MemberStatus.Active }, now));
        if (m.CustomerCode is null)
        {
            cs.Outbox.Add(MembershipEvents.Outbox("customer.create", new { memberId = m.Id }, now));
        }

        await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
        m.Version++;
        return m;
    }

    /// <summary>Customer ต้องมีอยู่ใน CustomerMgnt</summary>
    private async Task<string> RequireCustomerAsync(string code, CancellationToken ct)
    {
        try
        {
            return (await _customers.GetByCodeAsync(UserContextDTO.System, code.Trim(), ct).ConfigureAwait(false)).Code;
        }
        catch (NotFoundException)
        {
            throw new ValidationException("Customer not found.", new Dictionary<string, string[]> { ["customerCode"] = ["Customer not found."] });
        }
    }

    private MembershipChangeSet OutboxOnly(string topic, object payload)
    {
        var cs = new MembershipChangeSet();
        cs.Outbox.Add(MembershipEvents.Outbox(topic, payload, _clock.GetUtcNow()));
        return cs;
    }

    private string EmailHash(string email) => _privacy.BlindIndex("email:" + email);

    private void SetPhone(MemberEntity m, string phone)
    {
        m.PhoneEnvelope = _privacy.Seal(phone, PhoneContext);
        m.PhoneHash = _otp.PhoneHash(phone);
        m.PhoneMasked = $"{phone[..3]}-xxx-{phone[^4..]}";
    }

    private void SetEmail(MemberEntity m, string? email)
    {
        m.EmailEnvelope = email is null ? null : _privacy.Seal(email, EmailContext);
        m.EmailHash = email is null ? null : EmailHash(email);
        m.EmailMasked = MemberRules.MaskEmail(email);
    }

    private void SetBirthDate(MemberEntity m, DateOnly? birth)
    {
        m.BirthDateEnvelope = birth is DateOnly b ? _privacy.Seal(b.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), BirthContext) : null;
        m.BirthMonthDay = birth?.ToString("MM-dd", CultureInfo.InvariantCulture);
        m.BirthYear = birth?.Year;
    }

    /// <summary>แปลงช่องทางที่เก็บเป็นข้อความ</summary>
    internal static IEnumerable<MarketingChannel> ParseChannels(string? csv) =>
        string.IsNullOrEmpty(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Enum.TryParse(s, out MarketingChannel c) ? (MarketingChannel?)c : null).OfType<MarketingChannel>();

    private sealed class PhoneChangePayload
    {
        public string PhoneEnvelope { get; set; } = string.Empty;
    }
}
