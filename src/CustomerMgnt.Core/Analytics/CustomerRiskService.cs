using Common.Core.Domain;
using Common.Core.Exceptions;
using Common.Core.Services;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Core.Analytics;

/// <summary>ผลประเมินความเสี่ยงรวมของลูกค้า</summary>
/// <param name="CustomerId">ลูกค้า</param>
/// <param name="CustomerCode">รหัสลูกค้า</param>
/// <param name="Result">คะแนน 0-100, ระดับ และปัจจัยหลัก (จาก Common RiskScoringService)</param>
/// <param name="Factors">ค่าปัจจัยทั้งหมด (0..1) ที่ใช้คำนวณ</param>
/// <param name="Warnings">ข้อมูลที่ขาด (เช่น IssueTracking ไม่ตอบ) — ผลยังคำนวณได้จากปัจจัยที่เหลือ</param>
public sealed record CustomerRiskDTO(long CustomerId, string CustomerCode, RiskResultDTO Result, IReadOnlyList<RiskFactorDTO> Factors, IReadOnlyList<string> Warnings);

/// <summary>ภาพรวมความเสี่ยงของลูกค้าหลายราย</summary>
/// <param name="Customers">รายลูกค้า เรียงจากเสี่ยงมากไปน้อย</param>
/// <param name="Summary">สรุป (ค่าเฉลี่ย, P90, จำนวนต่อระดับ)</param>
public sealed record CustomerRiskPortfolioDTO(IReadOnlyList<CustomerRiskDTO> Customers, RiskSummaryDTO Summary);

