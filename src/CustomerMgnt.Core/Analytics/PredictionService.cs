using Common.Core.Compute;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Analytics;

/// <summary>
/// โมเดลทำนายแบบ classification:
/// <list type="bullet">
/// <item><b>Predictive Lead Scoring</b> — โอกาสปิดการขายของแต่ละ opportunity (ฝึกจาก Won/Lost ในอดีต)</item>
/// <item><b>Churn Prediction</b> — โอกาสที่ลูกค้าจะเลิกใช้บริการ (ฝึกจากลูกค้า Churned เทียบ Active)</item>
/// <item><b>Renewal &amp; Expansion Forecasting</b> — โอกาสต่อสัญญา MA (ฝึกจากสัญญา Renewed เทียบ Expired/Cancelled) และโอกาส upsell</item>
/// </list>
/// ข้อมูลน้อยเกินไป → ใช้โมเดลตั้งต้น (น้ำหนักจากผู้เชี่ยวชาญ) แล้วสลับเป็นโมเดลที่ฝึกเองเมื่อข้อมูลพอ (cold start)
/// โมเดลถูก cache ตาม <see cref="AnalyticsOptions.ModelTtlMinutes"/> และฝึกใหม่อัตโนมัติ
/// </summary>
public sealed class PredictionService : IDisposable
{
    private static readonly string[] LeadExtra = ["log_amount_100k", "stage_ordinal", "pipeline_age_scaled", "sales_probability"];
    private static readonly string[] RenewalExtra = ["renewal_count", "log_annual_value_100k", "months_to_end"];

    private readonly ICustomerDAO _customers;
    private readonly IOpportunityDAO _opportunities;
    private readonly IEngagementDAO _engagement;
    private readonly CustomerFeatureBuilder _features;
    private readonly RecommendationService _recommendations;
    private readonly ComputeOptions _compute;
    private readonly AnalyticsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PredictionService> _logger;
    private readonly SemaphoreSlim _trainLock = new(1, 1);
    private readonly Dictionary<string, (LogisticRegressionModel Model, DateTimeOffset TrainedAt)> _models = new(StringComparer.Ordinal);

