using Common.AspNetCore.Authentication;
using Common.Core.Domain;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using P = CustomerMgnt.Core.Membership.Services.MembershipPermissions;

namespace CustomerMgnt.Api.Controllers;

/// <summary>ประวัติการซื้อ — <c>api/v1/transactions</c> (§24)</summary>
/// <param name="checkout">บริการรายการขาย</param>
[Route("api/v1/transactions")]
public sealed class MembershipTransactionsController(CheckoutService checkout) : MembershipControllerBase
{
    /// <summary>ค้นหารายการขาย</summary>
    /// <param name="criteria">เงื่อนไข</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลแบบแบ่งหน้า</returns>
    [HttpGet]
    [RequirePermission(P.TxnRead)]
    public async Task<ActionResult<PagedResultDTO<TransactionSummaryDTO>>> Search([FromQuery] TransactionSearchCriteria criteria, CancellationToken ct) => Ok(await checkout.SearchAsync(Caller, criteria, ct));

    /// <summary>รายละเอียดรายการขาย (สินค้า สิทธิประโยชน์ คะแนน การคืน)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายละเอียด</returns>
    [HttpGet("{id:long}")]
    [RequirePermission(P.TxnRead)]
    public async Task<ActionResult<TransactionDetailDTO>> Get(long id, CancellationToken ct) => Ok(await checkout.GetDetailAsync(Caller, id, ct));
}

/// <summary>คำขออนุมัติ — <c>api/v1/approvals</c> (§24) สิทธิ์ตรวจตามชนิดเรื่องใน service</summary>
/// <param name="approvals">บริการอนุมัติ</param>
[Route("api/v1/approvals")]
[Authorize]
public sealed class MembershipApprovalsController(ApprovalService approvals) : MembershipControllerBase
{
    /// <summary>ค้นหาคำขอ</summary>
    /// <param name="criteria">เงื่อนไข</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApprovalRequestDTO>>> Search([FromQuery] ApprovalSearchCriteria criteria, CancellationToken ct) => Ok(await approvals.SearchAsync(Caller, criteria, ct));

    /// <summary>จำนวนคำขอที่รอฉันพิจารณา</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>จำนวน</returns>
    [HttpGet("pending-count")]
    public async Task<ActionResult<int>> PendingCount(CancellationToken ct) => Ok(await approvals.CountPendingAsync(Caller, ct));

    /// <summary>อ่านคำขอ</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApprovalRequestDTO>> Get(long id, CancellationToken ct) => Ok(await approvals.GetAsync(Caller, id, ct));

    /// <summary>อนุมัติ / ไม่อนุมัติ (ไม่อนุมัติต้องมีหมายเหตุ, อนุมัติงานตัวเองไม่ได้)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">ผลการพิจารณา</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    [HttpPost("{id:long}/decision")]
    public async Task<ActionResult<ApprovalRequestDTO>> Decide(long id, [FromBody] ApprovalDecisionRequest request, CancellationToken ct) => Ok(await approvals.DecideAsync(Caller, id, request, ct));

    /// <summary>ผู้ยื่นยกเลิกคำขอ</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">หมายเหตุ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    [HttpPost("{id:long}/cancel")]
    public async Task<ActionResult<ApprovalRequestDTO>> Cancel(long id, [FromBody] ApprovalCancelRequest request, CancellationToken ct) => Ok(await approvals.CancelAsync(Caller, id, request, ct));
}

/// <summary>รายงาน / ส่งออก / dashboard — <c>api/v1/reports</c>, <c>api/v1/dashboard</c> (§24)</summary>
/// <param name="reports">บริการรายงาน</param>
[Route("api/v1")]
public sealed class MembershipReportsController(ReportService reports) : MembershipControllerBase
{
    /// <summary>ตัวเลขภาพรวมตามสิทธิ์ของผู้เรียก</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>dashboard</returns>
    [HttpGet("dashboard")]
    [RequirePermission(P.MemberRead)]
    public async Task<ActionResult<DashboardDTO>> Dashboard(CancellationToken ct) => Ok(await reports.GetDashboardAsync(Caller, ct));

    /// <summary>รายงานบนหน้าจอ (members / points / promotions / transactions, ≤ 5,000 แถว)</summary>
    /// <param name="report">ชื่อรายงาน</param>
    /// <param name="filter">ช่วงวันและตัวกรอง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายงาน</returns>
    [HttpGet("reports/{report:regex(^(members|points|promotions|transactions)$)}")]
    [RequirePermission(P.ReportRead)]
    public async Task<ActionResult<ReportDTO>> Report(string report, [FromQuery] ReportFilter filter, CancellationToken ct) => Ok(await reports.GetReportAsync(Caller, report, filter, ct));

