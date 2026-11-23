using System.ComponentModel;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Analytics;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Services;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.AI;

/// <summary>ข้อผิดพลาดที่คืนให้โมเดลเป็นข้อมูล (โมเดลอธิบาย/แก้ input แล้วลองใหม่ได้) แทนการโยน exception</summary>
/// <param name="Error">รหัส เช่น not_found, validation_failed, forbidden</param>
/// <param name="Message">ข้อความ</param>
/// <param name="Details">รายละเอียด (เช่น field ที่ผิด)</param>
public sealed record ToolError(string Error, string Message, object? Details = null);

/// <summary>
/// AI tools ของ CustomerMgnt — แต่ละ method มี [Description] ที่ AIFunctionFactory ใช้สร้าง JSON schema ให้ LLM
/// ทุก tool เรียก business service ตัวเดียวกับ REST API (single source of truth) ภายใต้สิทธิ์ของผู้ใช้ปัจจุบัน
/// </summary>
public sealed class CustomerMgntAiToolsService
{
    private readonly IAiUserContextAccessor _identity;
    private readonly CustomerService _customers;
    private readonly ActivityService _activities;
    private readonly CustomerRequestService _requests;
    private readonly PipelineService _pipeline;
    private readonly SlaService _sla;
    private readonly InsightService _insights;
    private readonly PredictionService _predictions;
    private readonly ForecastService _forecasts;
    private readonly SegmentationService _segmentation;
    private readonly RecommendationService _recommendations;
    private readonly SendTimeOptimizationService _sendTime;
    private readonly NextBestActionService _nba;
    private readonly CustomerRiskService _risk;
    private readonly SentimentAnalysisService _sentiment;
    private readonly ILogger<CustomerMgntAiToolsService> _logger;

    /// <summary>สร้าง tools</summary>
    /// <param name="identity">ผู้ใช้ปัจจุบัน</param>
    /// <param name="customers">ลูกค้า</param>
    /// <param name="activities">กิจกรรม</param>
    /// <param name="requests">คำขอ/TOR</param>
    /// <param name="pipeline">pipeline</param>
    /// <param name="sla">SLA</param>
    /// <param name="insights">สรุป issue/project</param>
    /// <param name="predictions">ML classification</param>
    /// <param name="forecasts">พยากรณ์</param>
    /// <param name="segmentation">จัดกลุ่ม</param>
    /// <param name="recommendations">แนะนำสินค้า</param>
    /// <param name="sendTime">เวลาส่ง</param>
    /// <param name="nba">next best action</param>
    /// <param name="risk">ความเสี่ยงรวม</param>
    /// <param name="sentiment">วิเคราะห์อารมณ์</param>
    /// <param name="logger">logger</param>
    public CustomerMgntAiToolsService(IAiUserContextAccessor identity, CustomerService customers, ActivityService activities, CustomerRequestService requests, PipelineService pipeline,
        SlaService sla, InsightService insights, PredictionService predictions, ForecastService forecasts, SegmentationService segmentation, RecommendationService recommendations,
        SendTimeOptimizationService sendTime, NextBestActionService nba, CustomerRiskService risk, SentimentAnalysisService sentiment, ILogger<CustomerMgntAiToolsService> logger)
    {
        _identity = identity;
        _customers = customers;
        _activities = activities;
        _requests = requests;
        _pipeline = pipeline;
        _sla = sla;
        _insights = insights;
        _predictions = predictions;
        _forecasts = forecasts;
        _segmentation = segmentation;
        _recommendations = recommendations;
        _sendTime = sendTime;
        _nba = nba;
        _risk = risk;
        _sentiment = sentiment;
        _logger = logger;
    }

    // ------------------------------------------------------------------ risk & prediction

    /// <summary>ความเสี่ยงรวมของลูกค้า</summary>
    /// <param name="customerCode">รหัสลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คะแนน/ระดับ/ปัจจัย</returns>
    [Description("Calculates the overall account risk score (0-100) of one customer by combining churn probability (ML), SLA breach ratio and aging of open issues (IssueTracking), " +
                 "negative chat sentiment, contract expiry, overdue follow-ups and contact gap. Returns the level (Low/Medium/High/Critical), each factor value and the top contributors.")]
    public Task<object> CalculateCustomerRisk([Description("Customer code, e.g. CUST-000123")] string customerCode, CancellationToken ct = default) =>
        RunAsync("calculate_customer_risk", async user => (object)await _risk.AssessAsync(user, (await ResolveAsync(user, customerCode, ct)).Id, ct), ct);