    /// <summary>สร้างบริการ</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="opportunities">DAO โอกาสการขาย</param>
    /// <param name="engagement">DAO พฤติกรรม/สัญญา</param>
    /// <param name="features">feature builder</param>
    /// <param name="recommendations">ระบบแนะนำ (ใช้เลือก upsell)</param>
    /// <param name="compute">ค่าตั้ง SIMD/Parallel</param>
    /// <param name="options">ค่าตั้ง analytics</param>
    /// <param name="clock">นาฬิกา</param>
    /// <param name="logger">logger</param>
    public PredictionService(ICustomerDAO customers, IOpportunityDAO opportunities, IEngagementDAO engagement, CustomerFeatureBuilder features, RecommendationService recommendations,
        IOptions<ComputeOptions> compute, IOptions<AnalyticsOptions> options, TimeProvider clock, ILogger<PredictionService> logger)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentNullException.ThrowIfNull(options);
        _customers = customers;
        _opportunities = opportunities;
        _engagement = engagement;
        _features = features;
        _recommendations = recommendations;
        _compute = compute.Value;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>ให้คะแนน lead/opportunity ที่ยังเปิด เรียงจากโอกาสปิดสูงสุด และบันทึกคะแนนกลับลงฐานข้อมูล</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="customerId">เฉพาะลูกค้า (null = ทั้งหมด)</param>
    /// <param name="top">จำนวนสูงสุดที่คืน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คะแนนพร้อมปัจจัย</returns>
    public async Task<IReadOnlyList<LeadScoreDTO>> ScoreLeadsAsync(UserContextDTO user, long? customerId = null, int top = 100, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + โอกาสที่ยังเปิด
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        IReadOnlyList<OpportunityDTO> open = await _opportunities.ListAsync(customerId, true, null, ct).ConfigureAwait(false);
        if (open.Count == 0)
        {
            return [];
        }

        // Step 2: โมเดล + feature ของลูกค้าที่เกี่ยวข้อง
        LogisticRegressionModel model = await GetLeadModelAsync(ct).ConfigureAwait(false);
        IReadOnlyDictionary<long, CustomerFeatures> feats = await LoadFeaturesAsync(open.Select(o => o.CustomerId).Distinct().ToArray(), ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();

        // Step 3: ทำนายขนาน (โมเดล immutable → thread-safe)
        var scores = new LeadScoreDTO?[open.Count];
        ParallelBatch.For(open.Count, _compute, i =>
        {
            OpportunityDTO o = open[i];
            if (!feats.TryGetValue(o.CustomerId, out CustomerFeatures? f)) return;
            (double p, IReadOnlyList<FactorDTO> factors) = model.Explain(LeadVector(f.Vector, o, now));
            double score = Math.Round(p * 100, 1);
            scores[i] = new LeadScoreDTO(o.Id, o.CustomerId, o.Title, score, score >= 75 ? "A" : score >= 50 ? "B" : score >= 25 ? "C" : "D", factors);
        }, ct);

        // Step 4: บันทึกคะแนน (bulk) แล้วคืนอันดับสูงสุด
        var valid = scores.Where(s => s is not null).Select(s => s!).ToList();
        await _opportunities.UpdateScoresAsync(valid.ToDictionary(s => s.OpportunityId, s => s.Score), ct).ConfigureAwait(false);
        return valid.OrderByDescending(s => s.Score).Take(Math.Clamp(top, 1, 5_000)).ToList();
    }

    /// <summary>ความเสี่ยง churn ของลูกค้า Active (หรือรายเดียว)</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="customerId">ลูกค้า (null = ทุกราย)</param>
    /// <param name="minProbability">คืนเฉพาะที่ความน่าจะเป็น ≥ ค่านี้</param>
    /// <param name="top">จำนวนสูงสุด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้าเรียงจากเสี่ยงมากไปน้อย</returns>
    public async Task<IReadOnlyList<ChurnRiskDTO>> PredictChurnAsync(UserContextDTO user, long? customerId = null, double minProbability = 0, int top = 100, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + กลุ่มเป้าหมาย
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        IReadOnlyList<CustomerDTO> targets = customerId is { } id
            ? [await _customers.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.")]
            : (await LoadAllCustomersAsync(ct).ConfigureAwait(false)).Where(c => c.Status is CustomerStatus.Active or CustomerStatus.Inactive).ToList();
        if (targets.Count == 0)
        {
            return [];
        }

        // Step 2: โมเดล + feature
        LogisticRegressionModel model = await GetChurnModelAsync(ct).ConfigureAwait(false);
        IReadOnlyDictionary<long, CustomerFeatures> feats = await _features.BuildAsync(targets, ct).ConfigureAwait(false);

        // Step 3: ทำนายขนาน
        var results = new ChurnRiskDTO?[targets.Count];
        ParallelBatch.For(targets.Count, _compute, i =>
        {
            CustomerFeatures f = feats[targets[i].Id];
            (double p, IReadOnlyList<FactorDTO> factors) = model.Explain(f.Vector);
            string level = p >= 0.75 ? "Critical" : p >= 0.5 ? "High" : p >= 0.25 ? "Medium" : "Low";
            results[i] = new ChurnRiskDTO(f.Customer.Id, f.Customer.DisplayName, Math.Round(p, 4), level, factors.Where(x => x.Contribution > 0).ToList(), RetentionAction(f, p));
        }, ct);

        return results.Where(r => r is not null && r.Probability >= minProbability).Select(r => r!).OrderByDescending(r => r.Probability).Take(Math.Clamp(top, 1, 5_000)).ToList();
    }

    /// <summary>พยากรณ์การต่อสัญญา MA และโอกาส upsell ของสัญญาที่จะหมดอายุในช่วงเวลา</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="withinDays">หมดอายุภายในกี่วัน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุปและรายสัญญา</returns>
    public async Task<RenewalSummaryDTO> ForecastRenewalsAsync(UserContextDTO user, int withinDays = 180, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + สัญญาที่จะหมดอายุ
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        DateOnly today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        DateOnly to = today.AddDays(Math.Clamp(withinDays, 1, 730));
        IReadOnlyList<ContractDTO> contracts = (await _engagement.ListContractsEndingAsync(today, to, ct).ConfigureAwait(false)).Where(c => c.Status == ContractStatus.Active).ToList();
        if (contracts.Count == 0)
        {
            return new RenewalSummaryDTO(today, to, [], 0, 0);
        }

        // Step 2: โมเดล + feature ของลูกค้า
        LogisticRegressionModel model = await GetRenewalModelAsync(ct).ConfigureAwait(false);
        IReadOnlyDictionary<long, CustomerFeatures> feats = await LoadFeaturesAsync(contracts.Select(c => c.CustomerId).Distinct().ToArray(), ct).ConfigureAwait(false);

        // Step 3: ทำนายต่อสัญญา + expansion (upsell) ต่อสัญญา
        var list = new List<RenewalForecastDTO>(contracts.Count);
        foreach (ContractDTO c in contracts)
        {
            if (!feats.TryGetValue(c.CustomerId, out CustomerFeatures? f)) continue;
            (double p, IReadOnlyList<FactorDTO> factors) = model.Explain(RenewalVector(f.Vector, c, today));

            // expansion: ต่อสัญญาแน่ + การซื้อ/การใช้งานเพิ่มขึ้น + อารมณ์บวก → โอกาส upsell สูง
            double expansion = Math.Clamp((p * 0.5) + (Math.Max(0, f["purchase_trend"]) * 0.25) + (Math.Max(0, f["avg_sentiment_90d"]) * 0.15) + (Math.Min(1, f["log_web_visits_90d"] / 3) * 0.1), 0, 1);
            string? upsell = expansion >= 0.5 ? (await _recommendations.RecommendForFeaturesAsync(f, 1, ct).ConfigureAwait(false)).FirstOrDefault()?.Name : null;
            list.Add(new RenewalForecastDTO(c.Id, c.CustomerId, c.ContractNo, c.EndDate, Math.Round(p, 4), Math.Round(c.AnnualValue * (decimal)p, 2), Math.Round(expansion, 4), upsell, factors));
        }

        return new RenewalSummaryDTO(today, to, list.OrderBy(x => x.RenewalProbability).ToList(), contracts.Sum(c => c.AnnualValue), list.Sum(x => x.ExpectedRenewalValue));
    }

    /// <summary>ล้าง cache โมเดล (บังคับฝึกใหม่ในการเรียกครั้งถัดไป)</summary>
    public void InvalidateModels()
    {
        lock (_models)
        {
            _models.Clear();
        }
    }

    /// <summary>ฝึกโมเดลทั้งหมดใหม่ล่วงหน้า (ใช้โดย background worker เพื่อให้คำขอแรกไม่ต้องรอฝึก)</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Task</returns>
    internal async Task WarmUpAsync(CancellationToken ct)
    {
        // Step 1: ล้าง cache แล้วฝึกทีละโมเดล (GetOrTrainAsync กันการฝึกซ้อนอยู่แล้ว)
        InvalidateModels();
        await GetLeadModelAsync(ct).ConfigureAwait(false);
        await GetChurnModelAsync(ct).ConfigureAwait(false);
        await GetRenewalModelAsync(ct).ConfigureAwait(false);
    }

    /// <summary>ข้อมูลโมเดลที่ใช้อยู่ (จำนวนตัวอย่าง, log loss) — ใช้แสดงความน่าเชื่อถือของผล</summary>
    /// <returns>ชื่อโมเดล → (ตัวอย่าง, log loss, เวลาฝึก)</returns>
    public IReadOnlyDictionary<string, (int Samples, double LogLoss, DateTimeOffset TrainedAt)> DescribeModels()
    {
        lock (_models)
        {
            return _models.ToDictionary(kv => kv.Key, kv => (kv.Value.Model.TrainingSamples, kv.Value.Model.TrainingLogLoss, kv.Value.TrainedAt));
        }
    }

    // ---------------------------------------------------------------- models

    /// <summary>โมเดล lead scoring (ฝึกจาก opportunity ที่ปิดแล้วใน 2 ปี)</summary>
    private Task<LogisticRegressionModel> GetLeadModelAsync(CancellationToken ct) => GetOrTrainAsync("lead", async () =>
    {
        // Step 1: ข้อมูลฝึก
        IReadOnlyList<OpportunityDTO> closed = await _opportunities.ListClosedAsync(_clock.GetUtcNow().AddYears(-2), ct).ConfigureAwait(false);
        IReadOnlyDictionary<long, CustomerFeatures> feats = await LoadFeaturesAsync(closed.Select(o => o.CustomerId).Distinct().ToArray(), ct).ConfigureAwait(false);
        var rows = new List<float[]>();
        var labels = new List<bool>();
        foreach (OpportunityDTO o in closed)
        {
            if (!feats.TryGetValue(o.CustomerId, out CustomerFeatures? f)) continue;
            rows.Add(LeadVector(f.Vector, o, true, o.ClosedAt ?? _clock.GetUtcNow()));
            labels.Add(o.Stage == PipelineStage.Won);
        }

        // Step 2: ฝึก หรือใช้ prior
        string[] names = [.. CustomerFeatureBuilder.FeatureNames, .. LeadExtra];
        return TrainOrPrior(names, rows, labels, LeadPrior(), -3.0f, ct);
    }, ct);

    /// <summary>โมเดล churn (Churned = 1, Active = 0)</summary>
    private Task<LogisticRegressionModel> GetChurnModelAsync(CancellationToken ct) => GetOrTrainAsync("churn", async () =>
    {
        IReadOnlyList<CustomerDTO> all = (await LoadAllCustomersAsync(ct).ConfigureAwait(false)).Where(c => c.Status is CustomerStatus.Active or CustomerStatus.Churned).ToList();
        IReadOnlyDictionary<long, CustomerFeatures> feats = await _features.BuildAsync(all, ct).ConfigureAwait(false);
        var rows = all.Select(c => feats[c.Id].Vector).ToList();
        var labels = all.Select(c => c.Status == CustomerStatus.Churned).ToList();
        return TrainOrPrior(CustomerFeatureBuilder.FeatureNames, rows, labels, ChurnPrior(), -2.0f, ct);
    }, ct);

    /// <summary>โมเดลต่อสัญญา (Renewed = 1, Expired/Cancelled = 0)</summary>
    private Task<LogisticRegressionModel> GetRenewalModelAsync(CancellationToken ct) => GetOrTrainAsync("renewal", async () =>
    {
        IReadOnlyList<ContractDTO> closed = await _engagement.ListClosedContractsAsync(ct).ConfigureAwait(false);
        IReadOnlyDictionary<long, CustomerFeatures> feats = await LoadFeaturesAsync(closed.Select(c => c.CustomerId).Distinct().ToArray(), ct).ConfigureAwait(false);
        var rows = new List<float[]>();
        var labels = new List<bool>();
        foreach (ContractDTO c in closed)
        {
            if (!feats.TryGetValue(c.CustomerId, out CustomerFeatures? f)) continue;
            rows.Add(RenewalVector(f.Vector, c, c.EndDate.AddDays(-60)));
            labels.Add(c.Status == ContractStatus.Renewed);
        }

        string[] names = [.. CustomerFeatureBuilder.FeatureNames, .. RenewalExtra];
        return TrainOrPrior(names, rows, labels, RenewalPrior(), 0.5f, ct);
    }, ct);

    /// <summary>ฝึกเมื่อแต่ละคลาสมีข้อมูลพอ ไม่เช่นนั้นใช้ prior</summary>
    private LogisticRegressionModel TrainOrPrior(string[] names, List<float[]> rows, List<bool> labels, float[] prior, float bias, CancellationToken ct)
    {
        int pos = labels.Count(l => l), neg = labels.Count - pos;
        if (pos < _options.MinSamplesPerClass || neg < _options.MinSamplesPerClass)
        {
            _logger.LogInformation("Using prior model ({Positives} positive / {Negatives} negative samples)", pos, neg);
            return LogisticRegressionModel.FromPrior(names, prior, bias, _compute.UseSimd);
        }

        return LogisticRegressionModel.Train(names, rows, labels, _compute, ct: ct);
    }

    /// <summary>cache โมเดลตาม TTL (ฝึกครั้งละหนึ่งโมเดล ป้องกันฝึกซ้ำพร้อมกัน)</summary>
    private async Task<LogisticRegressionModel> GetOrTrainAsync(string key, Func<Task<LogisticRegressionModel>> train, CancellationToken ct)
    {
        // Step 1: cache hit
        DateTimeOffset now = _clock.GetUtcNow();
        lock (_models)
        {
            if (_models.TryGetValue(key, out var cached) && now - cached.TrainedAt < TimeSpan.FromMinutes(_options.ModelTtlMinutes))
            {
                return cached.Model;
            }
        }

        // Step 2: ฝึกใหม่ (double-checked)
        await _trainLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_models)
            {
                if (_models.TryGetValue(key, out var cached) && now - cached.TrainedAt < TimeSpan.FromMinutes(_options.ModelTtlMinutes))
                {
                    return cached.Model;
                }
            }

            LogisticRegressionModel model = await train().ConfigureAwait(false);
            lock (_models)
            {
                _models[key] = (model, _clock.GetUtcNow());
            }

            _logger.LogInformation("Model {Model} ready ({Samples} samples)", key, model.TrainingSamples);
            return model;
        }
        finally
        {
            _trainLock.Release();
        }
    }

