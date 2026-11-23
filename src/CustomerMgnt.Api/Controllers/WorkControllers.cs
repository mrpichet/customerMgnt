using Common.AspNetCore.Authentication;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace CustomerMgnt.Api.Controllers;

/// <summary>กิจกรรม/นัดหมาย/งานติดตาม (เชื่อม MessageHub สำหรับนัดประชุมและแจ้งเตือน)</summary>
/// <param name="activities">บริการกิจกรรม</param>
[Route("api/activities")]
public sealed class ActivitiesController(ActivityService activities) : CustomerMgntControllerBase
{
    /// <summary>สร้างกิจกรรม (ถ้าเป็น Meeting และตั้งค่า MessageHub ไว้ จะสร้างการประชุม + เชิญ + ตั้งเตือนให้)</summary>
    /// <param name="request">ข้อมูลกิจกรรม</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กิจกรรมที่สร้าง</returns>
    [HttpPost]
    [RequirePermission(Permissions.ActivityWrite)]
    public async Task<ActionResult<ActivityDTO>> Create([FromBody] CreateActivityRequest request, CancellationToken ct) =>
        Ok(await activities.CreateAsync(CurrentUser, request, ct));

    /// <summary>ปิดกิจกรรม (พร้อมสร้างงานติดตามต่อได้)</summary>
    /// <param name="id">กิจกรรม</param>
    /// <param name="request">ผลลัพธ์/งานต่อ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กิจกรรมที่ปิด และงานติดตาม (ถ้ามี)</returns>
    [HttpPost("{id:long}/complete")]
    [RequirePermission(Permissions.ActivityWrite)]
    public async Task<ActionResult<CompleteActivityResultDTO>> Complete(long id, [FromBody] CompleteActivityRequest request, CancellationToken ct)
    {
        (ActivityDTO completed, ActivityDTO? followUp) = await activities.CompleteAsync(CurrentUser, id, request, ct);
        return Ok(new CompleteActivityResultDTO(completed, followUp));
    }