    /// <summary>ภาพรวมความเสี่ยงของพอร์ตลูกค้า</summary>
    /// <param name="top">จำนวน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายลูกค้า + สรุป</returns>
    [Description("Ranks the riskiest customers in the portfolio (candidates from the churn model) with a full risk score each, plus a summary (average, P90, count per level).")]
    public Task<object> AssessPortfolioRisk([Description("How many customers to assess (1-50)")] int top = 20, CancellationToken ct = default) =>
        RunAsync("assess_portfolio_risk", async user => (object)await _risk.AssessPortfolioAsync(user, top, ct), ct);

    /// <summary>ความน่าจะเป็น churn</summary>
    /// <param name="customerCode">ลูกค้า (ว่าง = ทุกราย)</param>
    /// <param name="minProbability">ขั้นต่ำ</param>
    /// <param name="top">จำนวน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    [Description("Predicts churn probability (logistic regression trained on churned vs. active customers) with the signals that drive it and a recommended retention action. " +
                 "Leave customerCode empty to rank all active customers.")]
    public Task<object> PredictChurn(
        [Description("Customer code (optional)")] string? customerCode = null,
        [Description("Only return customers with probability >= this value (0..1)")] double minProbability = 0,
        [Description("Maximum rows (1-500)")] int top = 20,
        CancellationToken ct = default) =>
        RunAsync("predict_churn", async user => (object)await _predictions.PredictChurnAsync(user, await ResolveIdAsync(user, customerCode, ct), Math.Clamp(minProbability, 0, 1), Math.Clamp(top, 1, 500), ct), ct);

    /// <summary>คะแนน lead</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="top">จำนวน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คะแนน</returns>
    [Description("Predictive lead scoring: probability (0-100) that each open opportunity will be won, grade A-D and the factors behind the score.")]
    public Task<object> ScoreLeads([Description("Customer code (optional)")] string? customerCode = null, [Description("Maximum rows (1-500)")] int top = 20, CancellationToken ct = default) =>
        RunAsync("score_leads", async user => (object)await _predictions.ScoreLeadsAsync(user, await ResolveIdAsync(user, customerCode, ct), Math.Clamp(top, 1, 500), ct), ct);

    /// <summary>พยากรณ์ต่อสัญญา MA</summary>
    /// <param name="withinDays">ภายในกี่วัน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป</returns>
    [Description("Forecasts maintenance (MA) contract renewals ending within N days: renewal probability, expected renewal value, upsell/expansion probability and suggested upsell item.")]
    public Task<object> ForecastRenewals([Description("Contracts ending within this many days (1-730)")] int withinDays = 180, CancellationToken ct = default) =>
        RunAsync("forecast_renewals", async user => (object)await _predictions.ForecastRenewalsAsync(user, withinDays, ct), ct);

    /// <summary>พยากรณ์ยอดขาย</summary>
    /// <param name="horizonMonths">เดือน</param>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลพยากรณ์</returns>
    [Description("Sales forecast per month (Holt-Winters on purchase history + weighted open pipeline) with ~80% confidence band and monthly seasonal index.")]
    public Task<object> ForecastSales([Description("Months ahead (1-24)")] int horizonMonths = 6, [Description("Customer code (optional)")] string? customerCode = null, CancellationToken ct = default) =>
        RunAsync("forecast_sales", async user => (object)await _forecasts.ForecastSalesAsync(user, horizonMonths, await ResolveIdAsync(user, customerCode, ct), true, ct), ct);

    /// <summary>พยากรณ์งบตามฤดูกาล</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="horizonMonths">เดือน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลพยากรณ์</returns>
    [Description("Seasonal budget forecast of one customer: expected spend per month and the seasonal index (1.0 = average month) to time proposals with the customer's budget cycle.")]
    public Task<object> ForecastSeasonalBudget([Description("Customer code")] string customerCode, [Description("Months ahead (1-24)")] int horizonMonths = 12, CancellationToken ct = default) =>
        RunAsync("forecast_seasonal_budget", async user => (object)await _forecasts.ForecastSeasonalBudgetAsync(user, (await ResolveAsync(user, customerCode, ct)).Id, horizonMonths, ct), ct);

    /// <summary>next best action</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="max">จำนวน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>action</returns>
    [Description("Recommends the next best actions for a customer (respond to complaint, retention call, renewal, follow-up, discovery meeting, check-in, offer) with reason, priority, channel and best send time.")]
    public Task<object> RecommendNextBestAction([Description("Customer code")] string customerCode, [Description("Maximum actions (1-10)")] int max = 3, CancellationToken ct = default) =>
        RunAsync("recommend_next_best_action", async user => (object)await _nba.RecommendAsync(user, (await ResolveAsync(user, customerCode, ct)).Id, max, ct), ct);

    /// <summary>แนะนำสินค้า/เนื้อหา</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="top">จำนวน</param>
    /// <param name="kind">ประเภท</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    [Description("Product, service or content recommendations for a customer (item-based collaborative filtering on purchases and views, popularity fallback).")]
    public Task<object> RecommendProducts([Description("Customer code")] string customerCode, [Description("Number of items (1-50)")] int top = 5, [Description("Filter: Product, Service, Content or Promotion")] CatalogItemKind? kind = null, CancellationToken ct = default) =>
        RunAsync("recommend_products", async user => (object)await _recommendations.RecommendAsync(user, (await ResolveAsync(user, customerCode, ct)).Id, top, kind, ct), ct);

    /// <summary>เวลาส่งที่ดีที่สุด</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="channel">ช่องทาง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>เวลา</returns>
    [Description("Send-time optimization: best day/hour (customer's local time) to send a message on a channel, the next send time and expected open rate.")]
    public Task<object> GetBestSendTime([Description("Customer code")] string customerCode, [Description("Channel: Email, Line, Sms, ...")] ChannelKind channel = ChannelKind.Email, CancellationToken ct = default) =>
        RunAsync("get_best_send_time", async user => (object)await _sendTime.GetBestTimeAsync(user, (await ResolveAsync(user, customerCode, ct)).Id, channel, ct), ct);

    /// <summary>จัดกลุ่มลูกค้า</summary>
    /// <param name="segmentCount">จำนวนกลุ่ม</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กลุ่ม</returns>
    [Description("Dynamic segmentation (RFM + engagement, k-means++): segment names (Champions, Loyal, At Risk, ...), sizes and centroids. Does not modify customers.")]
    public Task<object> SegmentCustomers([Description("Number of segments (2-12)")] int segmentCount = 5, CancellationToken ct = default) =>
        RunAsync("segment_customers", async user =>
        {
            SegmentationResultDTO r = await _segmentation.SegmentAsync(user, segmentCount, persist: false, ct);

            // ตัดรายชื่อสมาชิกเหลือ 20 รายต่อกลุ่ม (ไม่ส่งข้อมูลมหาศาลเข้า context ของโมเดล)
            return (object)(r with { Segments = r.Segments.Select(s => s with { CustomerIds = s.CustomerIds.Take(20).ToList() }).ToList() });
        }, ct);

    /// <summary>วิเคราะห์อารมณ์</summary>
    /// <param name="text">ข้อความ</param>
    /// <returns>ผล</returns>
    [Description("Sentiment analysis of a Thai or English message: score -1..1, label, confidence, detected signals and whether it needs attention (escalation words).")]
    public object AnalyzeSentiment([Description("Message text (max 8000 characters)")] string text) =>
        string.IsNullOrWhiteSpace(text) ? new ToolError("validation_failed", "Text is required.") : _sentiment.Analyze(text.Length > 8000 ? text[..8000] : text);

    // ------------------------------------------------------------------ customer, SLA, issues, projects

    /// <summary>ค้นหาลูกค้า</summary>
    /// <param name="text">คำค้น</param>
    /// <param name="kind">ประเภท</param>
    /// <param name="status">สถานะ</param>
    /// <param name="segment">กลุ่ม</param>
    /// <param name="page">หน้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลค้นหา</returns>
    [Description("Searches customers (persons or juristic persons) by name, code, email or legal name, with optional filters.")]
    public Task<object> SearchCustomers(
        [Description("Search text")] string? text = null,
        [Description("Person or Juristic")] CustomerKind? kind = null,
        [Description("Prospect, Active, Inactive or Churned")] CustomerStatus? status = null,
        [Description("Segment name")] string? segment = null,
        [Description("Page (1..)")] int page = 1,
        CancellationToken ct = default) =>
        RunAsync("search_customers", async user => (object)await _customers.SearchAsync(user, new CustomerSearchCriteria { Text = text, Kind = kind, Status = status, Segment = segment, Page = Math.Max(1, page), PageSize = 25 }, ct), ct);

    /// <summary>ภาพรวม 360</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ข้อมูล</returns>
    [Description("Customer 360 view: profile, relationships (people/companies and positions), channel identities, recent conversations, open activities, requests, open opportunities and links to projects/issues.")]
    public Task<object> GetCustomer360([Description("Customer code")] string customerCode, CancellationToken ct = default) =>
        RunAsync("get_customer_360", async user => (object)await _customers.Get360Async(user, (await ResolveAsync(user, customerCode, ct)).Id, ct), ct);

    /// <summary>issue aging</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป</returns>
    [Description("Summary of open issues in IssueTracking by aging bucket and customer-specific severity, SLA breached/at-risk counts and the oldest issues. Leave customerCode empty for all customers.")]
    public Task<object> GetIssueAging([Description("Customer code (optional)")] string? customerCode = null, CancellationToken ct = default) =>
        RunAsync("get_issue_aging", async user => (object)await _insights.GetIssueAgingAsync(user, await ResolveIdAsync(user, customerCode, ct), ct), ct);

    /// <summary>SLA compliance</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป</returns>
    [Description("SLA compliance of a customer: issues met/breached/at-risk/on-track and compliance percentage per severity level.")]
    public Task<object> GetSlaCompliance([Description("Customer code")] string customerCode, CancellationToken ct = default) =>
        RunAsync("get_sla_compliance", async user => (object)await _insights.GetSlaComplianceAsync(user, (await ResolveAsync(user, customerCode, ct)).Id, ct), ct);

    /// <summary>นโยบาย SLA</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>นโยบาย</returns>
    [Description("Effective SLA policy of a customer (customer-specific or the default): business hours, holidays and the customer-defined severity levels with response/resolution minutes.")]
    public Task<object> GetSlaPolicy([Description("Customer code")] string customerCode, CancellationToken ct = default) =>
        RunAsync("get_sla_policy", async user => (object?)await _sla.GetEffectivePolicyAsync(user, customerCode, ct) ?? new ToolError("not_found", "No SLA policy applies to this customer."), ct);

    /// <summary>คำนวณ due date ของ SLA</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="severityCode">severity</param>
    /// <param name="startedAt">เวลาเริ่ม</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>เป้าหมาย</returns>
    [Description("Calculates SLA response and resolution due dates for a customer and one of its severity codes, honoring the policy's business hours, working days and holidays.")]
    public Task<object> CalculateSlaTargets(
        [Description("Customer code")] string customerCode,
        [Description("Severity code defined in the customer's SLA, e.g. S1")] string severityCode,
        [Description("Start time (ISO 8601); default now")] DateTimeOffset? startedAt = null,
        CancellationToken ct = default) =>
        RunAsync("calculate_sla_targets", async user => (object)await _sla.CalculateTargetsAsync(user, new SlaCalculateRequest { CustomerCode = customerCode, SeverityCode = severityCode, StartedAt = startedAt }, ct), ct);

    /// <summary>project portfolio</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป</returns>
    [Description("Active and closed projects of a customer from ProjectMgnt with phase, actual vs planned progress, health and forecast end date.")]
    public Task<object> GetProjectPortfolio([Description("Customer code")] string customerCode, CancellationToken ct = default) =>
        RunAsync("get_project_portfolio", async user => (object)await _insights.GetProjectPortfolioAsync(user, (await ResolveAsync(user, customerCode, ct)).Id, ct), ct);

    /// <summary>pipeline</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป</returns>
    [Description("Sales pipeline summary per stage: count, amount, weighted amount, average days in stage and win rate (12 months).")]
    public Task<object> GetPipelineSummary(CancellationToken ct = default) =>
        RunAsync("get_pipeline_summary", async user => (object)await _pipeline.SummaryAsync(user, ct), ct);

    /// <summary>agenda</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>agenda</returns>
    [Description("Customer agenda: upcoming meetings (MessageHub), pending agenda items carried from past meetings, open activities and overdue count.")]
    public Task<object> GetCustomerAgenda([Description("Customer code")] string customerCode, CancellationToken ct = default) =>
        RunAsync("get_customer_agenda", async user => (object)await _activities.GetCustomerAgendaAsync(user, (await ResolveAsync(user, customerCode, ct)).Id, ct), ct);

    /// <summary>คำขอของลูกค้า</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="status">สถานะ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    [Description("Lists customer requests / TOR / change requests / incidents with status.")]
    public Task<object> ListCustomerRequests([Description("Customer code (optional)")] string? customerCode = null, [Description("New, UnderReview, Approved, Forwarded, Rejected, Closed")] RequestStatus? status = null, CancellationToken ct = default) =>
        RunAsync("list_customer_requests", async user => (object)await _requests.ListAsync(user, await ResolveIdAsync(user, customerCode, ct), status, ct), ct);

    // ------------------------------------------------------------------ write tools (ต้องมีสิทธิ์เขียน)

    /// <summary>สร้างงานติดตาม</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="kind">ชนิด</param>
    /// <param name="subject">หัวข้อ</param>
    /// <param name="dueAt">กำหนด</param>
    /// <param name="notes">บันทึก</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กิจกรรม</returns>
    [Description("Creates a follow-up activity (call, email, task, visit, note) for a customer, owned by the current user, with a reminder 30 minutes before. Requires activity.write.")]
    public Task<object> CreateFollowUp(
        [Description("Customer code")] string customerCode,
        [Description("Call, Email, Task, Visit or Note (meetings are created through the API with attendees)")] ActivityKind kind,
        [Description("Subject (max 300 characters)")] string subject,
        [Description("Due time (ISO 8601)")] DateTimeOffset dueAt,
        [Description("Notes (optional)")] string? notes = null,
        CancellationToken ct = default) =>
        RunAsync("create_follow_up", async user =>
        {
            if (kind == ActivityKind.Meeting)
            {
                return new ToolError("validation_failed", "Use the REST API to create meetings with attendees.");
            }

            CustomerDTO c = await ResolveAsync(user, customerCode, ct);
            return (object)await _activities.CreateAsync(user, new CreateActivityRequest { CustomerId = c.Id, Kind = kind, Subject = subject, Notes = notes, DueAt = dueAt, RemindBeforeMinutes = 30 }, ct);
        }, ct);

    /// <summary>บันทึกคำขอ</summary>
    /// <param name="customerCode">ลูกค้า</param>
    /// <param name="kind">ชนิด</param>
    /// <param name="title">หัวข้อ</param>
    /// <param name="description">รายละเอียด</param>
    /// <param name="severityCode">severity</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    [Description("Records a new customer request (Request, Tor, ChangeRequest or Incident). Incidents require a severity code from the customer's SLA. Does not forward it to other systems. Requires request.write.")]
    public Task<object> CreateCustomerRequest(
        [Description("Customer code")] string customerCode,
        [Description("Request, Tor, ChangeRequest or Incident")] RequestKind kind,
        [Description("Title")] string title,
        [Description("Description")] string? description = null,
        [Description("Severity code (required for Incident)")] string? severityCode = null,
        CancellationToken ct = default) =>
        RunAsync("create_customer_request", async user =>
        {
            CustomerDTO c = await ResolveAsync(user, customerCode, ct);
            return (object)await _requests.CreateAsync(user, new CreateCustomerRequestRequest { CustomerId = c.Id, Kind = kind, Title = title, Description = description, SeverityCode = severityCode }, ct);
        }, ct);

    // ------------------------------------------------------------------ helpers

    /// <summary>หาลูกค้าจากรหัส</summary>
    private async Task<CustomerDTO> ResolveAsync(UserContextDTO user, string customerCode, CancellationToken ct)
    {
        ValidationException.ThrowIf(string.IsNullOrWhiteSpace(customerCode), "customerCode", "Customer code is required.");
        return await _customers.GetByCodeAsync(user, customerCode.Trim(), ct).ConfigureAwait(false);
    }

    /// <summary>หาลูกค้าจากรหัส (ว่าง = null)</summary>
    private async Task<long?> ResolveIdAsync(UserContextDTO user, string? customerCode, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(customerCode) ? null : (await ResolveAsync(user, customerCode, ct).ConfigureAwait(false)).Id;

    /// <summary>รัน tool: ระบุผู้ใช้ → เรียก service → แปลงข้อผิดพลาดที่คาดได้เป็น <see cref="ToolError"/></summary>
    private async Task<object> RunAsync(string tool, Func<UserContextDTO, Task<object>> body, CancellationToken ct)
    {
        try
        {
            // Step 1: ผู้ใช้ปัจจุบัน แล้วรัน business logic
            UserContextDTO user = await _identity.GetAsync(ct).ConfigureAwait(false);
            return await body(user).ConfigureAwait(false);
        }
        catch (ValidationException ex)
        {
            // Step 2: ข้อผิดพลาดที่คาดได้ → คืนเป็นข้อมูลให้โมเดลอธิบายหรือแก้ input
            return new ToolError("validation_failed", ex.Message, ex.Errors);
        }
        catch (CommonException ex)
        {
            return new ToolError(ex.ErrorCode, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Step 3: ข้อผิดพลาดที่ไม่คาดคิด → log ฝั่งเซิร์ฟเวอร์ โมเดลได้ข้อความกลาง (ไม่รั่วรายละเอียดภายใน)
            _logger.LogError(ex, "AI tool {Tool} failed", tool);
            return new ToolError("internal_error", "The tool failed unexpectedly. The incident was logged.");
        }
    }
}