    // ---------------------------------------------------------------- features

    /// <summary>feature ของ opportunity = feature ลูกค้า + มูลค่า, stage, อายุใน pipeline, ความน่าจะเป็นของเซลส์</summary>
    internal static float[] LeadVector(float[] customer, OpportunityDTO o, DateTimeOffset at) => LeadVector(customer, o, false, at);

    private static float[] LeadVector(float[] customer, OpportunityDTO o, bool asOfBeforeClose, DateTimeOffset at)
    {
        // ตอนฝึก opportunity ที่ปิดแล้วอยู่ที่ Won/Lost → ใช้ Negotiation แทนเพื่อไม่ให้ label รั่วเข้า feature
        PipelineStage stage = asOfBeforeClose && o.IsClosed ? PipelineStage.Negotiation : o.Stage;
        int salesProb = asOfBeforeClose && o.IsClosed ? PipelineService.DefaultProbability(stage) : o.Probability ?? PipelineService.DefaultProbability(stage);
        float[] v = new float[customer.Length + LeadExtra.Length];
        customer.CopyTo(v, 0);
        v[customer.Length] = MathF.Log(1 + (float)(o.Amount / 100_000m));
        v[customer.Length + 1] = (int)stage / 5f;
        v[customer.Length + 2] = (float)Math.Min(3, (at - o.CreatedAt).TotalDays / 180);
        v[customer.Length + 3] = salesProb / 100f;
        return v;
    }

