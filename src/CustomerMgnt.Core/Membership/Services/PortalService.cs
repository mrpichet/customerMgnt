using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Security;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>
/// Portal ของสมาชิก (Claude.MD §23): ใช้ token จาก AuthServer (Google / Facebook) — ผูก <c>sub</c> กับสมาชิกหลังยืนยัน OTP ของเบอร์
/// สมาชิกเห็นเฉพาะข้อมูลของตัวเอง ไม่ต้องมี permission
/// </summary>
public sealed class PortalService
{
    private const string RegistrationContext = "member.registration";

    private readonly IMemberDAO _members;
    private readonly IPointDAO _points;
    private readonly ICouponDAO _coupons;
    private readonly ICampaignDAO _campaigns;
    private readonly ISegmentDAO _segments;
    private readonly IMembershipSettingsDAO _settings;
    private readonly IMembershipAuditDAO _audit;
    private readonly IMembershipUnitOfWork _uow;
    private readonly MemberService _memberService;
    private readonly CouponService _couponService;
    private readonly CheckoutService _checkout;
    private readonly PrivacyRequestService _privacyRequests;
    private readonly PointLedgerService _ledger;
    private readonly OtpService _otp;
    private readonly PrivacyService _privacy;
    private readonly MembershipRulesProvider _rules;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public PortalService(IMemberDAO members, IPointDAO points, ICouponDAO coupons, ICampaignDAO campaigns, ISegmentDAO segments, IMembershipSettingsDAO settings,
        IMembershipAuditDAO audit, IMembershipUnitOfWork uow, MemberService memberService, CouponService couponService, CheckoutService checkout,
        PrivacyRequestService privacyRequests, PointLedgerService ledger, OtpService otp, PrivacyService privacy, MembershipRulesProvider rules, TimeProvider clock)
    {
        _members = members;
        _points = points;
        _coupons = coupons;
        _campaigns = campaigns;
        _segments = segments;
        _settings = settings;
        _audit = audit;
        _uow = uow;
        _memberService = memberService;
        _couponService = couponService;
        _checkout = checkout;
        _privacyRequests = privacyRequests;
        _ledger = ledger;
        _otp = otp;
        _privacy = privacy;
        _rules = rules;
        _clock = clock;
    }

    // ------------------------------------------------------------------ สมัคร / ข้อมูลของฉัน

    /// <summary>ข้อมูลของฉัน (ไม่ mask)</summary>
    public async Task<MemberDTO> GetMeAsync(MembershipCaller caller, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        return await _memberService.ToDtoAsync(m, unmasked: true, ct).ConfigureAwait(false);
    }

    /// <summary>เริ่มสมัคร: ตรวจข้อมูลแล้วส่ง OTP ไปยังเบอร์ — ข้อมูลที่กรอกเก็บแบบเข้ารหัสใน challenge จนกว่าจะยืนยัน</summary>
    public async Task<PortalRegistrationDTO> StartRegistrationAsync(MembershipCaller caller, RegisterMemberRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string sub = Subject(caller);
        if (await _members.GetBySubjectAsync(sub, ct).ConfigureAwait(false) is not null)
        {
            throw MembershipException.Conflict("duplicate_member", "This account is already linked to a member.");
        }

        request.CustomerCode = null;
        string phone = MemberRules.NormalizePhone(request.Phone);
        await EnsureLinkableAsync(phone, MemberRules.NormalizeEmail(request.Email), ct).ConfigureAwait(false);
        string sealedPayload = _privacy.Seal(MembershipJson.Write(request), RegistrationContext);
        OtpChallengeDTO c = await _otp.IssueAsync($"portal:{sub}", null, phone, sealedPayload, ct).ConfigureAwait(false);
        return new PortalRegistrationDTO { RegistrationId = c.ChallengeId, ChallengeId = c.ChallengeId, ExpiresAt = c.ExpiresAt };
    }

