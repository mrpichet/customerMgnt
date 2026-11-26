using Common.AspNetCore.Authentication;
using Common.Core.Domain;
using CustomerMgnt.Api.Infrastructure;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;
using Microsoft.AspNetCore.Mvc;
using P = CustomerMgnt.Core.Membership.Services.MembershipPermissions;

namespace CustomerMgnt.Api.Controllers;

/// <summary>ฐานของ Membership API (Claude.MD §16) — ผู้เรียก = ผู้ใช้จาก JWT + client_id + X-Request-Id</summary>
public abstract class MembershipControllerBase : CustomerMgntControllerBase
{
    /// <summary>ผู้เรียกของระบบสมาชิก</summary>
    protected MembershipCaller Caller => MembershipApi.CallerOf(HttpContext, CurrentUser);
}

/// <summary>สมาชิก — <c>api/v1/members</c> (§18)</summary>
/// <param name="members">บริการสมาชิก</param>
[Route("api/v1/members")]
public sealed class MembersController(MemberService members) : MembershipControllerBase
{
    /// <summary>ค้นหาสมาชิก (ข้อมูลส่วนบุคคลแบบ mask)</summary>
    /// <param name="criteria">เงื่อนไข</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลแบบแบ่งหน้า</returns>
    [HttpGet]
    [RequirePermission(P.MemberRead)]
    public async Task<ActionResult<PagedResultDTO<MemberSummaryDTO>>> Search([FromQuery] MemberSearchCriteria criteria, CancellationToken ct) =>
        Ok(await members.SearchAsync(Caller, criteria, ct));