    /// <summary>feature ของสัญญา = feature ลูกค้า + จำนวนครั้งที่ต่อ, มูลค่า, เดือนที่เหลือ</summary>
    internal static float[] RenewalVector(float[] customer, ContractDTO c, DateOnly at)
    {
        float[] v = new float[customer.Length + RenewalExtra.Length];
        customer.CopyTo(v, 0);
        v[customer.Length] = Math.Min(10, c.RenewalCount);
        v[customer.Length + 1] = MathF.Log(1 + (float)(c.AnnualValue / 100_000m));
        v[customer.Length + 2] = Math.Clamp((c.EndDate.DayNumber - at.DayNumber) / 30f, -12, 24);
        return v;
    }

    /// <summary>น้ำหนักตั้งต้นของ lead scoring (บน feature ดิบ)</summary>
    private static float[] LeadPrior() =>
    [
        0.10f, 0.10f, 2.0f, 4.0f, 0.4f, 0.3f, 0.2f, -0.3f, 0.3f, -0.4f, -0.1f, 0.8f, -0.8f, 0.4f, 0.3f, 0.05f,
        0.05f, 4.5f, -1.0f, 2.0f,
    ];

    /// <summary>น้ำหนักตั้งต้นของ churn</summary>
    private static float[] ChurnPrior() =>
        [0f, -0.05f, -2.0f, -2.0f, -0.3f, -0.5f, -0.2f, 1.5f, -1.0f, 0.8f, 0.3f, -1.2f, 0.8f, -0.3f, -0.2f, -0.1f];

