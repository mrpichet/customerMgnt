using Common.AspNetCore.Authentication;
using Common.Core.Domain;
using CustomerMgnt.Api.Infrastructure;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Common.Core.Exceptions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;
using A = CustomerMgnt.Core.MemberAnalytics.Services.AnaPermissions;

namespace CustomerMgnt.Api.Controllers;

// ระบบวิเคราะห์ข้อมูลสมาชิกและโปรโมชัน — Claude.MD ส่วนที่ 3 (§28–39)
// ตัวกรองร่วม (§28.1) รับจาก query string; ingest รับ body เป็น JSON ดิบแล้วแปลงเอง (ไม่ให้ model validation ปัดตกทั้ง batch) เพราะ service ตรวจทีละรายการ; สิทธิ์ระดับหน้าตรวจที่ attribute ส่วนสิทธิ์ผสม (เช่น ana.member.detail ตาม level) ตรวจใน service

/// <summary>Dashboard — <c>api/v1/analytics/dashboards</c> (§30)</summary>
/// <param name="query">บริการ query</param>
[Route("api/v1/analytics/dashboards")]
[Authorize]
[EnableRateLimiting(MembershipApi.AnalyticsRateLimit)]
public sealed class AnaDashboardsController(AnalyticsQueryService query) : MembershipControllerBase
{
    /// <summary>AN-01 ภาพรวมผู้บริหาร</summary>
    [HttpGet("executive")]
    [RequirePermission(A.DashboardRead)]
    public async Task<ActionResult<AnalyticsResultDTO<ExecutiveDashboardDTO>>> Executive([FromQuery] AnalyticsFilterDTO filter, CancellationToken ct) =>
        Ok(await query.ExecutiveAsync(Caller, filter, ct));

    /// <summary>AN-02 ภาพรวมสมาชิก</summary>
    [HttpGet("members")]
    [RequirePermission(A.MemberRead)]
    public async Task<ActionResult<AnalyticsResultDTO<MemberDashboardDTO>>> Members([FromQuery] AnalyticsFilterDTO filter, CancellationToken ct) =>
        Ok(await query.MemberDashboardAsync(Caller, filter, ct));

    /// <summary>AN-03 ภาพรวมโปรโมชัน (เฉพาะแคมเปญที่เห็นได้)</summary>
    [HttpGet("promotions")]
    [RequirePermission(A.PromotionRead)]
    public async Task<ActionResult<AnalyticsResultDTO<PromotionDashboardDTO>>> Promotions([FromQuery] AnalyticsFilterDTO filter, CancellationToken ct) =>
        Ok(await query.PromotionDashboardAsync(Caller, filter, ct));
}

/// <summary>วิเคราะห์สมาชิก — <c>api/v1/analytics/members</c> (§31)</summary>
/// <param name="query">บริการ query</param>
[Route("api/v1/analytics/members")]
[Authorize]
[EnableRateLimiting(MembershipApi.AnalyticsRateLimit)]
public sealed class AnaMembersController(AnalyticsQueryService query) : MembershipControllerBase
{
    /// <summary>AN-04 กลุ่มตามพฤติกรรม (<c>to</c> = วันที่อ้างอิง)</summary>
    [HttpGet("activity-groups")]
    [RequirePermission(A.MemberRead)]
    public async Task<ActionResult<AnalyticsResultDTO<ActivityGroupsDTO>>> ActivityGroups([FromQuery] AnalyticsFilterDTO filter, CancellationToken ct) =>
        Ok(await query.ActivityGroupsAsync(Caller, filter, ct));

    /// <summary>AN-05 RFM</summary>
    [HttpGet("rfm")]
    [RequirePermission(A.MemberRead)]
    public async Task<ActionResult<AnalyticsResultDTO<RfmDTO>>> Rfm([FromQuery] AnalyticsFilterDTO filter, CancellationToken ct) => Ok(await query.RfmAsync(Caller, filter, ct));

