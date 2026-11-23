using Common.AspNetCore.Authentication;
using Common.Core.Domain;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace CustomerMgnt.Api.Controllers;

/// <summary>ทะเบียนลูกค้า (บุคคล/นิติบุคคล), ตัวตนในช่องทาง, ลิงก์ไประบบอื่น, สัญญา และภาพรวม 360°</summary>
/// <param name="customers">บริการลูกค้า</param>
/// <param name="relationships">บริการความสัมพันธ์</param>
/// <param name="insights">บริการสรุป/ลิงก์</param>
/// <param name="ingestion">บริการสัญญา</param>
[Route("api/customers")]
public sealed class CustomersController(CustomerService customers, RelationshipService relationships, InsightService insights, DataIngestionService ingestion) : CustomerMgntControllerBase
{
    /// <summary>ค้นหาลูกค้า</summary>
    /// <param name="criteria">เงื่อนไข (query string)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลแบบแบ่งหน้า</returns>
    [HttpGet]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<PagedResultDTO<CustomerDTO>>> Search([FromQuery] CustomerSearchCriteria criteria, CancellationToken ct) =>
        Ok(await customers.SearchAsync(CurrentUser, criteria, ct));

    /// <summary>ลงทะเบียนลูกค้าใหม่ (เลขผู้เสียภาษีถูกเข้ารหัส PQC และตรวจซ้ำด้วย blind index)</summary>
    /// <param name="request">ข้อมูลลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้าที่สร้าง</returns>
    [HttpPost]
    [RequirePermission(Permissions.CustomerWrite)]
    [ProducesResponseType<CustomerDTO>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerDTO>> Register([FromBody] RegisterCustomerRequest request, CancellationToken ct)
    {
        CustomerDTO created = await customers.RegisterAsync(CurrentUser, request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>อ่านลูกค้า</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้า</returns>
    [HttpGet("{id:long}")]
    [RequirePermission(Permissions.CustomerRead)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerDTO>> Get(long id, CancellationToken ct) => Ok(await customers.GetAsync(CurrentUser, id, ct));

    /// <summary>อ่านลูกค้าจากรหัสลูกค้า (เช่น CUST-000123)</summary>
    /// <param name="code">รหัสลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้า</returns>
    [HttpGet("by-code/{code}")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<CustomerDTO>> GetByCode(string code, CancellationToken ct) => Ok(await customers.GetByCodeAsync(CurrentUser, code, ct));

    /// <summary>แก้ไขลูกค้า (ต้องส่ง version ล่าสุด)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">ข้อมูลที่แก้</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้าหลังแก้</returns>
    [HttpPut("{id:long}")]
    [RequirePermission(Permissions.CustomerWrite)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerDTO>> Update(long id, [FromBody] UpdateCustomerRequest request, CancellationToken ct) =>
        Ok(await customers.UpdateAsync(CurrentUser, id, request, ct));

    /// <summary>ภาพรวม 360° ของลูกค้า</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ภาพรวม</returns>
    [HttpGet("{id:long}/360")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<Customer360DTO>> Get360(long id, CancellationToken ct) => Ok(await customers.Get360Async(CurrentUser, id, ct));

    /// <summary>เพิ่มตัวตนในช่องทาง (LINE userId, Facebook PSID, อีเมล, ...)</summary>
    /// <param name="id">ลูกค้า</param>
    /// <param name="request">ตัวตน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ตัวตนที่บันทึก</returns>
    [HttpPost("{id:long}/identities")]
    [RequirePermission(Permissions.CustomerWrite)]
    public async Task<ActionResult<ChannelIdentityDTO>> AddIdentity(long id, [FromBody] ChannelIdentityRequest request, CancellationToken ct) =>
        Ok(await customers.AddIdentityAsync(CurrentUser, id, request, ct));

    /// <summary>ความสัมพันธ์ของลูกค้า (บุคคล → บริษัทที่สังกัด, บริษัท → ผู้ติดต่อ)</summary>
    /// <param name="id">ลูกค้า</param>
    /// <param name="activeOn">เฉพาะที่มีผลในวันนี้ (yyyy-MM-dd)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ความสัมพันธ์</returns>
    [HttpGet("{id:long}/relationships")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<IReadOnlyList<PartyRelationshipDTO>>> Relationships(long id, [FromQuery] DateOnly? activeOn, CancellationToken ct) =>
        Ok(await relationships.ListAsync(CurrentUser, id, activeOn, ct));

    /// <summary>ผูกลูกค้ากับโครงการ/issue/การประชุมในระบบอื่น</summary>
    /// <param name="id">ลูกค้า</param>
    /// <param name="request">ลิงก์</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลิงก์</returns>
    [HttpPost("{id:long}/links")]
    [RequirePermission(Permissions.CustomerWrite)]
    public async Task<ActionResult<ExternalLinkDTO>> Link(long id, [FromBody] ExternalLinkRequest request, CancellationToken ct) =>
        Ok(await insights.LinkAsync(CurrentUser, id, request.System, request.EntityType, request.ExternalId, request.Title, ct));

    /// <summary>สรุปโครงการของลูกค้า (Active/Closed พร้อมสถานะและความคืบหน้าจาก ProjectMgnt)</summary>
    /// <param name="id">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป</returns>
    [HttpGet("{id:long}/projects")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<ProjectPortfolioDTO>> Projects(long id, CancellationToken ct) => Ok(await insights.GetProjectPortfolioAsync(CurrentUser, id, ct));

    /// <summary>สัญญาของลูกค้า</summary>
    /// <param name="id">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สัญญา</returns>
    [HttpGet("{id:long}/contracts")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<IReadOnlyList<ContractDTO>>> Contracts(long id, CancellationToken ct) => Ok(await ingestion.ListContractsAsync(CurrentUser, id, ct));
}

/// <summary>ความสัมพันธ์บุคคล ↔ นิติบุคคล (ตำแหน่ง, ช่วงเวลา)</summary>
/// <param name="relationships">บริการความสัมพันธ์</param>
[Route("api/relationships")]
public sealed class RelationshipsController(RelationshipService relationships) : CustomerMgntControllerBase
{
    /// <summary>ผูกบุคคลกับบริษัท</summary>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ความสัมพันธ์</returns>
    [HttpPost]
    [RequirePermission(Permissions.CustomerWrite)]
    public async Task<ActionResult<PartyRelationshipDTO>> Link([FromBody] CreateRelationshipRequest request, CancellationToken ct) =>
        Ok(await relationships.LinkAsync(CurrentUser, request, ct));

    /// <summary>สิ้นสุดความสัมพันธ์ (เช่น ลาออก/ย้ายตำแหน่ง)</summary>
    /// <param name="id">ความสัมพันธ์</param>
    /// <param name="request">วันสุดท้าย</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ความสัมพันธ์</returns>
    [HttpPost("{id:long}/end")]
    [RequirePermission(Permissions.CustomerWrite)]
    public async Task<ActionResult<PartyRelationshipDTO>> End(long id, [FromBody] EndRelationshipRequest request, CancellationToken ct) =>
        Ok(await relationships.EndAsync(CurrentUser, id, request.ValidTo, ct));
}

/// <summary>สัญญา MA/บริการ</summary>
/// <param name="ingestion">บริการสัญญา</param>
[Route("api/contracts")]
public sealed class ContractsController(DataIngestionService ingestion) : CustomerMgntControllerBase
{
    /// <summary>บันทึกสัญญา</summary>
    /// <param name="request">สัญญา</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สัญญา</returns>
    [HttpPost]
    [RequirePermission(Permissions.CustomerWrite)]
    public async Task<ActionResult<ContractDTO>> Create([FromBody] ContractRequest request, CancellationToken ct) => Ok(await ingestion.CreateContractAsync(CurrentUser, request, ct));
}
