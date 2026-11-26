using Common.AspNetCore.Authentication;
using Common.Core.Domain;
using CustomerMgnt.Api.Infrastructure;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using P = CustomerMgnt.Core.Membership.Services.MembershipPermissions;

namespace CustomerMgnt.Api.Controllers;

/// <summary>แคมเปญ — <c>api/v1/promotions</c> (§21)</summary>
/// <param name="campaigns">บริการแคมเปญ</param>
/// <param name="checkout">ใช้จำลองการคำนวณ</param>
[Route("api/v1/promotions")]
public sealed class PromotionsController(CampaignService campaigns, CheckoutService checkout) : MembershipControllerBase
{
    /// <summary>ค้นหาแคมเปญ</summary>
    /// <param name="criteria">เงื่อนไข</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลแบบแบ่งหน้า</returns>
    [HttpGet]
    [RequirePermission(P.PromotionRead)]
    public async Task<ActionResult<PagedResultDTO<CampaignSummaryDTO>>> Search([FromQuery] CampaignSearchCriteria criteria, CancellationToken ct) => Ok(await campaigns.SearchAsync(Caller, criteria, ct));

    /// <summary>สร้างแคมเปญ (Draft)</summary>
    /// <param name="request">แคมเปญ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>แคมเปญที่สร้าง</returns>
    [HttpPost]
    [RequirePermission(P.PromotionWrite)]
    [ProducesResponseType<CampaignDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CampaignDTO>> Create([FromBody] CampaignRequest request, CancellationToken ct)
    {
        CampaignDTO c = await campaigns.CreateAsync(Caller, request, ct);
        return CreatedAtAction(nameof(Get), new { id = c.Id }, c);
    }

    /// <summary>อ่านแคมเปญ (รวมการใช้สิทธิ์)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>แคมเปญ</returns>
    [HttpGet("{id:long}")]
    [RequirePermission(P.PromotionRead)]
    public async Task<ActionResult<CampaignDTO>> Get(long id, CancellationToken ct) => Ok(await campaigns.GetAsync(Caller, id, ct));

    /// <summary>แก้แคมเปญ (field ที่ต้องอนุมัติหลังอนุมัติแล้ว → กลับไป PendingApproval)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">แคมเปญ (ต้องส่ง version)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>แคมเปญหลังแก้</returns>
    [HttpPut("{id:long}")]
    [RequirePermission(P.PromotionWrite)]
    public async Task<ActionResult<CampaignDTO>> Update(long id, [FromBody] CampaignRequest request, CancellationToken ct) => Ok(await campaigns.UpdateAsync(Caller, id, request, ct));

    /// <summary>ส่งอนุมัติ</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">version + หมายเหตุ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>แคมเปญ</returns>
    [HttpPost("{id:long}/submit")]
    [RequirePermission(P.PromotionWrite)]
    public async Task<ActionResult<CampaignDTO>> Submit(long id, [FromBody] CampaignActionRequest request, CancellationToken ct) => Ok(await campaigns.SubmitAsync(Caller, id, request, ct));

    /// <summary>หยุดชั่วคราว</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">version + เหตุผล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>แคมเปญ</returns>
    [HttpPost("{id:long}/pause")]
    [RequirePermission(P.PromotionWrite)]
    public async Task<ActionResult<CampaignDTO>> Pause(long id, [FromBody] CampaignActionRequest request, CancellationToken ct) => Ok(await campaigns.PauseAsync(Caller, id, request, ct));

    /// <summary>ใช้งานต่อ</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">version + เหตุผล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>แคมเปญ</returns>
    [HttpPost("{id:long}/resume")]
    [RequirePermission(P.PromotionWrite)]
    public async Task<ActionResult<CampaignDTO>> Resume(long id, [FromBody] CampaignActionRequest request, CancellationToken ct) => Ok(await campaigns.ResumeAsync(Caller, id, request, ct));

    /// <summary>ยกเลิก</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">version + เหตุผล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>แคมเปญ</returns>
    [HttpPost("{id:long}/cancel")]
    [RequirePermission(P.PromotionWrite)]
    public async Task<ActionResult<CampaignDTO>> Cancel(long id, [FromBody] CampaignActionRequest request, CancellationToken ct) => Ok(await campaigns.CancelAsync(Caller, id, request, ct));

    /// <summary>จำลองการคำนวณ (รวมแคมเปญ Draft ได้ ไม่บันทึกอะไร)</summary>
    /// <param name="request">ตะกร้าสินค้า + campaignIds</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลคำนวณ</returns>
    [HttpPost("simulate")]
    [RequirePermission(P.PromotionRead)]
    public async Task<ActionResult<QuoteDTO>> Simulate([FromBody] QuoteRequest request, CancellationToken ct) => Ok(await checkout.SimulateAsync(Caller, request, ct));
}

/// <summary>คูปอง — <c>api/v1/coupons</c> (§21)</summary>
/// <param name="coupons">บริการคูปอง</param>
[Route("api/v1/coupons")]
public sealed class CouponsController(CouponService coupons) : MembershipControllerBase
{
    /// <summary>ชุดคูปอง</summary>
    /// <param name="campaignId">แคมเปญ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ชุดคูปอง</returns>
    [HttpGet("batches")]
    [RequirePermission(P.CouponRead)]
    public async Task<ActionResult<IReadOnlyList<CouponBatchDTO>>> Batches([FromQuery] long? campaignId, CancellationToken ct) => Ok(await coupons.ListBatchesAsync(Caller, campaignId, ct));