    /// <summary>AN-05 บันทึกกลุ่ม RFM เป็น Segment แบบ snapshot (ต้องมี segment.write)</summary>
    [HttpPost("rfm/segments")]
    [RequirePermission(A.MemberRead)]
    [ProducesResponseType<SegmentSnapshotResultDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<SegmentSnapshotResultDTO>> SaveRfmSegment([FromBody] RfmSegmentRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await query.SaveRfmSegmentAsync(Caller, request, ct));

    /// <summary>AN-06 cohort</summary>
    [HttpGet("cohorts")]
    [RequirePermission(A.MemberRead)]
    public async Task<ActionResult<AnalyticsResultDTO<CohortDTO>>> Cohorts([FromQuery] AnalyticsFilterDTO filter, [FromQuery] CohortBy cohortBy = CohortBy.Registration,
        [FromQuery] CohortMeasure measure = CohortMeasure.Retention, [FromQuery] int months = 12, CancellationToken ct = default) =>
        Ok(await query.CohortsAsync(Caller, filter, cohortBy, measure, months, ct));

    /// <summary>AN-07 การซื้อซ้ำ</summary>
    [HttpGet("repeat-purchase")]
    [RequirePermission(A.MemberRead)]
    public async Task<ActionResult<AnalyticsResultDTO<RepeatPurchaseDTO>>> RepeatPurchase([FromQuery] AnalyticsFilterDTO filter, [FromQuery] int? firstPurchaseDays, CancellationToken ct) =>
        Ok(await query.RepeatPurchaseAsync(Caller, filter, firstPurchaseDays, ct));

    /// <summary>AN-08 มูลค่าการซื้อ</summary>
    [HttpGet("purchase-value")]
    [RequirePermission(A.MemberRead)]
    public async Task<ActionResult<AnalyticsResultDTO<PurchaseValueDTO>>> PurchaseValue([FromQuery] AnalyticsFilterDTO filter, [FromQuery] ValueBreakdown breakdown = ValueBreakdown.Tier,
        CancellationToken ct = default) =>
        Ok(await query.PurchaseValueAsync(Caller, filter, breakdown, ct));

    /// <summary>AN-15 แหล่งที่มาของสมาชิก</summary>
    [HttpGet("sources")]
    [RequirePermission(A.MemberRead)]
    public async Task<ActionResult<AnalyticsResultDTO<MemberSourceDTO>>> Sources([FromQuery] AnalyticsFilterDTO filter, CancellationToken ct) => Ok(await query.SourcesAsync(Caller, filter, ct));
}

/// <summary>คะแนน คูปอง แคมเปญ drill-down ตัวกรอง รายงาน ตั้งเวลา ส่งออก — <c>api/v1/analytics</c> (§32–35)</summary>
/// <param name="query">บริการ query</param>
/// <param name="reports">บริการรายงาน</param>
[Route("api/v1/analytics")]
[Authorize]
[EnableRateLimiting(MembershipApi.AnalyticsRateLimit)]
public sealed class AnaAnalyticsController(AnalyticsQueryService query, AnaReportService reports) : MembershipControllerBase
{
    // ------------------------------------------------------------ §32

    /// <summary>AN-10 คะแนน</summary>
    [HttpGet("points")]
    [RequirePermission(A.DashboardRead)]
    public async Task<ActionResult<AnalyticsResultDTO<PointsAnalyticsDTO>>> Points([FromQuery] AnalyticsFilterDTO filter, CancellationToken ct) => Ok(await query.PointsAsync(Caller, filter, ct));

    /// <summary>AN-11 คูปอง</summary>
    [HttpGet("coupons")]
    [RequirePermission(A.DashboardRead)]
    public async Task<ActionResult<AnalyticsResultDTO<CouponAnalyticsDTO>>> Coupons([FromQuery] AnalyticsFilterDTO filter, [FromQuery] string? campaign, [FromQuery] long? batchId,
        CancellationToken ct) =>
        Ok(await query.CouponsAsync(Caller, filter, campaign, batchId, ct));

    // ------------------------------------------------------------ §33

    /// <summary>AN-12 รายการแคมเปญ</summary>
    [HttpGet("campaigns")]
    [RequirePermission(A.PromotionRead)]
    public async Task<ActionResult<AnalyticsResultDTO<PagedResultDTO<CampaignAnalyticsSummaryDTO>>>> Campaigns([FromQuery] AnalyticsFilterDTO filter, [FromQuery] CampaignAnalyticsCriteria criteria,
        CancellationToken ct) =>
        Ok(await query.CampaignsAsync(Caller, filter, criteria, ct));