    /// <summary>ยืนยัน OTP แล้วผูกบัญชี — เบอร์ตรงสมาชิกที่เจ้าหน้าที่สมัครให้ = ผูกกับคนเดิม ไม่เช่นนั้นสร้างสมาชิกใหม่ (Active)</summary>
    public async Task<MemberDTO> CompleteRegistrationAsync(MembershipCaller caller, VerifyOtpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string sub = Subject(caller);
        if (await _members.GetBySubjectAsync(sub, ct).ConfigureAwait(false) is MemberEntity linked)
        {
            return await _memberService.ToDtoAsync(linked, unmasked: true, ct).ConfigureAwait(false);
        }

        OtpChallengeEntity c = await _otp.VerifyAsync(request.ChallengeId, request.Code, $"portal:{sub}", ct).ConfigureAwait(false);
        ValidationException.ThrowIf(!string.Equals(c.Purpose, $"portal:{sub}", StringComparison.Ordinal) || c.PayloadJson is null, "challengeId", "Invalid registration.");
        RegisterMemberRequest reg = MembershipJson.Read<RegisterMemberRequest>(_privacy.Open(c.PayloadJson!, RegistrationContext));
        string phone = MemberRules.NormalizePhone(reg.Phone);

        MemberEntity? existing = await _memberService.FindByPhoneAsync(phone, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.AuthSubject is not null)
            {
                throw MembershipException.Conflict("duplicate_member", "This phone is already linked to another account.");
            }

            existing.AuthSubject = sub;
            MemberEntity m = existing.Status == MemberStatus.PendingVerification
                ? await _memberService.ActivateAsync(caller, existing, "portal_otp", null, ct).ConfigureAwait(false)
                : await _memberService.SaveAsync(caller, existing, "member.portal_link", null, new { linked = true }, ct).ConfigureAwait(false);
            return await _memberService.ToDtoAsync(m, unmasked: true, ct).ConfigureAwait(false);
        }