    /// <summary>งานค้างของฉัน (เลยกำหนด + ภายใน N วันข้างหน้า)</summary>
    /// <param name="days">จำนวนวันข้างหน้า (1-90)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กิจกรรม</returns>
    [HttpGet("mine")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<IReadOnlyList<ActivityDTO>>> Mine([FromQuery] int days = 7, CancellationToken ct = default) =>
        Ok(await activities.MyOpenActivitiesAsync(CurrentUser, Math.Clamp(days, 1, 90), ct));

    /// <summary>กิจกรรมของลูกค้า</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="openOnly">เฉพาะที่ยังไม่ปิด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กิจกรรม</returns>
    [HttpGet("customer/{customerId:long}")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<IReadOnlyList<ActivityDTO>>> ByCustomer(long customerId, [FromQuery] bool openOnly = false, CancellationToken ct = default) =>
        Ok(await activities.ListByCustomerAsync(CurrentUser, customerId, openOnly, ct));

    /// <summary>วาระ/งานค้างของลูกค้า (นัดหมายถัดไป, งานเลยกำหนด, วาระประชุมจาก MessageHub)</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>วาระ</returns>
    [HttpGet("/api/customers/{customerId:long}/agenda")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<CustomerAgendaDTO>> Agenda(long customerId, CancellationToken ct) =>
        Ok(await activities.GetCustomerAgendaAsync(CurrentUser, customerId, ct));
}

/// <summary>ผลการปิดกิจกรรม</summary>
/// <param name="Completed">กิจกรรมที่ปิด</param>
/// <param name="FollowUp">งานติดตามที่สร้างต่อ (ถ้ามี)</param>
public sealed record CompleteActivityResultDTO(ActivityDTO Completed, ActivityDTO? FollowUp);

/// <summary>คำร้องขอของลูกค้า / TOR — อนุมัติแล้วส่งต่อ ProjectMgnt และ requirementMgnt</summary>
/// <param name="requests">บริการคำร้อง</param>
[Route("api/requests")]
public sealed class RequestsController(CustomerRequestService requests) : CustomerMgntControllerBase
{
    /// <summary>บันทึกคำร้อง/TOR ใหม่</summary>
    /// <param name="request">ข้อมูลคำร้องพร้อมรายการ TOR</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำร้องที่สร้าง</returns>
    [HttpPost]
    [RequirePermission(Permissions.RequestWrite)]
    [ProducesResponseType<CustomerRequestDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CustomerRequestDTO>> Create([FromBody] CreateCustomerRequestRequest request, CancellationToken ct)
    {
        CustomerRequestDTO created = await requests.CreateAsync(CurrentUser, request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>รายการคำร้อง</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="status">สถานะ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำร้อง</returns>
    [HttpGet]
    [RequirePermission(Permissions.RequestRead)]
    public async Task<ActionResult<IReadOnlyList<CustomerRequestDTO>>> List([FromQuery] long? customerId, [FromQuery] RequestStatus? status, CancellationToken ct) =>
        Ok(await requests.ListAsync(CurrentUser, customerId, status, ct));

    /// <summary>อ่านคำร้อง</summary>
    /// <param name="id">คำร้อง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำร้อง</returns>
    [HttpGet("{id:long}")]
    [RequirePermission(Permissions.RequestRead)]
    public async Task<ActionResult<CustomerRequestDTO>> Get(long id, CancellationToken ct) => Ok(await requests.GetAsync(CurrentUser, id, ct));

    /// <summary>ตัดสินคำร้อง (เริ่มประเมิน / อนุมัติ / ปฏิเสธ)</summary>
    /// <param name="id">คำร้อง</param>
    /// <param name="decision">ผลการตัดสิน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำร้องหลังเปลี่ยนสถานะ</returns>
    [HttpPost("{id:long}/decision")]
    [RequirePermission(Permissions.RequestWrite)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerRequestDTO>> Decide(long id, [FromBody] RequestDecisionRequest decision, CancellationToken ct) =>
        Ok(await requests.DecideAsync(CurrentUser, id, decision, ct));

    /// <summary>ส่งต่อคำร้องที่อนุมัติแล้วไป ProjectMgnt / requirementMgnt (ลงนามด้วย PQC เพื่อพิสูจน์ความถูกต้องภายหลัง)</summary>
    /// <param name="id">คำร้อง</param>
    /// <param name="options">ปลายทาง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลิงก์ที่สร้างและลายเซ็น</returns>
    [HttpPost("{id:long}/forward")]
    [RequirePermission(Permissions.RequestForward)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ForwardResultDTO>> Forward(long id, [FromBody] ForwardRequestOptions options, CancellationToken ct) =>
        Ok(await requests.ForwardAsync(CurrentUser, id, options, ct));

    /// <summary>ตรวจลายเซ็นของคำร้องที่ส่งต่อแล้ว (ตรวจว่าเนื้อหาไม่ถูกแก้หลังส่ง)</summary>
    /// <param name="id">คำร้อง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลตรวจ</returns>
    [HttpGet("{id:long}/verify-signature")]
    [RequirePermission(Permissions.RequestRead)]
    public async Task<ActionResult<SignatureCheckDTO>> VerifySignature(long id, CancellationToken ct) =>
        Ok(new SignatureCheckDTO(id, await requests.VerifyForwardSignatureAsync(CurrentUser, id, ct)));
}

/// <summary>ผลตรวจลายเซ็น</summary>
/// <param name="RequestId">คำร้อง</param>
/// <param name="Valid">ถูกต้องหรือไม่</param>
public sealed record SignatureCheckDTO(long RequestId, bool Valid);

/// <summary>Lead / opportunity และ sales pipeline</summary>
/// <param name="pipeline">บริการ pipeline</param>
[Route("api/opportunities")]
public sealed class PipelineController(PipelineService pipeline) : CustomerMgntControllerBase
{
    /// <summary>สร้าง lead/opportunity</summary>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>opportunity</returns>
    [HttpPost]
    [RequirePermission(Permissions.SalesWrite)]
    public async Task<ActionResult<OpportunityDTO>> Create([FromBody] CreateOpportunityRequest request, CancellationToken ct) =>
        Ok(await pipeline.CreateAsync(CurrentUser, request, ct));

    /// <summary>รายการ opportunity</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="openOnly">เฉพาะที่ยังไม่ปิด</param>
    /// <param name="mine">เฉพาะของฉัน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>opportunity</returns>
    [HttpGet]
    [RequirePermission(Permissions.SalesRead)]
    public async Task<ActionResult<IReadOnlyList<OpportunityDTO>>> List([FromQuery] long? customerId, [FromQuery] bool openOnly = true, [FromQuery] bool mine = false, CancellationToken ct = default) =>
        Ok(await pipeline.ListAsync(CurrentUser, customerId, openOnly, mine, ct));

    /// <summary>ย้าย stage (ต้องส่ง version ล่าสุด; Won/Lost ปิดดีล)</summary>
    /// <param name="id">opportunity</param>
    /// <param name="request">stage ใหม่</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>opportunity หลังย้าย</returns>
    [HttpPost("{id:long}/stage")]
    [RequirePermission(Permissions.SalesWrite)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OpportunityDTO>> MoveStage(long id, [FromBody] MoveStageRequest request, CancellationToken ct) =>
        Ok(await pipeline.MoveStageAsync(CurrentUser, id, request, ct));

    /// <summary>สรุป pipeline ตาม stage (มูลค่า, มูลค่าถ่วงน้ำหนัก, win rate)</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป</returns>
    [HttpGet("summary")]
    [RequirePermission(Permissions.SalesRead)]
    public async Task<ActionResult<PipelineSummaryDTO>> Summary(CancellationToken ct) => Ok(await pipeline.SummaryAsync(CurrentUser, ct));
}

/// <summary>SLA: นโยบายต่อลูกค้า, ระดับ severity, คำนวณกำหนดเวลา (ใช้โดย IssueTracking)</summary>
/// <param name="sla">บริการ SLA</param>
/// <param name="insights">บริการสรุป (compliance)</param>
[Route("api/sla")]
public sealed class SlaController(SlaService sla, InsightService insights) : CustomerMgntControllerBase
{
    /// <summary>คำนวณกำหนดเวลา response/resolution ของงานตาม SLA ของลูกค้าและ severity</summary>
    /// <param name="request">ลูกค้า, severity, เวลาเริ่ม</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กำหนดเวลา</returns>
    [HttpPost("calculate")]
    [RequirePermission(Permissions.SlaRead)]
    public async Task<ActionResult<SlaTargetDTO>> Calculate([FromBody] SlaCalculateRequest request, CancellationToken ct) =>
        Ok(await sla.CalculateTargetsAsync(CurrentUser, request, ct));

    /// <summary>ประเมินสถานะ SLA ของงาน (OnTrack / AtRisk / Breached)</summary>
    /// <param name="request">ข้อมูลงาน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลประเมิน</returns>
    [HttpPost("evaluate")]
    [RequirePermission(Permissions.SlaRead)]
    public async Task<ActionResult<SlaEvaluationDTO>> Evaluate([FromBody] SlaEvaluateRequest request, CancellationToken ct) =>
        Ok(await sla.EvaluateAsync(CurrentUser, request, ct));

    /// <summary>นโยบาย SLA ที่มีผลกับลูกค้า (ใช้ sync ระดับ severity ไปยัง IssueTracking)</summary>
    /// <param name="code">รหัสลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>นโยบาย</returns>
    [HttpGet("customers/{code}/policy")]
    [RequirePermission(Permissions.SlaRead)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SlaPolicyDTO>> EffectivePolicy(string code, CancellationToken ct)
    {
        SlaPolicyDTO? policy = await sla.GetEffectivePolicyAsync(CurrentUser, code, ct);
        return policy is null ? NotFound() : Ok(policy);
    }

    /// <summary>สรุปการปฏิบัติตาม SLA ของลูกค้า (จาก issue ใน IssueTracking)</summary>
    /// <param name="id">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป</returns>
    [HttpGet("customers/{id:long}/compliance")]
    [RequirePermission(Permissions.SlaRead)]
    public async Task<ActionResult<SlaComplianceDTO>> Compliance(long id, CancellationToken ct) => Ok(await insights.GetSlaComplianceAsync(CurrentUser, id, ct));

    /// <summary>รายการนโยบาย (customerId = เฉพาะของลูกค้านั้น + นโยบายกลาง)</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>นโยบาย</returns>
    [HttpGet("policies")]
    [RequirePermission(Permissions.SlaRead)]
    public async Task<ActionResult<IReadOnlyList<SlaPolicyDTO>>> Policies([FromQuery] long? customerId, CancellationToken ct) =>
        Ok(await sla.ListPoliciesAsync(CurrentUser, customerId, ct));

    /// <summary>สร้างนโยบาย SLA พร้อมระดับ severity ของลูกค้า</summary>
    /// <param name="request">นโยบาย</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>นโยบายที่สร้าง</returns>
    [HttpPost("policies")]
    [RequirePermission(Permissions.SlaManage)]
    public async Task<ActionResult<SlaPolicyDTO>> CreatePolicy([FromBody] SlaPolicyRequest request, CancellationToken ct) =>
        Ok(await sla.CreatePolicyAsync(CurrentUser, request, ct));

    /// <summary>แก้ไขนโยบาย SLA (แทนที่ระดับ severity ทั้งชุด; ต้องส่ง version ล่าสุด)</summary>
    /// <param name="id">นโยบาย</param>
    /// <param name="request">นโยบาย</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>นโยบายหลังแก้</returns>
    [HttpPut("policies/{id:long}")]
    [RequirePermission(Permissions.SlaManage)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SlaPolicyDTO>> UpdatePolicy(long id, [FromBody] SlaPolicyRequest request, CancellationToken ct) =>
        Ok(await sla.UpdatePolicyAsync(CurrentUser, id, request, ct));
}

/// <summary>สรุปข้อมูลจากระบบอื่น (อายุ issue ตาม severity)</summary>
/// <param name="insights">บริการสรุป</param>
[Route("api/insights")]
public sealed class InsightsController(InsightService insights) : CustomerMgntControllerBase
{
    /// <summary>สรุป issue ที่ยังเปิด แยกตาม severity และช่วงอายุ (customerId ว่าง = ทุกลูกค้า)</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป</returns>
    [HttpGet("issues/aging")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<IssueAgingSummaryDTO>> IssueAging([FromQuery] long? customerId, CancellationToken ct) =>
        Ok(await insights.GetIssueAgingAsync(CurrentUser, customerId, ct));
}