    /// <summary>ขอส่งออกรายงาน (เบื้องหลัง)</summary>
    /// <param name="request">รายงาน ตัวกรอง รูปแบบไฟล์</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>งานส่งออก</returns>
    [HttpPost("reports/exports")]
    [RequirePermission(P.ReportExport)]
    [ProducesResponseType<ExportJobDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<ExportJobDTO>> RequestExport([FromBody] ExportRequest request, CancellationToken ct) => Accepted(await reports.RequestExportAsync(Caller, request, ct));

    /// <summary>งานส่งออกของฉัน</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>งาน</returns>
    [HttpGet("reports/exports")]
    [RequirePermission(P.ReportExport)]
    public async Task<ActionResult<IReadOnlyList<ExportJobDTO>>> Exports(CancellationToken ct) => Ok(await reports.ListExportsAsync(Caller, ct));

    /// <summary>ดาวน์โหลดไฟล์ (เจ้าของงานเท่านั้น หมดอายุ 24 ชม.)</summary>
    /// <param name="id">งาน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ไฟล์</returns>
    [HttpGet("reports/exports/{id:long}/file")]
    [RequirePermission(P.ReportExport)]
    [Produces("application/octet-stream")]
    public async Task<IActionResult> Download(long id, CancellationToken ct)
    {
        (string name, string type, byte[] content) = await reports.DownloadAsync(Caller, id, ct);
        Response.Headers.CacheControl = "no-store";
        return File(content, type, name);
    }
}

/// <summary>ส่งข้อความแคมเปญ — <c>api/v1/campaign-messages</c> (§24)</summary>
/// <param name="messages">บริการส่งข้อความ</param>
[Route("api/v1/campaign-messages")]
public sealed class CampaignMessagesController(CampaignMessageService messages) : MembershipControllerBase
{
    /// <summary>ตั้งงานส่งข้อความ (ส่งเฉพาะคนที่ยินยอมช่องทางนั้น)</summary>
    /// <param name="request">ผู้รับ ช่องทาง ข้อความ เวลา</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>งาน</returns>
    [HttpPost]
    [RequirePermission(P.CampaignMessageSend)]
    [ProducesResponseType<MessageJobDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<MessageJobDTO>> Create([FromBody] CampaignMessageRequest request, CancellationToken ct) => Accepted(await messages.CreateAsync(Caller, request, ct));

    /// <summary>สถานะงานส่งข้อความ</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>งาน</returns>
    [HttpGet]
    [RequirePermission(P.CampaignMessageSend)]
    public async Task<ActionResult<IReadOnlyList<MessageJobDTO>>> List(CancellationToken ct) => Ok(await messages.ListAsync(Caller, ct));
}

/// <summary>ตั้งค่า และ audit — <c>api/v1/settings</c>, <c>api/v1/audit</c> (§24)</summary>
/// <param name="settings">บริการตั้งค่า</param>
[Route("api/v1")]
public sealed class MembershipSettingsController(SettingsService settings) : MembershipControllerBase
{
    /// <summary>กฎธุรกิจปัจจุบัน</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กฎธุรกิจ</returns>
    [HttpGet("settings/rules")]
    [RequirePermission(P.SettingsManage)]
    public async Task<ActionResult<MembershipRulesDTO>> GetRules(CancellationToken ct) => Ok(await settings.GetRulesAsync(Caller, ct));

    /// <summary>บันทึกกฎธุรกิจ (ต้องส่ง version)</summary>
    /// <param name="rules">กฎธุรกิจ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กฎธุรกิจหลังบันทึก</returns>
    [HttpPut("settings/rules")]
    [RequirePermission(P.SettingsManage)]
    public async Task<ActionResult<MembershipRulesDTO>> SaveRules([FromBody] MembershipRulesDTO rules, CancellationToken ct) => Ok(await settings.SaveRulesAsync(Caller, rules, ct));

    /// <summary>ฉบับความยินยอม</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ฉบับทั้งหมด</returns>
    [HttpGet("settings/consents")]
    [RequirePermission(P.SettingsManage)]
    public async Task<ActionResult<IReadOnlyList<ConsentVersionDTO>>> Consents(CancellationToken ct) => Ok(await settings.ListConsentsAsync(Caller, ct));