        (MemberEntity created, _) = await _memberService.CreateMemberAsync(caller, reg, RegisteredChannel.Portal, sub, activate: true, ct).ConfigureAwait(false);
        return await _memberService.ToDtoAsync(created, unmasked: true, ct).ConfigureAwait(false);
    }

    /// <summary>แก้ข้อมูลตัวเอง (เปลี่ยนเบอร์ต้อง OTP)</summary>
    public async Task<UpdateMemberResultDTO> UpdateMeAsync(MembershipCaller caller, UpdateMemberRequest request, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        return await _memberService.UpdateCoreAsync(caller, m, request, unmasked: true, ct).ConfigureAwait(false);
    }

    /// <summary>ยืนยัน OTP เปลี่ยนเบอร์</summary>
    public async Task<MemberDTO> VerifyPhoneChangeAsync(MembershipCaller caller, VerifyOtpRequest request, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        m = await _memberService.ApplyVerifiedChallengeAsync(caller, m.Id, request, ct).ConfigureAwait(false);
        return await _memberService.ToDtoAsync(m, unmasked: true, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ คะแนน / ของรางวัล

    /// <summary>ยอดคะแนน</summary>
    public async Task<PointBalanceDTO> GetPointsAsync(MembershipCaller caller, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        return await _ledger.GetBalanceAsync(m.Id, _clock.GetUtcNow(), rules.ExpiryNotifyDays, ct).ConfigureAwait(false);
    }

    /// <summary>ประวัติคะแนน</summary>
    public async Task<PagedResultDTO<PointLedgerDTO>> GetLedgerAsync(MembershipCaller caller, int page, int pageSize, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        return await _points.SearchLedgerAsync(new LedgerCriteria { MemberId = m.Id, Page = Math.Max(page, 1), PageSize = Math.Clamp(pageSize, 1, 100) }, ct).ConfigureAwait(false);
    }

    /// <summary>ของรางวัลที่แลกด้วยคะแนนได้ (ชุดคูปองที่ตั้งราคาเป็นคะแนน ของแคมเปญที่ Active)</summary>
    public async Task<IReadOnlyList<RewardDTO>> ListRewardsAsync(MembershipCaller caller, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        PointAccountDTO account = await _points.GetAccountAsync(m.Id, ct).ConfigureAwait(false);
        List<(CouponBatchDTO Batch, CampaignDTO Campaign)> rewards = await RewardBatchesAsync(m, ct).ConfigureAwait(false);
        return [.. rewards.Select(r => new RewardDTO
        {
            Id = r.Batch.Id,
            Name = r.Batch.Name,
            Description = r.Campaign.Description ?? r.Campaign.Name,
            PointsCost = r.Batch.PointsCost!.Value,
            ExpiresAt = r.Batch.ExpiresAt,
            Affordable = account.Available >= r.Batch.PointsCost!.Value,
        }).OrderBy(r => r.PointsCost)];
    }

    /// <summary>แลกของรางวัล: ตัดคะแนน (FEFO) และออกคูปองใน transaction เดียว — key เดิม = ผลเดิม</summary>
    public async Task<CouponDTO> RedeemRewardAsync(MembershipCaller caller, long rewardId, string idempotencyKey, CancellationToken ct)
    {
        ValidationException.ThrowIf(string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100, "Idempotency-Key", "Idempotency-Key is required.");
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        if (m.Status != MemberStatus.Active)
        {
            throw MembershipException.Conflict("member_not_active", "The member is not active.");
        }

        (CouponBatchDTO batch, CampaignDTO campaign) = (await RewardBatchesAsync(m, ct).ConfigureAwait(false)).FirstOrDefault(r => r.Batch.Id == rewardId);
        if (batch is null)
        {
            throw new NotFoundException("Reward not found.");
        }

        string key = $"reward:{m.Id}:{idempotencyKey.Trim()}";
        return await MembershipEvents.RetryOnConflictAsync(async () =>
        {
            if (await _points.LedgerKeyExistsAsync(key, ct).ConfigureAwait(false))
            {
                return (await _coupons.ListByMemberAsync(m.Id, null, ct).ConfigureAwait(false)).Where(c => c.BatchId == batch.Id).OrderByDescending(c => c.CreatedAt).First();
            }

            if (batch.PerMember && (await _coupons.ListByMemberAsync(m.Id, null, ct).ConfigureAwait(false)).Any(c => c.BatchId == batch.Id && c.Status != CouponStatus.Cancelled))
            {
                throw MembershipException.Conflict("limit_reached", "This reward can be redeemed once per member.");
            }

            DateTimeOffset now = _clock.GetUtcNow();
            var cs = new MembershipChangeSet();
            PointPosting posting = await _ledger.OpenAsync(m.Id, now, ct).ConfigureAwait(false);
            var ctx = new LedgerContext(LedgerType.Redeem, key, caller.Actor) { Reference = $"reward:{batch.Id}", Reason = batch.Name, RequestId = caller.RequestId };
            PointLedgerService.Debit(posting, cs, batch.PointsCost!.Value, ctx, now, allowDebt: false);
            PointLedgerService.Finish(posting, cs);
            CouponDTO coupon = await _couponService.IssueToMemberAsync(batch, m.Id, caller.Actor, $"reward:{batch.Id}", cs, ct).ConfigureAwait(false);
            cs.Outbox.Add(MembershipEvents.Outbox("points.redeemed", new { memberId = m.Id, m.MemberNo, points = batch.PointsCost, reward = batch.Id }, now));
            cs.Audit.Add(MembershipEvents.Audit(caller, "reward.redeem", $"member:{m.Id}", null, new { reward = batch.Id, points = batch.PointsCost, coupon.Code }, now));
            await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
            coupon.CampaignName = campaign.Name;
            coupon.QrPayload = coupon.Code;
            return coupon;
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ คูปอง / โปรโมชัน / ประวัติซื้อ

    /// <summary>คูปองของฉัน</summary>
    public async Task<IReadOnlyList<CouponDTO>> ListCouponsAsync(MembershipCaller caller, CouponStatus? status, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        IReadOnlyList<CouponDTO> coupons = await _coupons.ListByMemberAsync(m.Id, status, ct).ConfigureAwait(false);
        Dictionary<long, CampaignDTO> campaigns = (await _campaigns.ListByIdsAsync([.. coupons.Select(c => c.CampaignId).Distinct()], ct).ConfigureAwait(false)).ToDictionary(c => c.Id);
        return [.. coupons.OrderBy(c => c.Status).ThenBy(c => c.ExpiresAt).Select(c =>
        {
            CouponDTO x = c.Clone();
            x.CampaignName = campaigns.TryGetValue(c.CampaignId, out CampaignDTO? cp) ? cp.Name : null;
            x.QrPayload = c.Status is CouponStatus.Available or CouponStatus.Reversed ? c.Code : null;
            x.ReservationId = null;
            return x;
        })];
    }

    /// <summary>โปรโมชันที่ฉันมีสิทธิ์ (Active และตรงกลุ่มเป้าหมาย)</summary>
    public async Task<IReadOnlyList<CampaignSummaryDTO>> ListPromotionsAsync(MembershipCaller caller, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        IReadOnlySet<long> segments = await _segments.ListSegmentIdsOfMemberAsync(m.Id, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        return [.. (await _campaigns.ListByStatusAsync([CampaignStatus.Active, CampaignStatus.Scheduled], ct).ConfigureAwait(false))
            .Where(c => c.EndAt > now && Targets(c, m, segments))
            .OrderBy(c => c.StartAt)
            .Select(c =>
            {
                CampaignSummaryDTO s = CampaignService.ToSummary(c);
                s.Budget = null;
                s.BudgetUsed = 0;
                s.Uses = 0;
                return s;
            })];
    }

    /// <summary>ประวัติซื้อ</summary>
    public async Task<PagedResultDTO<TransactionSummaryDTO>> ListPurchasesAsync(MembershipCaller caller, int page, int pageSize, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        return await _checkout.ListByMemberAsync(m.Id, Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ consent / PDPA

    /// <summary>ฉบับความยินยอมปัจจุบัน (ใช้ตอนสมัคร — ยังไม่ต้องเป็นสมาชิก)</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ฉบับที่เผยแพร่ล่าสุด หรือ null</returns>
    public Task<ConsentVersionDTO?> GetCurrentConsentAsync(CancellationToken ct) => _memberService.CurrentConsentAsync(ct);

    /// <summary>ความยินยอมของฉัน + ฉบับปัจจุบัน</summary>
    public async Task<MyConsentsDTO> GetConsentsAsync(MembershipCaller caller, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        return new MyConsentsDTO
        {
            Current = await _memberService.CurrentConsentAsync(ct).ConfigureAwait(false),
            History = [.. (await _members.ListConsentsAsync(m.Id, ct).ConfigureAwait(false)).OrderByDescending(c => c.At)],
        };
    }

    /// <summary>ให้ / ถอนความยินยอม (รับข่าวสารแยกตามช่องทาง)</summary>
    public async Task<MyConsentsDTO> UpdateConsentsAsync(MembershipCaller caller, UpdateConsentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        ConsentVersionDTO? version = (await _settings.ListConsentVersionsAsync(ct).ConfigureAwait(false)).FirstOrDefault(v => v.Id == request.ConsentVersionId && v.IsPublished);
        ValidationException.ThrowIf(version is null, "consentVersionId", "Unknown consent version.");
        DateTimeOffset now = _clock.GetUtcNow();
        List<MarketingChannel> channels = request.MarketingConsent ? [.. request.MarketingChannels.Distinct()] : [];
        await _members.AddConsentAsync(new MemberConsentDTO
        {
            MemberId = m.Id, ConsentVersionId = version!.Id, Accepted = request.Accepted, MarketingConsent = request.MarketingConsent && channels.Count > 0,
            MarketingChannels = channels, Channel = RegisteredChannel.Portal.ToString(), At = now,
        }, ct).ConfigureAwait(false);
        object before = new { m.MarketingConsent, m.MarketingChannels };
        m.MarketingConsent = request.MarketingConsent && channels.Count > 0;
        m.MarketingChannels = m.MarketingConsent ? string.Join(',', channels) : null;
        await _memberService.SaveAsync(caller, m, "member.consent", before, new { m.MarketingConsent, m.MarketingChannels, version = version.Id, request.Accepted }, ct).ConfigureAwait(false);
        return await GetConsentsAsync(caller, ct).ConfigureAwait(false);
    }

    /// <summary>ยื่นคำขอตาม PDPA</summary>
    public async Task<PrivacyRequestDTO> CreatePrivacyRequestAsync(MembershipCaller caller, CreatePrivacyRequest request, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        return await _privacyRequests.CreateAsync(caller, m, request, ct).ConfigureAwait(false);
    }

    /// <summary>คำขอของฉัน</summary>
    public async Task<IReadOnlyList<PrivacyRequestDTO>> ListPrivacyRequestsAsync(MembershipCaller caller, CancellationToken ct)
    {
        MemberEntity m = await MeAsync(caller, ct).ConfigureAwait(false);
        return await _privacyRequests.ListByMemberAsync(m.Id, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    private static string Subject(MembershipCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.IsClient || string.IsNullOrEmpty(caller.User.Subject) || caller.User == UserContextDTO.System)
        {
            throw new ForbiddenException("A member token is required.");
        }

        return caller.User.Subject;
    }

    private async Task<MemberEntity> MeAsync(MembershipCaller caller, CancellationToken ct)
    {
        MemberEntity? m = await _members.GetBySubjectAsync(Subject(caller), ct).ConfigureAwait(false);
        if (m is null || m.Status == MemberStatus.Closed)
        {
            throw new MembershipException(404, "not_registered", "This account is not registered as a member.");
        }

        return m;
    }

    /// <summary>เบอร์/อีเมลต้องไม่ซ้ำกับสมาชิกอื่น ยกเว้นสมาชิกที่เจ้าหน้าที่สมัครให้และยังไม่ผูกบัญชี (เบอร์ตรง)</summary>
    private async Task EnsureLinkableAsync(string phone, string? email, CancellationToken ct)
    {
        MemberEntity? byPhone = await _memberService.FindByPhoneAsync(phone, ct).ConfigureAwait(false);
        if (byPhone?.AuthSubject is not null)
        {
            throw MembershipException.Conflict("duplicate_member", "This phone is already linked to another account.");
        }

        if (email is not null && await _memberService.FindDuplicateAsync(null, email, byPhone?.Id, ct).ConfigureAwait(false) is not null)
        {
            throw MembershipException.Conflict("duplicate_member", "A member with this email already exists.");
        }
    }

    private async Task<List<(CouponBatchDTO Batch, CampaignDTO Campaign)>> RewardBatchesAsync(MemberEntity m, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        IReadOnlySet<long> segments = await _segments.ListSegmentIdsOfMemberAsync(m.Id, ct).ConfigureAwait(false);
        Dictionary<long, CampaignDTO> active = (await _campaigns.ListByStatusAsync([CampaignStatus.Active], ct).ConfigureAwait(false))
            .Where(c => Targets(c, m, segments)).ToDictionary(c => c.Id);
        return [.. (await _coupons.ListBatchesAsync(null, ct).ConfigureAwait(false))
            .Where(b => b.PointsCost is > 0 && b.ExpiresAt > now && b.Status != JobStatus.Failed && active.ContainsKey(b.CampaignId))
            .Select(b => (b, active[b.CampaignId]))];
    }

    private static bool Targets(CampaignDTO c, MemberEntity m, IReadOnlySet<long> segments) =>
        c.Targets.AllMembers
        || (m.TierCode is not null && c.Targets.TierCodes.Contains(m.TierCode, StringComparer.OrdinalIgnoreCase))
        || c.Targets.SegmentIds.Any(segments.Contains);
}