    /// <summary>สมัครสมาชิกโดยเจ้าหน้าที่ (สถานะ PendingVerification และส่ง OTP)</summary>
    /// <param name="request">ข้อมูลผู้สมัคร</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิกและ OTP ที่ส่ง</returns>
    [HttpPost]
    [RequirePermission(P.MemberWrite)]
    [ProducesResponseType<MemberRegistrationResultDTO>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MemberRegistrationResultDTO>> Register([FromBody] RegisterMemberRequest request, CancellationToken ct)
    {
        MemberRegistrationResultDTO r = await members.RegisterAsync(Caller, request, ct);
        return CreatedAtAction(nameof(Get), new { id = r.Member.Id }, r);
    }

    /// <summary>อ่านสมาชิก</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิก</returns>
    [HttpGet("{id:long}")]
    [RequirePermission(P.MemberRead)]
    public async Task<ActionResult<MemberDTO>> Get(long id, CancellationToken ct) => Ok(await members.GetAsync(Caller, id, ct));

    /// <summary>อ่านสมาชิกจากเลขสมาชิก</summary>
    /// <param name="memberNo">เลขสมาชิก</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิก</returns>
    [HttpGet("by-no/{memberNo}")]
    [RequirePermission(P.MemberRead)]
    public async Task<ActionResult<MemberDTO>> GetByNo(string memberNo, CancellationToken ct) => Ok(await members.GetByNoAsync(Caller, memberNo, ct));

    /// <summary>แก้ข้อมูลสมาชิก — เปลี่ยนเบอร์ได้ 202 พร้อม OTP ของเบอร์ใหม่</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">ข้อมูลที่แก้ (null = ไม่เปลี่ยน)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิกหลังแก้</returns>
    [HttpPut("{id:long}")]
    [RequirePermission(P.MemberWrite)]
    [ProducesResponseType<UpdateMemberResultDTO>(StatusCodes.Status200OK)]
    [ProducesResponseType<UpdateMemberResultDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<UpdateMemberResultDTO>> Update(long id, [FromBody] UpdateMemberRequest request, CancellationToken ct)
    {
        UpdateMemberResultDTO r = await members.UpdateAsync(Caller, id, request, ct);
        return r.Challenge is null ? Ok(r) : Accepted(r);
    }

    /// <summary>ดูข้อมูลส่วนบุคคลเต็ม (บันทึก audit)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">field ที่ต้องการและเหตุผล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อมูลส่วนบุคคล</returns>
    [HttpPost("{id:long}/reveal")]
    [RequirePermission(P.MemberPii)]
    public async Task<ActionResult<MemberPiiDTO>> Reveal(long id, [FromBody] RevealPiiRequest request, CancellationToken ct) => Ok(await members.RevealAsync(Caller, id, request, ct));

    /// <summary>ส่ง OTP ใหม่</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>OTP ที่ส่ง (ไม่มีรหัส)</returns>
    [HttpPost("{id:long}/otp")]
    [RequirePermission(P.MemberWrite)]
    [ProducesResponseType<OtpChallengeDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<OtpChallengeDTO>> ResendOtp(long id, CancellationToken ct) => Accepted(await members.ResendOtpAsync(Caller, id, ct));

    /// <summary>ยืนยัน OTP → Active</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">challengeId + code</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิก</returns>
    [HttpPost("{id:long}/verify")]
    [RequirePermission(P.MemberWrite)]
    public async Task<ActionResult<MemberDTO>> Verify(long id, [FromBody] VerifyOtpRequest request, CancellationToken ct) => Ok(await members.VerifyAsync(Caller, id, request, ct));

    /// <summary>ยืนยันแทน OTP (ตรวจบัตรต่อหน้า)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">เหตุผลและเอกสารอ้างอิง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิก</returns>
    [HttpPost("{id:long}/verify-override")]
    [RequirePermission(P.MemberVerifyOverride)]
    public async Task<ActionResult<MemberDTO>> VerifyOverride(long id, [FromBody] VerifyOverrideRequest request, CancellationToken ct) => Ok(await members.VerifyOverrideAsync(Caller, id, request, ct));

    /// <summary>เปลี่ยนสถานะสมาชิก</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">version, สถานะใหม่, เหตุผล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิก</returns>
    [HttpPut("{id:long}/status")]
    [RequirePermission(P.MemberStatus)]
    public async Task<ActionResult<MemberDTO>> ChangeStatus(long id, [FromBody] ChangeMemberStatusRequest request, CancellationToken ct) => Ok(await members.ChangeStatusAsync(Caller, id, request, ct));

    /// <summary>ผูกกับลูกค้า (customerCode = null ให้ระบบสร้างลูกค้าบุคคลใหม่)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">version + customerCode</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิก</returns>
    [HttpPut("{id:long}/customer")]
    [RequirePermission(P.MemberWrite)]
    public async Task<ActionResult<MemberDTO>> LinkCustomer(long id, [FromBody] LinkCustomerRequest request, CancellationToken ct) => Ok(await members.LinkCustomerAsync(Caller, id, request, ct));

    /// <summary>ประวัติ Tier</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ประวัติ</returns>
    [HttpGet("{id:long}/tier-history")]
    [RequirePermission(P.MemberRead)]
    public async Task<ActionResult<IReadOnlyList<TierHistoryDTO>>> TierHistory(long id, CancellationToken ct) => Ok(await members.GetTierHistoryAsync(Caller, id, ct));

    /// <summary>ประวัติความยินยอม</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ความยินยอม</returns>
    [HttpGet("{id:long}/consents")]
    [RequirePermission(P.MemberRead)]
    public async Task<ActionResult<IReadOnlyList<MemberConsentDTO>>> Consents(long id, CancellationToken ct) => Ok(await members.GetConsentsAsync(Caller, id, ct));

    /// <summary>audit log ของสมาชิกคนนี้</summary>
    /// <param name="id">รหัส</param>
    /// <param name="page">หน้า</param>
    /// <param name="pageSize">ขนาดหน้า (1-200)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>audit</returns>
    [HttpGet("{id:long}/audit")]
    [RequirePermission(P.AuditRead)]
    public async Task<ActionResult<PagedResultDTO<MembershipAuditDTO>>> Audit(long id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Ok(await members.GetAuditAsync(Caller, id, page, Math.Clamp(pageSize, 1, 200), ct));

    /// <summary>ตรวจเบอร์/อีเมลซ้ำ</summary>
    /// <param name="request">เบอร์ / อีเมล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลตรวจ</returns>
    [HttpPost("duplicates/check")]
    [RequirePermission(P.MemberWrite)]
    public async Task<ActionResult<DuplicateCheckDTO>> CheckDuplicate([FromBody] DuplicateCheckRequest request, CancellationToken ct) => Ok(await members.CheckDuplicateAsync(Caller, request, ct));

    /// <summary>เทียบข้อมูลก่อนรวมบัญชี</summary>
    /// <param name="primaryId">บัญชีหลัก</param>
    /// <param name="secondaryId">บัญชีที่ถูกรวม</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ตัวอย่างผลการรวม</returns>
    [HttpGet("merges/preview")]
    [RequirePermission(P.MemberMerge)]
    public async Task<ActionResult<MergePreviewDTO>> PreviewMerge([FromQuery] long primaryId, [FromQuery] long secondaryId, CancellationToken ct) =>
        Ok(await members.PreviewMergeAsync(Caller, primaryId, secondaryId, ct));

    /// <summary>ยื่นขอรวมบัญชี (รออนุมัติ)</summary>
    /// <param name="request">บัญชีและ field ที่เลือก</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขออนุมัติ</returns>
    [HttpPost("merges")]
    [RequirePermission(P.MemberMerge)]
    [ProducesResponseType<ApprovalRequestDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<ApprovalRequestDTO>> RequestMerge([FromBody] MergeRequest request, CancellationToken ct) => Accepted(await members.RequestMergeAsync(Caller, request, ct));
}

/// <summary>ขอประเมิน Tier</summary>
/// <param name="MemberId">สมาชิก (null = ทั้งหมด)</param>
public sealed record TierEvaluateRequest(long? MemberId);

/// <summary>Tier — <c>api/v1/tiers</c> (§19)</summary>
/// <param name="tiers">บริการ Tier</param>
[Route("api/v1/tiers")]
public sealed class TiersController(TierService tiers) : MembershipControllerBase
{
    /// <summary>Tier ทั้งหมด (เรียงตาม rank)</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Tier</returns>
    [HttpGet]
    [RequirePermission(P.MemberRead)]
    public async Task<ActionResult<IReadOnlyList<TierDTO>>> List(CancellationToken ct) => Ok(await tiers.ListAsync(Caller, ct));

    /// <summary>อ่าน Tier</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Tier</returns>
    [HttpGet("{id:long}")]
    [RequirePermission(P.MemberRead)]
    public async Task<ActionResult<TierDTO>> Get(long id, CancellationToken ct) => Ok(await tiers.GetAsync(Caller, id, ct));

    /// <summary>สร้าง Tier</summary>
    /// <param name="request">ข้อมูล Tier</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Tier ที่สร้าง</returns>
    [HttpPost]
    [RequirePermission(P.TierManage)]
    [ProducesResponseType<TierDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TierDTO>> Create([FromBody] TierRequest request, CancellationToken ct)
    {
        TierDTO t = await tiers.CreateAsync(Caller, request, ct);
        return CreatedAtAction(nameof(Get), new { id = t.Id }, t);
    }

    /// <summary>แก้ Tier (ต้องส่ง version)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">ข้อมูล Tier</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Tier หลังแก้</returns>
    [HttpPut("{id:long}")]
    [RequirePermission(P.TierManage)]
    public async Task<ActionResult<TierDTO>> Update(long id, [FromBody] TierRequest request, CancellationToken ct) => Ok(await tiers.UpdateAsync(Caller, id, request, ct));

    /// <summary>สั่งประเมิน Tier (งานเบื้องหลัง)</summary>
    /// <param name="request">สมาชิก (ว่าง = ทั้งหมด)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>งาน</returns>
    [HttpPost("evaluate")]
    [RequirePermission(P.TierManage)]
    [ProducesResponseType<JobDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<JobDTO>> Evaluate([FromBody] TierEvaluateRequest? request, CancellationToken ct) => Accepted(await tiers.EvaluateAsync(Caller, request?.MemberId, ct));
}

/// <summary>คะแนน — <c>api/v1/points</c> (§20)</summary>
/// <param name="points">บริการคะแนน</param>
[Route("api/v1/points")]
public sealed class PointsController(PointService points) : MembershipControllerBase
{
    /// <summary>ยอดคะแนนของสมาชิก</summary>
    /// <param name="memberId">สมาชิก</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ยอดคะแนน</returns>
    [HttpGet("balances/{memberId:long}")]
    [RequirePermission(P.PointsRead)]
    public async Task<ActionResult<PointBalanceDTO>> Balance(long memberId, CancellationToken ct) => Ok(await points.GetBalanceAsync(Caller, memberId, ct));

    /// <summary>รายการเคลื่อนไหวคะแนน (append-only)</summary>
    /// <param name="criteria">เงื่อนไข</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลแบบแบ่งหน้า</returns>
    [HttpGet("ledger")]
    [RequirePermission(P.PointsRead)]
    public async Task<ActionResult<PagedResultDTO<PointLedgerDTO>>> Ledger([FromQuery] LedgerCriteria criteria, CancellationToken ct) => Ok(await points.SearchLedgerAsync(Caller, criteria, ct));

    /// <summary>lot คะแนน (FEFO)</summary>
    /// <param name="memberId">สมาชิก</param>
    /// <param name="activeOnly">เฉพาะที่ยังเหลือและยังไม่หมดอายุ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>lot</returns>
    [HttpGet("lots/{memberId:long}")]
    [RequirePermission(P.PointsRead)]
    public async Task<ActionResult<IReadOnlyList<PointLotDTO>>> Lots(long memberId, [FromQuery] bool activeOnly = true, CancellationToken ct = default) =>
        Ok(await points.ListLotsAsync(Caller, memberId, activeOnly, ct));

    /// <summary>กฎการได้คะแนน</summary>
    /// <param name="activeOnly">เฉพาะที่ใช้งาน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กฎ</returns>
    [HttpGet("rules")]
    [RequirePermission(P.PointsRead)]
    public async Task<ActionResult<IReadOnlyList<EarnRuleDTO>>> Rules([FromQuery] bool activeOnly = false, CancellationToken ct = default) => Ok(await points.ListRulesAsync(Caller, activeOnly, ct));

    /// <summary>สร้างกฎ (รออนุมัติ)</summary>
    /// <param name="request">กฎ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กฎที่สร้าง</returns>
    [HttpPost("rules")]
    [RequirePermission(P.PointsRuleManage)]
    [ProducesResponseType<EarnRuleDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<EarnRuleDTO>> CreateRule([FromBody] EarnRuleRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await points.CreateRuleAsync(Caller, request, ct));

    /// <summary>แก้กฎ (field ที่ต้องอนุมัติ → PendingApproval)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">กฎ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กฎหลังแก้</returns>
    [HttpPut("rules/{id:long}")]
    [RequirePermission(P.PointsRuleManage)]
    public async Task<ActionResult<EarnRuleDTO>> UpdateRule(long id, [FromBody] EarnRuleRequest request, CancellationToken ct) => Ok(await points.UpdateRuleAsync(Caller, id, request, ct));

    /// <summary>ยื่นปรับคะแนน (ในวงเงิน = Applied, เกิน = PendingApproval)</summary>
    /// <param name="request">การปรับ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>การปรับ</returns>
    [HttpPost("adjustments")]
    [RequirePermission(P.PointsAdjust)]
    [ProducesResponseType<AdjustmentDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AdjustmentDTO>> Adjust([FromBody] AdjustmentRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await points.RequestAdjustmentAsync(Caller, request, ct));

    /// <summary>รายการปรับคะแนน</summary>
    /// <param name="status">สถานะ</param>
    /// <param name="memberId">สมาชิก</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>การปรับ</returns>
    [HttpGet("adjustments")]
    [RequirePermission(P.PointsRead)]
    public async Task<ActionResult<IReadOnlyList<AdjustmentDTO>>> Adjustments([FromQuery] AdjustmentStatus? status, [FromQuery] long? memberId, CancellationToken ct) =>
        Ok(await points.ListAdjustmentsAsync(Caller, status, memberId, ct));
}