    /// <summary>AN-14 เทียบแคมเปญ 2–5 แคมเปญ</summary>
    [HttpGet("campaigns/compare")]
    [RequirePermission(A.PromotionRead)]
    public async Task<ActionResult<AnalyticsResultDTO<CampaignCompareDTO>>> Compare([FromQuery] List<string> codes, [FromQuery] AnalyticsFilterDTO filter,
        [FromQuery] Attribution attribution = Attribution.BenefitUsed, CancellationToken ct = default) =>
        Ok(await query.CompareCampaignsAsync(Caller, codes, filter, attribution, ct));

    /// <summary>AN-13 ภาพรวมแคมเปญ</summary>
    [HttpGet("campaigns/{code}")]
    [RequirePermission(A.PromotionRead)]
    public async Task<ActionResult<AnalyticsResultDTO<CampaignReportDTO>>> Campaign(string code, [FromQuery] AnalyticsFilterDTO filter, [FromQuery] Attribution attribution = Attribution.BenefitUsed,
        CancellationToken ct = default) =>
        Ok(await query.CampaignReportAsync(Caller, code, filter, attribution, ct));

    /// <summary>AN-13 แยกมิติ</summary>
    [HttpGet("campaigns/{code}/breakdown")]
    [RequirePermission(A.PromotionRead)]
    public async Task<ActionResult<AnalyticsResultDTO<List<BreakdownRowDTO>>>> Breakdown(string code, [FromQuery] AnalyticsFilterDTO filter, [FromQuery] CampaignDimension dimension = CampaignDimension.Branch,
        [FromQuery] Attribution attribution = Attribution.BenefitUsed, CancellationToken ct = default) =>
        Ok(await query.CampaignBreakdownAsync(Caller, code, filter, dimension, attribution, ct));

    /// <summary>AN-13 กลุ่มควบคุม</summary>
    [HttpGet("campaigns/{code}/lift")]
    [RequirePermission(A.PromotionRead)]
    public async Task<ActionResult<AnalyticsResultDTO<CampaignLiftDTO>>> Lift(string code, [FromQuery] AnalyticsFilterDTO filter, CancellationToken ct) =>
        Ok(await query.CampaignLiftAsync(Caller, code, filter, ct));

    /// <summary>AN-13 ต้นทุน / ROI</summary>
    [HttpGet("campaigns/{code}/roi")]
    [RequirePermission(A.PromotionRead)]
    public async Task<ActionResult<AnalyticsResultDTO<CampaignRoiDTO>>> Roi(string code, [FromQuery] AnalyticsFilterDTO filter, CancellationToken ct) =>
        Ok(await query.CampaignRoiAsync(Caller, code, filter, ct));

    // ------------------------------------------------------------ §34

    /// <summary>AN-09 drill-down (สิทธิ์ตาม metric; level=Member ต้องมี ana.member.detail)</summary>
    [HttpGet("drilldown")]
    public async Task<ActionResult<AnalyticsResultDTO<DrilldownPageDTO>>> Drilldown([FromQuery] AnalyticsFilterDTO filter, [FromQuery] DrilldownQuery drill, CancellationToken ct) =>
        Ok(await query.DrilldownAsync(Caller, filter, drill, ct));