    /// <summary>ร่างฉบับใหม่</summary>
    /// <param name="request">หัวข้อ เนื้อหา วันมีผล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ฉบับที่สร้าง</returns>
    [HttpPost("settings/consents")]
    [RequirePermission(P.SettingsManage)]
    [ProducesResponseType<ConsentVersionDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ConsentVersionDTO>> CreateConsent([FromBody] ConsentVersionRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await settings.CreateConsentAsync(Caller, request, ct));

    /// <summary>เผยแพร่ฉบับ (แก้ไม่ได้อีก)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ฉบับ</returns>
    [HttpPost("settings/consents/{id:long}/publish")]
    [RequirePermission(P.SettingsManage)]
    public async Task<ActionResult<ConsentVersionDTO>> PublishConsent(long id, CancellationToken ct) => Ok(await settings.PublishConsentAsync(Caller, id, ct));

    /// <summary>ระบบภายนอกที่เรียก API</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ระบบภายนอก</returns>
    [HttpGet("settings/integrations")]
    [RequirePermission(P.SettingsManage)]
    public async Task<ActionResult<IReadOnlyList<IntegrationClientDTO>>> Integrations(CancellationToken ct) => Ok(await settings.ListIntegrationsAsync(Caller, ct));

    /// <summary>เพิ่ม/แก้ระบบภายนอก</summary>
    /// <param name="client">ระบบภายนอก</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ระบบภายนอกทั้งหมด</returns>
    [HttpPut("settings/integrations")]
    [RequirePermission(P.SettingsManage)]
    public async Task<ActionResult<IReadOnlyList<IntegrationClientDTO>>> SaveIntegration([FromBody] IntegrationClientDTO client, CancellationToken ct) => Ok(await settings.SaveIntegrationAsync(Caller, client, ct));

    /// <summary>บทบาทและสิทธิ์ที่แนะนำให้สร้างใน AuthServer</summary>
    /// <returns>บทบาท</returns>
    [HttpGet("settings/roles")]
    [RequirePermission(P.SettingsManage)]
    public ActionResult<IReadOnlyList<RoleTemplateDTO>> Roles() => Ok(settings.ListRoleTemplates(Caller));

    /// <summary>ค้น audit log ของระบบสมาชิก</summary>
    /// <param name="criteria">เงื่อนไข</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลแบบแบ่งหน้า</returns>
    [HttpGet("audit")]
    [RequirePermission(P.AuditRead)]
    public async Task<ActionResult<PagedResultDTO<MembershipAuditDTO>>> Audit([FromQuery] AuditSearchCriteria criteria, CancellationToken ct) => Ok(await settings.SearchAuditAsync(Caller, criteria, ct));
}

/// <summary>คำขอสิทธิ์ PDPA — <c>api/v1/privacy-requests</c> (§24)</summary>
/// <param name="privacy">บริการคำขอ PDPA</param>
[Route("api/v1/privacy-requests")]
public sealed class PrivacyRequestsController(PrivacyRequestService privacy) : MembershipControllerBase
{
    /// <summary>คำขอทั้งหมด</summary>
    /// <param name="status">สถานะ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    [HttpGet]
    [RequirePermission(P.PrivacyManage)]
    public async Task<ActionResult<IReadOnlyList<PrivacyRequestDTO>>> List([FromQuery] PrivacyRequestStatus? status, CancellationToken ct) => Ok(await privacy.ListAsync(Caller, status, ct));

    /// <summary>อ่านคำขอ</summary>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    [HttpGet("{id:long}")]
    [RequirePermission(P.PrivacyManage)]
    public async Task<ActionResult<PrivacyRequestDTO>> Get(long id, CancellationToken ct) => Ok(await privacy.GetAsync(Caller, id, ct));

    /// <summary>ปิดคำขอ (Erase = ลบข้อมูลส่วนบุคคลและปิดบัญชี)</summary>
    /// <param name="id">รหัส</param>
    /// <param name="request">ผลและบันทึก</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    [HttpPost("{id:long}/complete")]
    [RequirePermission(P.PrivacyManage)]
    public async Task<ActionResult<PrivacyRequestDTO>> Complete(long id, [FromBody] CompletePrivacyRequest request, CancellationToken ct) => Ok(await privacy.CompleteAsync(Caller, id, request, ct));
}