/// <summary>
/// คำนวณความเสี่ยงรวมของลูกค้า (Customer Health / Account Risk) จากหลายแหล่ง แล้วให้คะแนนด้วย Common <see cref="RiskScoringService"/>
/// (ถ่วงน้ำหนัก, SIMD/Parallel) — เป็นฟังก์ชันหลักที่ลงทะเบียนเป็น AIFunction/MCP tool
/// <list type="table">
/// <item><term>churn_probability (3.0)</term><description>ความน่าจะเป็น churn จากโมเดล ML</description></item>
/// <item><term>sla_breach_ratio (2.0)</term><description>สัดส่วน issue ที่เปิดอยู่และเกิน SLA (IssueTracking)</description></item>
/// <item><term>issue_aging (1.5)</term><description>อายุเฉลี่ยของ issue ที่เปิดอยู่ / 90 วัน</description></item>
/// <item><term>negative_sentiment (1.5)</term><description>อารมณ์เชิงลบเฉลี่ยในแชท 90 วัน</description></item>
/// <item><term>contract_expiry (1.5)</term><description>สัญญาใกล้หมดใน 90 วัน (ยิ่งใกล้ยิ่งเสี่ยง)</description></item>
/// <item><term>overdue_followups (1.0)</term><description>งานติดตามที่เลยกำหนด / 5</description></item>
/// <item><term>contact_gap (1.0)</term><description>วันนับจากติดต่อล่าสุด / 120</description></item>
/// </list>
/// </summary>
public sealed class CustomerRiskService
{
    /// <summary>น้ำหนักของปัจจัย</summary>
    internal static readonly IReadOnlyDictionary<string, double> Weights = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["churn_probability"] = 3.0,
        ["sla_breach_ratio"] = 2.0,
        ["issue_aging"] = 1.5,
        ["negative_sentiment"] = 1.5,
        ["contract_expiry"] = 1.5,
        ["overdue_followups"] = 1.0,
        ["contact_gap"] = 1.0,
    };

    private readonly ICustomerDAO _customers;
    private readonly IActivityDAO _activities;
    private readonly IConversationDAO _conversations;
    private readonly IEngagementDAO _engagement;
    private readonly PredictionService _predictions;
    private readonly InsightService _insights;
    private readonly RiskScoringService _scoring;
    private readonly TimeProvider _clock;
    private readonly ILogger<CustomerRiskService> _logger;

    /// <summary>สร้างบริการ</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="activities">DAO กิจกรรม</param>
    /// <param name="conversations">DAO แชท</param>
    /// <param name="engagement">DAO สัญญา</param>
    /// <param name="predictions">โมเดลทำนาย</param>
    /// <param name="insights">สรุป issue/SLA จาก IssueTracking</param>
    /// <param name="scoring">ตัวให้คะแนนความเสี่ยงของ Common</param>
    /// <param name="clock">นาฬิกา</param>
    /// <param name="logger">logger</param>
    public CustomerRiskService(ICustomerDAO customers, IActivityDAO activities, IConversationDAO conversations, IEngagementDAO engagement,
        PredictionService predictions, InsightService insights, RiskScoringService scoring, TimeProvider clock, ILogger<CustomerRiskService> logger)
    {
        _customers = customers;
        _activities = activities;
        _conversations = conversations;
        _engagement = engagement;
        _predictions = predictions;
        _insights = insights;
        _scoring = scoring;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>ประเมินความเสี่ยงของลูกค้ารายเดียว</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลประเมิน</returns>
    /// <exception cref="NotFoundException">ไม่พบลูกค้า</exception>
    public async Task<CustomerRiskDTO> AssessAsync(UserContextDTO user, long customerId, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้า
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        CustomerDTO customer = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");

        // Step 2: ปัจจัย → คะแนน
        (List<RiskFactorDTO> factors, List<string> warnings) = await CollectFactorsAsync(user, customer, null, ct).ConfigureAwait(false);
        RiskResultDTO result = _scoring.Score([new RiskSubjectDTO(customer.Code, customer.DisplayName, factors)], null, ct)[0];
        return new CustomerRiskDTO(customer.Id, customer.Code, result, factors, warnings);
    }

    /// <summary>ประเมินลูกค้าที่น่ากังวลที่สุด (คัดจากความเสี่ยง churn) แล้วสรุปภาพรวม</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="top">จำนวนลูกค้า (1-50)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายลูกค้า + สรุป</returns>
    public async Task<CustomerRiskPortfolioDTO> AssessPortfolioAsync(UserContextDTO user, int top = 20, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ผู้สมัคร = ลูกค้าที่ churn สูงสุด
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        top = Math.Clamp(top, 1, 50);
        IReadOnlyList<ChurnRiskDTO> candidates = await _predictions.PredictChurnAsync(user, null, 0, top, ct).ConfigureAwait(false);
        Dictionary<long, double> churn = candidates.ToDictionary(c => c.CustomerId, c => c.Probability);
        IReadOnlyList<CustomerDTO> customers = await _customers.GetManyAsync(churn.Keys.ToArray(), ct).ConfigureAwait(false);

        // Step 2: ปัจจัยของแต่ละรายแบบขนาน (จำกัด 4 เพื่อไม่ถล่ม IssueTracking)
        var rows = new (CustomerDTO Customer, List<RiskFactorDTO> Factors, List<string> Warnings)[customers.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, customers.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (i, token) =>
        {
            (List<RiskFactorDTO> f, List<string> w) = await CollectFactorsAsync(user, customers[i], churn[customers[i].Id], token).ConfigureAwait(false);
            rows[i] = (customers[i], f, w);
        }).ConfigureAwait(false);

        // Step 3: ให้คะแนนเป็นชุด (Common ใช้ SIMD/Parallel) แล้วสรุป
        IReadOnlyList<RiskResultDTO> results = _scoring.Score(rows.Select(r => new RiskSubjectDTO(r.Customer.Code, r.Customer.DisplayName, r.Factors)).ToList(), null, ct);
        var list = rows.Select((r, i) => new CustomerRiskDTO(r.Customer.Id, r.Customer.Code, results[i], r.Factors, r.Warnings)).OrderByDescending(x => x.Result.Score).ToList();
        return new CustomerRiskPortfolioDTO(list, _scoring.Summarize(results, top));
    }

    /// <summary>รวบรวมปัจจัย (แหล่งที่ล่มถูกข้ามพร้อมคำเตือน)</summary>
    private async Task<(List<RiskFactorDTO> Factors, List<string> Warnings)> CollectFactorsAsync(UserContextDTO user, CustomerDTO customer, double? knownChurn, CancellationToken ct)
    {
        var warnings = new List<string>();
        DateTimeOffset now = _clock.GetUtcNow();

        // Step 1: ข้อมูลภายใน (โหลดพร้อมกัน)
        Task<IReadOnlyList<ChurnRiskDTO>> churnTask = knownChurn is null ? _predictions.PredictChurnAsync(user, customer.Id, 0, 1, ct) : Task.FromResult<IReadOnlyList<ChurnRiskDTO>>([]);
        Task<IReadOnlyList<ActivityDTO>> actTask = _activities.ListByCustomerAsync(customer.Id, true, 200, ct);
        Task<IReadOnlyDictionary<long, (DateTimeOffset? LastContactAt, int Done90Days)>> contactTask = _activities.ContactStatsAsync([customer.Id], ct);
        Task<IReadOnlyDictionary<long, (double Average, int Count)>> sentimentTask = _conversations.SentimentByCustomerAsync([customer.Id], now.AddDays(-90), ct);
        Task<IReadOnlyList<ContractDTO>> contractTask = _engagement.ListContractsAsync(customer.Id, ct);
        await Task.WhenAll(churnTask, actTask, contactTask, sentimentTask, contractTask).ConfigureAwait(false);

        // Step 2: ข้อมูลจาก IssueTracking (ล้มเหลวได้)
        IssueAgingSummaryDTO? aging = null;
        try
        {
            aging = await _insights.GetIssueAgingAsync(user, customer.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DependencyUnavailableException or ForbiddenException or ValidationException)
        {
            _logger.LogWarning(ex, "Issue data unavailable for customer {CustomerId}", customer.Id);
            warnings.Add($"Issue/SLA factors skipped: {ex.Message}");
        }

        // Step 3: แปลงเป็นค่า 0..1
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        double churnP = knownChurn ?? churnTask.Result.FirstOrDefault()?.Probability ?? 0;
        int overdue = actTask.Result.Count(a => a.IsOverdue(now));
        contactTask.Result.TryGetValue(customer.Id, out var contact);
        double gapDays = contact.LastContactAt is { } last ? (now - last).TotalDays : 120;
        sentimentTask.Result.TryGetValue(customer.Id, out var sentiment);
        int? daysToExpiry = contractTask.Result.Where(c => c.Status == ContractStatus.Active && c.EndDate >= today).Select(c => (int?)(c.EndDate.DayNumber - today.DayNumber)).Min();

        var factors = new List<RiskFactorDTO>
        {
            Factor("churn_probability", churnP),
            Factor("negative_sentiment", sentiment.Count == 0 ? 0 : Math.Max(0, -sentiment.Average)),
            Factor("contract_expiry", daysToExpiry is { } d && d <= 90 ? 1 - (d / 90.0) : 0),
            Factor("overdue_followups", overdue / 5.0),
            Factor("contact_gap", gapDays / 120.0),
        };
        if (aging is not null)
        {
            factors.Add(Factor("sla_breach_ratio", aging.OpenIssues == 0 ? 0 : (double)aging.SlaBreached / aging.OpenIssues));
            factors.Add(Factor("issue_aging", aging.AverageAgeDays / 90.0));
        }

        return (factors, warnings);
    }

    /// <summary>ปัจจัยที่ตัดค่าให้อยู่ใน 0..1 พร้อมน้ำหนักมาตรฐาน</summary>
    private static RiskFactorDTO Factor(string name, double value) => new(name, Math.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1), 4), Weights[name]);
}