    /// <summary>บันทึกรายชื่อจาก drill-down เป็น Segment (segment.write + ana.member.detail)</summary>
    [HttpPost("drilldown/segments")]
    [RequirePermission(A.MemberDetail)]
    [ProducesResponseType<SegmentSnapshotResultDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<SegmentSnapshotResultDTO>> SaveDrilldownSegment([FromBody] DrilldownSegmentRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await query.SaveDrilldownSegmentAsync(Caller, request, ct));

    // ------------------------------------------------------------ §35 ตัวกรองที่บันทึก

    /// <summary>ตัวกรองที่บันทึกของฉัน</summary>
    [HttpGet("saved-filters")]
    public async Task<ActionResult<IReadOnlyList<SavedFilterDTO>>> SavedFilters([FromQuery] string? page, CancellationToken ct) => Ok(await reports.ListSavedFiltersAsync(Caller, page, ct));

    /// <summary>บันทึกตัวกรอง (≤ 50 ต่อคนต่อหน้า)</summary>
    [HttpPost("saved-filters")]
    [ProducesResponseType<SavedFilterDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<SavedFilterDTO>> CreateSavedFilter([FromBody] SavedFilterDTO filter, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await reports.CreateSavedFilterAsync(Caller, filter, ct));

    /// <summary>แก้ตัวกรอง (เจ้าของ, ตรวจ version)</summary>
    [HttpPut("saved-filters/{id:guid}")]
    public async Task<ActionResult<SavedFilterDTO>> UpdateSavedFilter(Guid id, [FromBody] SavedFilterDTO filter, CancellationToken ct) => Ok(await reports.UpdateSavedFilterAsync(Caller, id, filter, ct));

    /// <summary>ลบตัวกรอง</summary>
    [HttpDelete("saved-filters/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSavedFilter(Guid id, CancellationToken ct)
    {
        await reports.DeleteSavedFilterAsync(Caller, id, ct);
        return NoContent();
    }

    // ------------------------------------------------------------ §35 รายงาน

    /// <summary>metric และ dimension ที่ใช้ได้ตามสิทธิ์</summary>
    [HttpGet("catalog")]
    public ActionResult<MetricCatalogDTO> Catalog() => Ok(reports.Catalog(Caller));

    /// <summary>AN-18 รายงาน (มาตรฐาน + ของตน + ที่แชร์ให้)</summary>
    [HttpGet("reports")]
    public async Task<ActionResult<IReadOnlyList<ReportSummaryDTO>>> Reports([FromQuery] bool? mine, [FromQuery] bool? standard, [FromQuery] string? text, CancellationToken ct) =>
        Ok(await reports.ListReportsAsync(Caller, mine, standard, text, ct));

    /// <summary>อ่านรายงาน</summary>
    [HttpGet("reports/{id:guid}")]
    public async Task<ActionResult<ReportDefinitionDTO>> Report(Guid id, CancellationToken ct) => Ok(await reports.GetReportAsync(Caller, id, ct));

    /// <summary>AN-20 สร้างรายงาน</summary>
    [HttpPost("reports")]
    [RequirePermission(A.ReportBuild)]
    [ProducesResponseType<ReportDefinitionDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ReportDefinitionDTO>> CreateReport([FromBody] ReportDefinitionDTO report, CancellationToken ct)
    {
        ReportDefinitionDTO r = await reports.CreateReportAsync(Caller, report, ct);
        return CreatedAtAction(nameof(Report), new { id = r.Id }, r);
    }

    /// <summary>แก้รายงาน (รายงานมาตรฐาน = 409 standard_report)</summary>
    [HttpPut("reports/{id:guid}")]
    [RequirePermission(A.ReportBuild)]
    public async Task<ActionResult<ReportDefinitionDTO>> UpdateReport(Guid id, [FromBody] ReportDefinitionDTO report, CancellationToken ct) => Ok(await reports.UpdateReportAsync(Caller, id, report, ct));

    /// <summary>ทำสำเนารายงาน</summary>
    [HttpPost("reports/{id:guid}/copy")]
    [RequirePermission(A.ReportBuild)]
    [ProducesResponseType<ReportDefinitionDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ReportDefinitionDTO>> CopyReport(Guid id, [FromBody] ReportCopyRequest request, CancellationToken ct)
    {
        ReportDefinitionDTO r = await reports.CopyReportAsync(Caller, id, request, ct);
        return CreatedAtAction(nameof(Report), new { id = r.Id }, r);
    }

    /// <summary>ลบรายงาน (ลบตั้งเวลาด้วย)</summary>
    [HttpDelete("reports/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteReport(Guid id, CancellationToken ct)
    {
        await reports.DeleteReportAsync(Caller, id, ct);
        return NoContent();
    }

    /// <summary>แชร์รายงาน (แทนที่ทั้งชุด)</summary>
    [HttpPut("reports/{id:guid}/shares")]
    [RequirePermission(A.ReportShare)]
    public async Task<ActionResult<IReadOnlyList<ReportShareDTO>>> ShareReport(Guid id, [FromBody] List<ReportShareDTO> shares, CancellationToken ct) =>
        Ok(await reports.ShareReportAsync(Caller, id, shares, ct));

    /// <summary>AN-19 เรียกดูรายงาน (ตามขอบเขตของผู้เปิด)</summary>
    [HttpGet("reports/{id:guid}/run")]
    public async Task<ActionResult<AnalyticsResultDTO<ReportResultDTO>>> RunReport(Guid id, [FromQuery] AnalyticsFilterDTO filter, [FromQuery] int page = 1, [FromQuery] int pageSize = 100,
        CancellationToken ct = default) =>
        Ok(await reports.RunReportAsync(Caller, id, filter, page, pageSize, ct));

    // ------------------------------------------------------------ §35 ตั้งเวลา

    /// <summary>AN-21 ตั้งเวลาของฉัน</summary>
    [HttpGet("schedules")]
    [RequirePermission(A.ReportBuild)]
    public async Task<ActionResult<IReadOnlyList<ReportScheduleDTO>>> Schedules(CancellationToken ct) => Ok(await reports.ListSchedulesAsync(Caller, ct));

    /// <summary>สร้างตั้งเวลา</summary>
    [HttpPost("schedules")]
    [RequirePermission(A.ReportBuild)]
    [ProducesResponseType<ReportScheduleDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ReportScheduleDTO>> CreateSchedule([FromBody] ReportScheduleDTO schedule, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await reports.CreateScheduleAsync(Caller, schedule, ct));

    /// <summary>แก้ตั้งเวลา (เจ้าของ)</summary>
    [HttpPut("schedules/{id:guid}")]
    [RequirePermission(A.ReportBuild)]
    public async Task<ActionResult<ReportScheduleDTO>> UpdateSchedule(Guid id, [FromBody] ReportScheduleDTO schedule, CancellationToken ct) => Ok(await reports.UpdateScheduleAsync(Caller, id, schedule, ct));

    /// <summary>ลบตั้งเวลา</summary>
    [HttpDelete("schedules/{id:guid}")]
    [RequirePermission(A.ReportBuild)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSchedule(Guid id, CancellationToken ct)
    {
        await reports.DeleteScheduleAsync(Caller, id, ct);
        return NoContent();
    }

    /// <summary>ส่งทันที</summary>
    [HttpPost("schedules/{id:guid}/run-now")]
    [RequirePermission(A.ReportBuild)]
    [ProducesResponseType<AnaExportJobDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<AnaExportJobDTO>> RunScheduleNow(Guid id, CancellationToken ct) => Accepted(await reports.RunScheduleNowAsync(Caller, id, ct));

    // ------------------------------------------------------------ §35 ส่งออก

    /// <summary>ขอส่งออก (ทำเบื้องหลัง)</summary>
    [HttpPost("exports")]
    [RequirePermission(A.Export)]
    [ProducesResponseType<AnaExportJobDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<AnaExportJobDTO>> RequestExport([FromBody] AnaExportRequest request, CancellationToken ct) => Accepted(await reports.RequestExportAsync(Caller, request, ct));

    /// <summary>AN-22 งานส่งออก (ของตน; audit.read + mine=false = ทุกคน)</summary>
    [HttpGet("exports")]
    [RequirePermission(A.Export)]
    public async Task<ActionResult<IReadOnlyList<AnaExportJobDTO>>> Exports([FromQuery] bool mine = true, CancellationToken ct = default) => Ok(await reports.ListExportsAsync(Caller, mine, ct));

    /// <summary>ดาวน์โหลดไฟล์ (เจ้าของงาน; หมดอายุ = 410 export_expired)</summary>
    [HttpGet("exports/{id:long}/file")]
    [RequirePermission(A.Export)]
    [Produces("application/octet-stream")]
    public async Task<IActionResult> Download(long id, CancellationToken ct)
    {
        (string name, string type, byte[] content) = await reports.DownloadAsync(Caller, id, ct);
        Response.Headers.CacheControl = "no-store";
        return File(content, type, name);
    }
}

/// <summary>Funnel — <c>api/v1/analytics/funnels</c> (§37)</summary>
/// <param name="query">บริการ query</param>
/// <param name="admin">บริการตั้งค่า</param>
[Route("api/v1/analytics/funnels")]
[Authorize]
[EnableRateLimiting(MembershipApi.AnalyticsRateLimit)]
public sealed class AnaFunnelsController(AnalyticsQueryService query, AnaAdminService admin) : MembershipControllerBase
{
    /// <summary>funnel ทั้งหมด</summary>
    [HttpGet]
    [RequirePermission(A.PromotionRead)]
    public async Task<ActionResult<IReadOnlyList<FunnelDTO>>> List(CancellationToken ct) => Ok(await admin.ListFunnelsAsync(Caller, ct));

    /// <summary>อ่าน funnel</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(A.PromotionRead)]
    public async Task<ActionResult<FunnelDTO>> Get(Guid id, CancellationToken ct) => Ok(await admin.GetFunnelAsync(Caller, id, ct));

    /// <summary>AN-16 ผล funnel (แคมเปญที่มองไม่เห็นถูกตัด)</summary>
    [HttpGet("{id:guid}/result")]
    [RequirePermission(A.PromotionRead)]
    public async Task<ActionResult<AnalyticsResultDTO<FunnelResultDTO>>> Result(Guid id, [FromQuery] AnalyticsFilterDTO filter, CancellationToken ct) =>
        Ok(await query.FunnelResultAsync(Caller, id, filter, ct));

    /// <summary>AN-16F สร้าง funnel</summary>
    [HttpPost]
    [RequirePermission(A.SettingsManage)]
    [ProducesResponseType<FunnelDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<FunnelDTO>> Create([FromBody] FunnelDTO funnel, CancellationToken ct)
    {
        FunnelDTO f = await admin.CreateFunnelAsync(Caller, funnel, ct);
        return CreatedAtAction(nameof(Get), new { id = f.Id }, f);
    }

    /// <summary>แก้ funnel</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(A.SettingsManage)]
    public async Task<ActionResult<FunnelDTO>> Update(Guid id, [FromBody] FunnelDTO funnel, CancellationToken ct) => Ok(await admin.UpdateFunnelAsync(Caller, id, funnel, ct));

    /// <summary>ลบ funnel</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(A.SettingsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await admin.DeleteFunnelAsync(Caller, id, ct);
        return NoContent();
    }
}

/// <summary>แจ้งเตือน — <c>api/v1/analytics/alerts</c> (§38)</summary>
/// <param name="alerts">บริการแจ้งเตือน</param>
[Route("api/v1/analytics/alerts")]
[Authorize]
[EnableRateLimiting(MembershipApi.AnalyticsRateLimit)]
public sealed class AnaAlertsController(AnaAlertService alerts) : MembershipControllerBase
{
    /// <summary>กฎทั้งหมด</summary>
    [HttpGet("rules")]
    [RequirePermission(A.AlertManage)]
    public async Task<ActionResult<IReadOnlyList<AlertRuleDTO>>> Rules(CancellationToken ct) => Ok(await alerts.ListRulesAsync(Caller, ct));

    /// <summary>อ่านกฎ</summary>
    [HttpGet("rules/{id:guid}")]
    [RequirePermission(A.AlertManage)]
    public async Task<ActionResult<AlertRuleDTO>> Rule(Guid id, CancellationToken ct) => Ok(await alerts.GetRuleAsync(Caller, id, ct));

    /// <summary>AN-24 สร้างกฎ</summary>
    [HttpPost("rules")]
    [RequirePermission(A.AlertManage)]
    [ProducesResponseType<AlertRuleDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AlertRuleDTO>> Create([FromBody] AlertRuleDTO rule, CancellationToken ct)
    {
        AlertRuleDTO r = await alerts.CreateRuleAsync(Caller, rule, ct);
        return CreatedAtAction(nameof(Rule), new { id = r.Id }, r);
    }

    /// <summary>แก้กฎ</summary>
    [HttpPut("rules/{id:guid}")]
    [RequirePermission(A.AlertManage)]
    public async Task<ActionResult<AlertRuleDTO>> Update(Guid id, [FromBody] AlertRuleDTO rule, CancellationToken ct) => Ok(await alerts.UpdateRuleAsync(Caller, id, rule, ct));

    /// <summary>ลบกฎ</summary>
    [HttpDelete("rules/{id:guid}")]
    [RequirePermission(A.AlertManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await alerts.DeleteRuleAsync(Caller, id, ct);
        return NoContent();
    }

    /// <summary>ประเมินทันที (ไม่ส่งข้อความ)</summary>
    [HttpPost("rules/{id:guid}/test")]
    [RequirePermission(A.AlertManage)]
    public async Task<ActionResult<AlertTestResultDTO>> Test(Guid id, CancellationToken ct) => Ok(await alerts.TestRuleAsync(Caller, id, ct));

    /// <summary>AN-23 เหตุการณ์แจ้งเตือน (เฉพาะขอบเขตของผู้เรียก)</summary>
    [HttpGet("events")]
    [RequirePermission(A.AlertManage)]
    public async Task<ActionResult<PagedResultDTO<AlertEventDTO>>> Events([FromQuery] AlertEventCriteria criteria, CancellationToken ct) => Ok(await alerts.ListEventsAsync(Caller, criteria, ct));
}

/// <summary>ตั้งค่า / ขอบเขต / สถานะข้อมูล — <c>api/v1/analytics/admin</c> (§39)</summary>
/// <param name="admin">บริการตั้งค่า</param>
/// <param name="scopes">บริการขอบเขตข้อมูล</param>
[Route("api/v1/analytics/admin")]
[Authorize]
[EnableRateLimiting(MembershipApi.AnalyticsRateLimit)]
public sealed class AnaAdminController(AnaAdminService admin, DataScopeService scopes) : MembershipControllerBase
{
    /// <summary>AN-25 ค่าตั้ง</summary>
    [HttpGet("settings")]
    [RequirePermission(A.SettingsManage)]
    public async Task<ActionResult<AnalyticsSettingsDTO>> Settings(CancellationToken ct) => Ok(await admin.GetSettingsAsync(Caller, ct));

    /// <summary>บันทึกค่าตั้ง (read model ถูกทำเครื่องหมายคำนวณใหม่ย้อนหลัง recomputeDays)</summary>
    [HttpPut("settings")]
    [RequirePermission(A.SettingsManage)]
    public async Task<ActionResult<AnalyticsSettingsDTO>> SaveSettings([FromBody] AnalyticsSettingsDTO value, CancellationToken ct) => Ok(await admin.SaveSettingsAsync(Caller, value, ct));

    /// <summary>AN-26 สถานะข้อมูล</summary>
    [HttpGet("data-status")]
    [RequirePermission(A.DashboardRead)]
    public async Task<ActionResult<DataStatusDTO>> DataStatus(CancellationToken ct) => Ok(await admin.DataStatusAsync(Caller, ct));

    /// <summary>ปัญหาข้อมูล</summary>
    [HttpGet("data-issues")]
    [RequirePermission(A.DashboardRead)]
    public async Task<ActionResult<PagedResultDTO<DataIssueDTO>>> DataIssues([FromQuery] string? kind, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Ok(await admin.DataIssuesAsync(Caller, kind, page, pageSize, ct));

    /// <summary>สั่งคำนวณใหม่ (≤ 731 วัน; ช่วงทับงานที่รออยู่ = 409 refresh_running)</summary>
    [HttpPost("refresh")]
    [RequirePermission(A.SettingsManage)]
    [ProducesResponseType<RefreshRunDTO>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<RefreshRunDTO>> Refresh([FromBody] RefreshRequest request, CancellationToken ct) => Accepted(await admin.RequestRefreshAsync(Caller, request, ct));

    /// <summary>งานคำนวณใหม่ล่าสุด 50 งาน</summary>
    [HttpGet("refresh")]
    [RequirePermission(A.DashboardRead)]
    public async Task<ActionResult<IReadOnlyList<RefreshRunDTO>>> RefreshRuns(CancellationToken ct) => Ok(await admin.ListRefreshRunsAsync(Caller, ct));

    /// <summary>AN-27 กฎขอบเขตข้อมูล</summary>
    [HttpGet("data-scopes")]
    [RequirePermission(A.SettingsManage)]
    public async Task<ActionResult<IReadOnlyList<DataScopeRuleDTO>>> DataScopes([FromQuery] string? subject, CancellationToken ct) => Ok(await scopes.ListAsync(Caller, subject, ct));

    /// <summary>ขอบเขตรวมของผู้ใช้ (กฎของผู้ใช้ + role ที่ระบุ — AuthServer เป็นเจ้าของข้อมูล role)</summary>
    [HttpGet("data-scopes/effective")]
    [RequirePermission(A.SettingsManage)]
    public async Task<ActionResult<EffectiveScopeDTO>> Effective([FromQuery] string sub, [FromQuery] List<string>? roles, CancellationToken ct) =>
        Ok(await scopes.EffectiveAsync(sub, roles ?? [], ct));

    /// <summary>อ่านกฎขอบเขต</summary>
    [HttpGet("data-scopes/{id:guid}")]
    [RequirePermission(A.SettingsManage)]
    public async Task<ActionResult<DataScopeRuleDTO>> DataScope(Guid id, CancellationToken ct) => Ok(await scopes.GetAsync(Caller, id, ct));

    /// <summary>AN-28 สร้างกฎขอบเขต</summary>
    [HttpPost("data-scopes")]
    [RequirePermission(A.SettingsManage)]
    [ProducesResponseType<DataScopeRuleDTO>(StatusCodes.Status201Created)]
    public async Task<ActionResult<DataScopeRuleDTO>> CreateDataScope([FromBody] DataScopeRuleDTO rule, CancellationToken ct)
    {
        DataScopeRuleDTO r = await scopes.CreateAsync(Caller, rule, ct);
        return CreatedAtAction(nameof(DataScope), new { id = r.Id }, r);
    }

    /// <summary>แก้กฎขอบเขต</summary>
    [HttpPut("data-scopes/{id:guid}")]
    [RequirePermission(A.SettingsManage)]
    public async Task<ActionResult<DataScopeRuleDTO>> UpdateDataScope(Guid id, [FromBody] DataScopeRuleDTO rule, CancellationToken ct) => Ok(await scopes.UpdateAsync(Caller, id, rule, ct));

    /// <summary>ลบกฎขอบเขต</summary>
    [HttpDelete("data-scopes/{id:guid}")]
    [RequirePermission(A.SettingsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteDataScope(Guid id, CancellationToken ct)
    {
        await scopes.DeleteAsync(Caller, id, ct);
        return NoContent();
    }
}

/// <summary>รับข้อมูลจากระบบต้นทาง — <c>api/v1/ingest</c> (§36): scope analytics.ingest, บังคับ Idempotency-Key, ตอบผลรายรายการ</summary>
/// <param name="ingest">บริการรับข้อมูล</param>
[Route("api/v1/ingest")]
[Authorize]
[EnableRateLimiting(MembershipApi.IngestRateLimit)]
/// <param name="json">ตัวเลือก JSON ของ MVC</param>
public sealed class AnaIngestController(AnalyticsIngestService ingest, IOptions<JsonOptions> json) : MembershipControllerBase
{
    /// <summary>แปลง body เป็นรายการ — ต้องเป็น array; รูปแบบผิดทั้งก้อน = 400</summary>
    private List<T> Items<T>(JsonElement body)
    {
        ValidationException.ThrowIf(body.ValueKind != JsonValueKind.Array, "body", "The body must be a JSON array.");
        try
        {
            return body.Deserialize<List<T>>(json.Value.JsonSerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            throw new ValidationException("The body is not in the expected format.");
        }
    }

    /// <summary>master สินค้า (แบรนด์ หมวด ต้นทุน)</summary>
    [HttpPut("products")]
    [RequirePermission(A.Ingest)]
    [Idempotent]
    public async Task<ActionResult<IngestResultDTO>> Products([FromBody] JsonElement items, CancellationToken ct) => Ok(await ingest.ProductsAsync(Caller, Items<ProductIngestDTO>(items), ct));

    /// <summary>master สาขา + เวลาเปิด–ปิด</summary>
    [HttpPut("branches")]
    [RequirePermission(A.Ingest)]
    [Idempotent]
    public async Task<ActionResult<IngestResultDTO>> Branches([FromBody] JsonElement items, CancellationToken ct) => Ok(await ingest.BranchesAsync(Caller, Items<BranchIngestDTO>(items), ct));

    /// <summary>ต้นทุนแคมเปญ</summary>
    [HttpPost("campaign-costs")]
    [RequirePermission(A.Ingest)]
    [Idempotent]
    public async Task<ActionResult<IngestResultDTO>> CampaignCosts([FromBody] JsonElement items, CancellationToken ct) => Ok(await ingest.CampaignCostsAsync(Caller, Items<CampaignCostIngestDTO>(items), ct));

    /// <summary>เหตุการณ์ข้อความ / การดูโปรโมชัน</summary>
    [HttpPost("message-events")]
    [RequirePermission(A.Ingest)]
    [Idempotent]
    public async Task<ActionResult<IngestResultDTO>> MessageEvents([FromBody] JsonElement items, CancellationToken ct) => Ok(await ingest.MessageEventsAsync(Caller, Items<MessageEventIngestDTO>(items), ct));

    /// <summary>ยอดขายทั้งหมดรายวัน/สาขา/ช่องทาง</summary>
    [HttpPost("sales-summary")]
    [RequirePermission(A.Ingest)]
    [Idempotent]
    public async Task<ActionResult<IngestResultDTO>> SalesSummary([FromBody] JsonElement items, CancellationToken ct) => Ok(await ingest.SalesSummaryAsync(Caller, Items<SalesSummaryIngestDTO>(items), ct));
}
