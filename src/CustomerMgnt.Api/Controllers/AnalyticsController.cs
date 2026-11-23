using Common.AspNetCore.Authentication;
using CustomerMgnt.AI;
using CustomerMgnt.Core.Analytics;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

namespace CustomerMgnt.Api.Controllers;

/// <summary>
/// ML / analytics: lead scoring, churn, MA renewal/upsell, sales forecast, งบตามฤดูกาล, next best action,
/// recommendation, send-time optimization, dynamic segmentation, sentiment และ risk score
/// </summary>
/// <param name="predictions">โมเดลทำนาย (lead/churn/renewal)</param>
/// <param name="forecast">พยากรณ์อนุกรมเวลา</param>
/// <param name="nba">next best action</param>
/// <param name="recommendations">ระบบแนะนำ</param>
/// <param name="sendTime">send-time optimization</param>
/// <param name="segmentation">dynamic segmentation</param>
/// <param name="sentiment">sentiment analysis</param>
/// <param name="risk">risk score ลูกค้า</param>
[Route("api/analytics")]
public sealed class AnalyticsController(
    PredictionService predictions,
    ForecastService forecast,
    NextBestActionService nba,
    RecommendationService recommendations,
    SendTimeOptimizationService sendTime,
    SegmentationService segmentation,
    SentimentAnalysisService sentiment,
    CustomerRiskService risk) : CustomerMgntControllerBase
{
    /// <summary>คะแนน lead (โอกาสปิดการขาย) พร้อมปัจจัยที่มีผล</summary>
    /// <param name="customerId">เฉพาะลูกค้า</param>
    /// <param name="top">จำนวนสูงสุด (1-1000)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คะแนนเรียงจากสูงไปต่ำ</returns>
    [HttpGet("leads")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<IReadOnlyList<LeadScoreDTO>>> Leads([FromQuery] long? customerId, [FromQuery] int top = 100, CancellationToken ct = default) =>
        Ok(await predictions.ScoreLeadsAsync(CurrentUser, customerId, Math.Clamp(top, 1, 1000), ct));

    /// <summary>ความเสี่ยงลูกค้าเลิกใช้บริการ (churn)</summary>
    /// <param name="customerId">เฉพาะลูกค้า</param>
    /// <param name="minProbability">ความน่าจะเป็นขั้นต่ำ (0-1)</param>
    /// <param name="top">จำนวนสูงสุด (1-1000)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้าเรียงตามความเสี่ยง</returns>
    [HttpGet("churn")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<IReadOnlyList<ChurnRiskDTO>>> Churn([FromQuery] long? customerId, [FromQuery] double minProbability = 0, [FromQuery] int top = 100, CancellationToken ct = default) =>
        Ok(await predictions.PredictChurnAsync(CurrentUser, customerId, Math.Clamp(minProbability, 0, 1), Math.Clamp(top, 1, 1000), ct));

    /// <summary>พยากรณ์การต่อสัญญา MA และโอกาส upsell ของสัญญาที่จะหมดอายุ</summary>
    /// <param name="withinDays">ช่วงวันที่หมดอายุ (1-730)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุปการต่อสัญญา</returns>
    [HttpGet("renewals")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<RenewalSummaryDTO>> Renewals([FromQuery] int withinDays = 180, CancellationToken ct = default) =>
        Ok(await predictions.ForecastRenewalsAsync(CurrentUser, Math.Clamp(withinDays, 1, 730), ct));

    /// <summary>พยากรณ์ยอดขายรายเดือน (Holt-Winters + pipeline ถ่วงน้ำหนัก)</summary>
    /// <param name="horizonMonths">จำนวนเดือน (1-24)</param>
    /// <param name="customerId">เฉพาะลูกค้า</param>
    /// <param name="includePipeline">รวม pipeline ที่ยังเปิด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>พยากรณ์พร้อมช่วงความเชื่อมั่น 80%</returns>
    [HttpGet("forecast/sales")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<ForecastDTO>> ForecastSales([FromQuery] int horizonMonths = 6, [FromQuery] long? customerId = null, [FromQuery] bool includePipeline = true, CancellationToken ct = default) =>
        Ok(await forecast.ForecastSalesAsync(CurrentUser, Math.Clamp(horizonMonths, 1, 24), customerId, includePipeline, ct));

    /// <summary>พยากรณ์งบประมาณ/การใช้จ่ายของลูกค้าตามฤดูกาล</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="horizonMonths">จำนวนเดือน (1-24)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>พยากรณ์ + ดัชนีฤดูกาล</returns>
    [HttpGet("forecast/budget/{customerId:long}")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<ForecastDTO>> ForecastBudget(long customerId, [FromQuery] int horizonMonths = 12, CancellationToken ct = default) =>
        Ok(await forecast.ForecastSeasonalBudgetAsync(CurrentUser, customerId, Math.Clamp(horizonMonths, 1, 24), ct));

    /// <summary>สิ่งที่ควรทำต่อไปกับลูกค้า (next best action)</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="max">จำนวนสูงสุด (1-10)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>การกระทำเรียงตามความสำคัญ</returns>
    [HttpGet("nba/{customerId:long}")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<IReadOnlyList<NextBestActionDTO>>> NextBestAction(long customerId, [FromQuery] int max = 3, CancellationToken ct = default) =>
        Ok(await nba.RecommendAsync(CurrentUser, customerId, Math.Clamp(max, 1, 10), ct));

    /// <summary>แนะนำสินค้า/บริการ/บทความ (item-based collaborative filtering)</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="top">จำนวน (1-50)</param>
    /// <param name="kind">ชนิดรายการ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการแนะนำ</returns>
    [HttpGet("recommendations/{customerId:long}")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<IReadOnlyList<RecommendationDTO>>> Recommend(long customerId, [FromQuery] int top = 5, [FromQuery] CatalogItemKind? kind = null, CancellationToken ct = default) =>
        Ok(await recommendations.RecommendAsync(CurrentUser, customerId, Math.Clamp(top, 1, 50), kind, ct));

    /// <summary>เวลาที่เหมาะสมที่สุดในการส่งข้อความหาลูกค้า</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="channel">ช่องทาง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>วัน/ชั่วโมงที่ดีที่สุดและเวลาส่งครั้งถัดไป</returns>
    [HttpGet("send-time/{customerId:long}")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<SendTimeDTO>> SendTime(long customerId, [FromQuery] ChannelKind channel = ChannelKind.Email, CancellationToken ct = default) =>
        Ok(await sendTime.GetBestTimeAsync(CurrentUser, customerId, channel, ct));

    /// <summary>แบ่งกลุ่มลูกค้าแบบ dynamic (k-means) — ไม่บันทึกผล</summary>
    /// <param name="segments">จำนวนกลุ่ม (ว่าง = ตามค่าตั้ง)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กลุ่มและสมาชิก</returns>
    [HttpGet("segments")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<SegmentationResultDTO>> Segments([FromQuery] int? segments, CancellationToken ct) =>
        Ok(await segmentation.SegmentAsync(CurrentUser, segments, persist: false, ct));

    /// <summary>แบ่งกลุ่มลูกค้าและบันทึกชื่อกลุ่มลงข้อมูลลูกค้า (ต้องมีสิทธิ์ customer.write ด้วย)</summary>
    /// <param name="segments">จำนวนกลุ่ม</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กลุ่มและสมาชิก</returns>
    [HttpPost("segments")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<SegmentationResultDTO>> PersistSegments([FromQuery] int? segments, CancellationToken ct) =>
        Ok(await segmentation.SegmentAsync(CurrentUser, segments, persist: true, ct));

    /// <summary>วิเคราะห์อารมณ์ข้อความ (ไทย/อังกฤษ)</summary>
    /// <param name="request">ข้อความ</param>
    /// <returns>คะแนนและป้ายอารมณ์</returns>
    [HttpPost("sentiment")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public ActionResult<SentimentDTO> Sentiment([FromBody] SentimentRequest request) => Ok(sentiment.Analyze(request.Text));

    /// <summary>คะแนนความเสี่ยงของลูกค้า (churn, SLA breach, อายุ issue, อารมณ์, สัญญาใกล้หมด, งานค้าง)</summary>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลประเมินพร้อมปัจจัย</returns>
    [HttpGet("risk/{customerId:long}")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<CustomerRiskDTO>> Risk(long customerId, CancellationToken ct) => Ok(await risk.AssessAsync(CurrentUser, customerId, ct));

    /// <summary>ภาพรวมความเสี่ยงของลูกค้ากลุ่มเสี่ยงสูงสุด</summary>
    /// <param name="top">จำนวนลูกค้า (1-200)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลประเมินและสรุป</returns>
    [HttpGet("risk/portfolio")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public async Task<ActionResult<CustomerRiskPortfolioDTO>> RiskPortfolio([FromQuery] int top = 20, CancellationToken ct = default) =>
        Ok(await risk.AssessPortfolioAsync(CurrentUser, Math.Clamp(top, 1, 200), ct));

    /// <summary>ข้อมูลโมเดลที่ใช้อยู่ (จำนวนตัวอย่าง, log loss, เวลาฝึก)</summary>
    /// <returns>รายการโมเดล</returns>
    [HttpGet("models")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public ActionResult<IReadOnlyList<ModelInfoDTO>> Models() =>
        Ok(predictions.DescribeModels().Select(kv => new ModelInfoDTO(kv.Key, kv.Value.Samples, kv.Value.LogLoss, kv.Value.TrainedAt)).ToList());

    /// <summary>ล้าง cache โมเดล (ฝึกใหม่ในการเรียกครั้งถัดไป) — ใช้หลังนำเข้าข้อมูลชุดใหญ่</summary>
    /// <returns>204</returns>
    [HttpPost("models/retrain")]
    [RequirePermission(Permissions.DataIngest)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Retrain()
    {
        predictions.InvalidateModels();
        recommendations.Invalidate();
        return NoContent();
    }
}

/// <summary>ข้อมูลโมเดล</summary>
/// <param name="Name">ชื่อโมเดล</param>
/// <param name="Samples">จำนวนตัวอย่างที่ใช้ฝึก</param>
/// <param name="LogLoss">log loss บนชุดฝึก</param>
/// <param name="TrainedAt">เวลาฝึก</param>
public sealed record ModelInfoDTO(string Name, int Samples, double LogLoss, DateTimeOffset TrainedAt);

/// <summary>นำเข้าข้อมูลพฤติกรรม/สัญญา/แคตตาล็อก (สำหรับโมเดล ML)</summary>
/// <param name="ingestion">บริการนำเข้า</param>
[Route("api/ingest")]
public sealed class IngestionController(DataIngestionService ingestion) : CustomerMgntControllerBase
{
    /// <summary>นำเข้าเหตุการณ์พฤติกรรม (ซื้อ, เปิดอีเมล, เข้าเว็บ, ...) สูงสุด 10,000 รายการต่อครั้ง</summary>
    /// <param name="request">เหตุการณ์</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>จำนวนที่บันทึก</returns>
    [HttpPost("events")]
    [RequirePermission(Permissions.DataIngest)]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<ActionResult<IngestResultDTO>> Events([FromBody] IngestEventsRequest request, CancellationToken ct) =>
        Ok(new IngestResultDTO(await ingestion.IngestEventsAsync(CurrentUser, request.Events, ct)));

    /// <summary>รายการแคตตาล็อก</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    [HttpGet("catalog")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<IReadOnlyList<CatalogItemDTO>>> Catalog(CancellationToken ct) => Ok(await ingestion.ListCatalogAsync(CurrentUser, ct));

    /// <summary>เพิ่ม/แก้รายการแคตตาล็อก</summary>
    /// <param name="code">รหัสรายการ</param>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการที่บันทึก</returns>
    [HttpPut("catalog/{code}")]
    [RequirePermission(Permissions.DataIngest)]
    public async Task<ActionResult<CatalogItemDTO>> UpsertCatalog(string code, [FromBody] CatalogItemRequest request, CancellationToken ct) =>
        Ok(await ingestion.UpsertCatalogItemAsync(CurrentUser, new CatalogItemDTO
        {
            Code = code,
            Name = request.Name,
            Kind = request.Kind,
            Category = request.Category,
            Price = request.Price,
            IsActive = request.IsActive,
        }, ct));
}

/// <summary>ผลนำเข้า</summary>
/// <param name="Accepted">จำนวนที่บันทึก</param>
public sealed record IngestResultDTO(int Accepted);

/// <summary>โปรไฟล์ผู้ใช้ภายใน (ชื่อที่แสดง, ผูกบัญชี MessageHub เพื่อรับแจ้งเตือน)</summary>
/// <param name="profiles">บริการโปรไฟล์</param>
[Route("api")]
public sealed class ProfileController(UserProfileService profiles) : CustomerMgntControllerBase
{
    /// <summary>โปรไฟล์ของฉัน</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>โปรไฟล์</returns>
    [HttpGet("me/profile")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<UserProfileDTO>> Mine(CancellationToken ct) => Ok(await profiles.GetMineAsync(CurrentUser, ct));

    /// <summary>แก้โปรไฟล์ของฉัน</summary>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>โปรไฟล์</returns>
    [HttpPut("me/profile")]
    [RequirePermission(Permissions.CustomerRead)]
    public async Task<ActionResult<UserProfileDTO>> UpdateMine([FromBody] ProfileRequest request, CancellationToken ct) =>
        Ok(await profiles.UpdateMineAsync(CurrentUser, request.DisplayName, request.MessageHubUserId, ct));

    /// <summary>ตั้งโปรไฟล์ของผู้ใช้อื่น (ผู้ดูแลระบบเท่านั้น — ตรวจใน service)</summary>
    /// <param name="subject">subject ใน AuthServer</param>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>โปรไฟล์</returns>
    [HttpPut("users/{subject}/profile")]
    [RequirePermission(Permissions.CustomerWrite)]
    public async Task<ActionResult<UserProfileDTO>> Upsert(string subject, [FromBody] ProfileRequest request, CancellationToken ct) =>
        Ok(await profiles.UpsertAsync(CurrentUser, new UserProfileDTO { Subject = subject, DisplayName = request.DisplayName, MessageHubUserId = request.MessageHubUserId }, ct));
}

/// <summary>รายการ AI tools (AIFunction) ที่เปิดให้ IChatClient / MCP ใช้</summary>
/// <param name="tools">AIFunction ทั้งหมด</param>
[Route("api/ai")]
public sealed class AiController(CustomerMgntAiTools tools) : CustomerMgntControllerBase
{
    /// <summary>รายชื่อ tools พร้อมคำอธิบายและ JSON schema ของพารามิเตอร์</summary>
    /// <returns>tools</returns>
    [HttpGet("tools")]
    [RequirePermission(Permissions.AnalyticsRead)]
    public ActionResult<IReadOnlyList<AiToolInfoDTO>> Tools() =>
        Ok(tools.Functions.Values.OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new AiToolInfoDTO(f.Name, f.Description, f.JsonSchema)).ToList());
}

/// <summary>ข้อมูล AI tool</summary>
/// <param name="Name">ชื่อ</param>
/// <param name="Description">คำอธิบาย</param>
/// <param name="Parameters">JSON schema ของพารามิเตอร์</param>
public sealed record AiToolInfoDTO(string Name, string Description, System.Text.Json.JsonElement Parameters);