    /// <summary>สร้างชุดคูปอง (สร้างรหัสเบื้องหลัง)</summary>
    /// <param name="request">ชุดคูปอง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>งาน</returns>
    [HttpPost("batches")]
    [RequirePermission(P.CouponWrite)]
    [ProducesResponseType<JobDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<JobDTO>> CreateBatch([FromBody] CouponBatchRequest request, CancellationToken ct) => Accepted(await coupons.CreateBatchAsync(Caller, request, ct));

    /// <summary>ค้นหาคูปอง</summary>
    /// <param name="criteria">เงื่อนไข</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลแบบแบ่งหน้า</returns>
    [HttpGet]
    [RequirePermission(P.CouponRead)]
    public async Task<ActionResult<PagedResultDTO<CouponDTO>>> Search([FromQuery] CouponSearchCriteria criteria, CancellationToken ct) => Ok(await coupons.SearchAsync(Caller, criteria, ct));

    /// <summary>อ่านคูปองพร้อมประวัติ</summary>
    /// <param name="code">รหัสคูปอง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คูปอง</returns>
    [HttpGet("{code}")]
    [RequirePermission(P.CouponRead)]
    public async Task<ActionResult<CouponDetailDTO>> Get(string code, CancellationToken ct) => Ok(await coupons.GetAsync(Caller, code, ct));

    /// <summary>ยกเลิกคูปอง</summary>
    /// <param name="code">รหัสคูปอง</param>
    /// <param name="request">เหตุผล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คูปอง</returns>
    [HttpPost("{code}/cancel")]
    [RequirePermission(P.CouponWrite)]
    public async Task<ActionResult<CouponDTO>> Cancel(string code, [FromBody] CancelCouponRequest request, CancellationToken ct) => Ok(await coupons.CancelAsync(Caller, code, request, ct));

    /// <summary>แจกคูปองให้สมาชิก / Segment (เบื้องหลัง)</summary>
    /// <param name="request">แคมเปญและผู้รับ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>งาน</returns>
    [HttpPost("issue")]
    [RequirePermission(P.CouponWrite)]
    [ProducesResponseType<JobDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<JobDTO>> Issue([FromBody] IssueCouponsRequest request, CancellationToken ct) => Accepted(await coupons.IssueAsync(Caller, request, ct));
}

/// <summary>ตัวอย่างผลของเงื่อนไข Segment</summary>
/// <param name="Rule">เงื่อนไข</param>
public sealed record SegmentPreviewRequest(SegmentRuleDTO Rule);

/// <summary>Segment — <c>api/v1/segments</c> (§21)</summary>
/// <param name="segments">บริการ Segment</param>
[Route("api/v1/segments")]
public sealed class SegmentsController(SegmentService segments) : MembershipControllerBase
{
    /// <summary>Segment ทั้งหมด</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Segment</returns>
    [HttpGet]
    [RequirePermission(P.SegmentRead)]
    public async Task<ActionResult<IReadOnlyList<SegmentDTO>>> List(CancellationToken ct) => Ok(await segments.ListAsync(Caller, ct));

    /// <summary>อ่าน Segment</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Segment</returns>
    [HttpGet("{id:long}")]
    [RequirePermission(P.SegmentRead)]
    public async Task<ActionResult<SegmentDTO>> Get(long id, CancellationToken ct) => Ok(await segments.GetAsync(Caller, id, ct));

    /// <summary>สร้าง Segment</summary>
    /// <param name="request">Segment</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Segment ที่สร้าง</returns>
    [HttpPost]
    [RequirePermission(P.SegmentWrite)]
    [ProducesResponseType<SegmentDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<SegmentDTO>> Create([FromBody] SegmentRequest request, CancellationToken ct)
    {
        SegmentDTO s = await segments.CreateAsync(Caller, request, ct);
        return CreatedAtAction(nameof(Get), new { id = s.Id }, s);
    }

    /// <summary>แก้ Segment (ต้องส่ง version)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">Segment</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Segment หลังแก้</returns>
    [HttpPut("{id:long}")]
    [RequirePermission(P.SegmentWrite)]
    public async Task<ActionResult<SegmentDTO>> Update(long id, [FromBody] SegmentRequest request, CancellationToken ct) => Ok(await segments.UpdateAsync(Caller, id, request, ct));

    /// <summary>ดูจำนวนและตัวอย่างสมาชิกของเงื่อนไข</summary>
    /// <param name="request">เงื่อนไข</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>จำนวน + ตัวอย่าง 10 คน</returns>
    [HttpPost("preview")]
    [RequirePermission(P.SegmentRead)]
    public async Task<ActionResult<SegmentPreviewDTO>> Preview([FromBody] SegmentPreviewRequest request, CancellationToken ct) => Ok(await segments.PreviewAsync(Caller, request.Rule, ct));

    /// <summary>คำนวณสมาชิกใหม่ (เบื้องหลัง)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>งาน</returns>
    [HttpPost("{id:long}/refresh")]
    [RequirePermission(P.SegmentWrite)]
    [ProducesResponseType<JobDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<JobDTO>> Refresh(long id, CancellationToken ct) => Accepted(await segments.RefreshAsync(Caller, id, ct));

    /// <summary>สมาชิกใน Segment</summary>
    /// <param name="id">รหัส</param>
    /// <param name="page">หน้า</param>
    /// <param name="pageSize">ขนาดหน้า (1-200)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลแบบแบ่งหน้า</returns>
    [HttpGet("{id:long}/members")]
    [RequirePermission(P.SegmentRead)]
    public async Task<ActionResult<PagedResultDTO<MemberSummaryDTO>>> Members(long id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Ok(await segments.ListMembersAsync(Caller, id, page, Math.Clamp(pageSize, 1, 200), ct));
}

/// <summary>POS / ระบบต้นทาง — <c>api/v1/pos</c> (§22): client_credentials และ <c>Idempotency-Key</c> ทุก POST</summary>
/// <param name="checkout">บริการ checkout</param>
[Route("api/v1/pos")]
[EnableRateLimiting(MembershipApi.PosRateLimit)]
public sealed class PosController(CheckoutService checkout) : MembershipControllerBase
{
    /// <summary>ค้นหาสมาชิกด้วยเบอร์ / เลขสมาชิก / รหัสคูปอง</summary>
    /// <param name="phone">เบอร์โทร</param>
    /// <param name="memberNo">เลขสมาชิก</param>
    /// <param name="couponCode">รหัสคูปอง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อมูลที่แคชเชียร์ต้องใช้</returns>
    [HttpGet("members/lookup")]
    [RequirePermission(P.PosLookup)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PosMemberDTO>> Lookup([FromQuery] string? phone, [FromQuery] string? memberNo, [FromQuery] string? couponCode, CancellationToken ct) =>
        Ok(await checkout.LookupAsync(Caller, phone, memberNo, couponCode, ct));

    /// <summary>คำนวณส่วนลด/คะแนน (ไม่จองอะไร)</summary>
    /// <param name="request">ตะกร้าสินค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลคำนวณ</returns>
    [HttpPost("checkout/quote")]
    [RequirePermission(P.PosCheckout)]
    public async Task<ActionResult<QuoteDTO>> Quote([FromBody] QuoteRequest request, CancellationToken ct) => Ok(await checkout.QuoteAsync(Caller, request, ct));

    /// <summary>จองคะแนน คูปอง และสิทธิ์ — ผลต่างจาก quoteId เดิม = 409 quote_changed พร้อม quote ใหม่ใน payload</summary>
    /// <param name="request">ตะกร้าสินค้า + quoteId</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>การจอง</returns>
    [HttpPost("checkout/reserve")]
    [RequirePermission(P.PosCheckout)]
    [Idempotent]
    [ProducesResponseType<ReservationDTO>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReservationDTO>> Reserve([FromBody] QuoteRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await checkout.ReserveAsync(Caller, request, ct));

    /// <summary>ยืนยันการขาย (ตัดคะแนน ใช้คูปอง ให้คะแนน)</summary>
    /// <param name="reservationId">การจอง</param>
    /// <param name="request">transactionId + การชำระ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลการขาย</returns>
    [HttpPost("checkout/{reservationId}/commit")]
    [RequirePermission(P.PosCheckout)]
    [Idempotent]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    public async Task<ActionResult<CheckoutResultDTO>> Commit(string reservationId, [FromBody] CommitRequest request, CancellationToken ct) =>
        Ok(await checkout.CommitAsync(Caller, reservationId, request, ct));

    /// <summary>ยกเลิกการจอง (ซ้ำได้)</summary>
    /// <param name="reservationId">การจอง</param>
    /// <param name="request">เหตุผล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>204</returns>
    [HttpPost("checkout/{reservationId}/release")]
    [RequirePermission(P.PosCheckout)]
    [Idempotent]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Release(string reservationId, [FromBody] ReleaseRequest request, CancellationToken ct)
    {
        await checkout.ReleaseAsync(Caller, reservationId, request, ct);
        return NoContent();
    }

    /// <summary>บันทึกการขายที่ไม่ผ่าน checkout (offline — ได้คะแนนพื้นฐานอย่างเดียว)</summary>
    /// <param name="request">รายการขาย</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>201 ใหม่ / 200 เคยบันทึกแล้ว</returns>
    [HttpPost("transactions")]
    [RequirePermission(P.PosTransaction)]
    [Idempotent]
    [ProducesResponseType<TransactionResultDTO>(StatusCodes.Status201Created)]
    [ProducesResponseType<TransactionResultDTO>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TransactionResultDTO>> Record([FromBody] SalesTransactionRequest request, CancellationToken ct)
    {
        TransactionResultDTO r = await checkout.RecordTransactionAsync(Caller, request, ct);
        return r.AlreadyRecorded ? Ok(r) : StatusCode(StatusCodes.Status201Created, r);
    }

    /// <summary>อ่านรายการขายของระบบต้นทางนี้</summary>
    /// <param name="transactionId">เลขรายการ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการขาย</returns>
    [HttpGet("transactions/{transactionId}")]
    [RequirePermission(P.PosTransaction)]
    public async Task<ActionResult<TransactionResultDTO>> GetTransaction(string transactionId, CancellationToken ct) => Ok(await checkout.GetTransactionAsync(Caller, transactionId, ct));

    /// <summary>ยกเลิกรายการขาย (void)</summary>
    /// <param name="transactionId">เลขรายการ</param>
    /// <param name="request">เหตุผล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการขาย</returns>
    [HttpPost("transactions/{transactionId}/cancel")]
    [RequirePermission(P.PosTransaction)]
    [Idempotent]
    public async Task<ActionResult<TransactionResultDTO>> CancelTransaction(string transactionId, [FromBody] ReleaseRequest request, CancellationToken ct) =>
        Ok(await checkout.CancelTransactionAsync(Caller, transactionId, request, ct));

    /// <summary>คืนสินค้า — ยังไม่พบรายการขาย (มาผิดลำดับ) = 202 PendingOriginal แล้วประมวลผลภายหลัง</summary>
    /// <param name="transactionId">เลขรายการขายเดิม</param>
    /// <param name="request">รายการคืน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลการคืน</returns>
    [HttpPost("transactions/{transactionId}/returns")]
    [RequirePermission(P.PosTransaction)]
    [Idempotent]
    [ProducesResponseType<ReturnResultDTO>(StatusCodes.Status200OK)]
    [ProducesResponseType<ReturnResultDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<ReturnResultDTO>> Return(string transactionId, [FromBody] ReturnRequest request, CancellationToken ct)
    {
        ReturnResultDTO r = await checkout.ReturnAsync(Caller, transactionId, request, ct);
        return r.Status == "PendingOriginal" ? Accepted(r) : Ok(r);
    }
}

/// <summary>Portal ของสมาชิก — <c>api/v1/me</c> (§23): token สมาชิกจาก AuthServer ไม่ต้องมี permission</summary>
/// <param name="portal">บริการ Portal</param>
[Route("api/v1/me")]
[Authorize]
[EnableRateLimiting(MembershipApi.PortalRateLimit)]
public sealed class PortalController(PortalService portal) : MembershipControllerBase
{
    /// <summary>ข้อมูลสมาชิกของฉัน (ไม่ mask)</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิก / 404 not_registered</returns>
    [HttpGet("member")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MemberDTO>> Me(CancellationToken ct) => Ok(await portal.GetMeAsync(Caller, ct));

    /// <summary>เริ่มสมัคร — ส่ง OTP ไปที่เบอร์</summary>
    /// <param name="request">ข้อมูลผู้สมัคร</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>registrationId + challengeId</returns>
    [HttpPost("registration")]
    [ProducesResponseType<PortalRegistrationDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<PortalRegistrationDTO>> StartRegistration([FromBody] RegisterMemberRequest request, CancellationToken ct) =>
        Accepted(await portal.StartRegistrationAsync(Caller, request, ct));

    /// <summary>ยืนยัน OTP แล้วสร้าง/ผูกสมาชิก</summary>
    /// <param name="request">challengeId + code</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิก</returns>
    [HttpPost("registration/verify")]
    [ProducesResponseType<MemberDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<MemberDTO>> CompleteRegistration([FromBody] VerifyOtpRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await portal.CompleteRegistrationAsync(Caller, request, ct));

    /// <summary>แก้ข้อมูลของฉัน — เปลี่ยนเบอร์ได้ 202 + OTP</summary>
    /// <param name="request">ข้อมูลที่แก้</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิก</returns>
    [HttpPut("member")]
    [ProducesResponseType<UpdateMemberResultDTO>(StatusCodes.Status200OK)]
    [ProducesResponseType<UpdateMemberResultDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<UpdateMemberResultDTO>> Update([FromBody] UpdateMemberRequest request, CancellationToken ct)
    {
        UpdateMemberResultDTO r = await portal.UpdateMeAsync(Caller, request, ct);
        return r.Challenge is null ? Ok(r) : Accepted(r);
    }

    /// <summary>ยืนยัน OTP ของเบอร์ใหม่</summary>
    /// <param name="request">challengeId + code</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สมาชิก</returns>
    [HttpPost("member/phone/verify")]
    public async Task<ActionResult<MemberDTO>> VerifyPhone([FromBody] VerifyOtpRequest request, CancellationToken ct) => Ok(await portal.VerifyPhoneChangeAsync(Caller, request, ct));

    /// <summary>ยอดคะแนนของฉัน</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ยอดคะแนน</returns>
    [HttpGet("points")]
    public async Task<ActionResult<PointBalanceDTO>> Points(CancellationToken ct) => Ok(await portal.GetPointsAsync(Caller, ct));

    /// <summary>รายการเคลื่อนไหวคะแนนของฉัน</summary>
    /// <param name="page">หน้า</param>
    /// <param name="pageSize">ขนาดหน้า (1-100)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลแบบแบ่งหน้า</returns>
    [HttpGet("points/ledger")]
    public async Task<ActionResult<PagedResultDTO<PointLedgerDTO>>> Ledger([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await portal.GetLedgerAsync(Caller, page, Math.Clamp(pageSize, 1, 100), ct));

    /// <summary>ของรางวัลที่แลกด้วยคะแนนได้</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ของรางวัล</returns>
    [HttpGet("rewards")]
    public async Task<ActionResult<IReadOnlyList<RewardDTO>>> Rewards(CancellationToken ct) => Ok(await portal.ListRewardsAsync(Caller, ct));

    /// <summary>แลกของรางวัล (ต้องมี Idempotency-Key)</summary>
    /// <param name="rewardId">ของรางวัล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คูปองที่ได้</returns>
    [HttpPost("rewards/{rewardId:long}/redeem")]
    [ProducesResponseType<CouponDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CouponDTO>> Redeem(long rewardId, CancellationToken ct)
    {
        string? key = Request.Headers[IdempotencyFilter.Header].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 100)
        {
            throw new CustomerMgnt.Core.Membership.Engine.MembershipException(400, "idempotency_key_required", "The Idempotency-Key header is required (at most 100 characters).");
        }

        return StatusCode(StatusCodes.Status201Created, await portal.RedeemRewardAsync(Caller, rewardId, key, ct));
    }

    /// <summary>คูปองของฉัน (มี qrPayload)</summary>
    /// <param name="status">สถานะ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คูปอง</returns>
    [HttpGet("coupons")]
    public async Task<ActionResult<IReadOnlyList<CouponDTO>>> Coupons([FromQuery] CouponStatus? status, CancellationToken ct) => Ok(await portal.ListCouponsAsync(Caller, status, ct));

    /// <summary>โปรโมชันที่ฉันมีสิทธิ์</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>แคมเปญ</returns>
    [HttpGet("promotions")]
    public async Task<ActionResult<IReadOnlyList<CampaignSummaryDTO>>> Promotions(CancellationToken ct) => Ok(await portal.ListPromotionsAsync(Caller, ct));

    /// <summary>ประวัติการซื้อของฉัน</summary>
    /// <param name="page">หน้า</param>
    /// <param name="pageSize">ขนาดหน้า (1-100)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลแบบแบ่งหน้า</returns>
    [HttpGet("purchases")]
    public async Task<ActionResult<PagedResultDTO<TransactionSummaryDTO>>> Purchases([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await portal.ListPurchasesAsync(Caller, page, Math.Clamp(pageSize, 1, 100), ct));

    /// <summary>ความยินยอมของฉันและฉบับปัจจุบัน</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ความยินยอม</returns>
    [HttpGet("consents")]
    public async Task<ActionResult<MyConsentsDTO>> Consents(CancellationToken ct) => Ok(await portal.GetConsentsAsync(Caller, ct));

    /// <summary>ให้/ถอนความยินยอม</summary>
    /// <param name="request">ฉบับ + ช่องทางการตลาด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ความยินยอม</returns>
    [HttpPut("consents")]
    public async Task<ActionResult<MyConsentsDTO>> UpdateConsents([FromBody] UpdateConsentRequest request, CancellationToken ct) => Ok(await portal.UpdateConsentsAsync(Caller, request, ct));

    /// <summary>ยื่นคำขอใช้สิทธิ์ตาม PDPA</summary>
    /// <param name="request">ชนิดและรายละเอียด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    [HttpPost("privacy-requests")]
    [ProducesResponseType<PrivacyRequestDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PrivacyRequestDTO>> CreatePrivacyRequest([FromBody] CreatePrivacyRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await portal.CreatePrivacyRequestAsync(Caller, request, ct));

    /// <summary>คำขอ PDPA ของฉัน</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    [HttpGet("privacy-requests")]
    public async Task<ActionResult<IReadOnlyList<PrivacyRequestDTO>>> PrivacyRequests(CancellationToken ct) => Ok(await portal.ListPrivacyRequestsAsync(Caller, ct));
}