    /// <summary>น้ำหนักตั้งต้นของการต่อสัญญา</summary>
    private static float[] RenewalPrior() =>
    [
        0.05f, 0.05f, 1.0f, 1.5f, 0.2f, 0.3f, 0.1f, -0.5f, 0.5f, -0.6f, -0.2f, 1.0f, -0.6f, 0.2f, 0.1f, 0.1f,
        0.5f, 0.0f, 0.0f,
    ];

    /// <summary>คำแนะนำการรักษาลูกค้าตามสัญญาณที่เด่นที่สุด</summary>
    private static string RetentionAction(CustomerFeatures f, double p)
    {
        if (p < 0.25) return "ดูแลตามปกติ";
        if (f["log_complaints_90d"] > 0 || f["avg_sentiment_90d"] < -0.2) return "ผู้จัดการโทรหาเพื่อแก้ปัญหาที่ร้องเรียน และเสนอส่วนลด/บริการเสริมชดเชย";
        if (f["years_since_last_purchase"] > 0.5) return "เสนอโปรโมชันเฉพาะลูกค้าเก่าเพื่อกระตุ้นการซื้อซ้ำ";
        if (f["days_since_contact_scaled"] > 0.5) return "นัดเยี่ยมลูกค้า/ประชุมทบทวนการใช้งาน (ขาดการติดต่อนาน)";
        return "ส่ง Customer success check-in และสำรวจความพึงพอใจ";
    }

    /// <summary>โหลดลูกค้าทุกรายแบบ keyset paging (จำกัดตาม MaxCustomersPerRun)</summary>
    private Task<IReadOnlyList<CustomerDTO>> LoadAllCustomersAsync(CancellationToken ct) => CustomerScan.LoadAllAsync(_customers, _options.MaxCustomersPerRun, ct);

    /// <summary>feature ของลูกค้าตามรหัส</summary>
    private async Task<IReadOnlyDictionary<long, CustomerFeatures>> LoadFeaturesAsync(IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<long, CustomerFeatures>();
        var customers = new List<CustomerDTO>(ids.Count);
        foreach (long[] chunk in ids.Chunk(1_000))
        {
            customers.AddRange(await _customers.GetManyAsync(chunk, ct).ConfigureAwait(false));
        }

        return await _features.BuildAsync(customers, ct).ConfigureAwait(false);
    }

    /// <summary>คืนทรัพยากร (semaphore)</summary>
    public void Dispose() => _trainLock.Dispose();
}
