using Common.Core.Compute;
using Common.Core.Domain;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Analytics;

/// <summary>
/// แบ่งกลุ่มลูกค้าแบบไดนามิก (Dynamic Segmentation) ด้วย RFM + พฤติกรรมการมีส่วนร่วม:
/// <list type="number">
/// <item>สร้างเวกเตอร์ Recency / Frequency / Monetary / Engagement / Complaint / Sentiment ของทุกลูกค้า</item>
/// <item>ทำ z-score แล้วจัดกลุ่มด้วย k-means++ (คำนวณระยะแบบขนาน/SIMD ตาม ComputeOptions)</item>
/// <item>ตั้งชื่อกลุ่มจาก centroid ด้วยกฎที่อธิบายได้ (Champions, At Risk, ...) แล้วบันทึกกลุ่มกลับไปที่ลูกค้า</item>
/// </list>
/// รันซ้ำได้ทุกเมื่อ (เช่น ทุกคืนผ่าน <see cref="AnalyticsRefreshWorker"/>) กลุ่มจึงเปลี่ยนตามพฤติกรรมล่าสุด
/// </summary>
public sealed class SegmentationService
{
    /// <summary>มิติที่ใช้จัดกลุ่ม (ชื่อ feature ใน <see cref="CustomerFeatureBuilder.FeatureNames"/>)</summary>
    internal static readonly string[] Dimensions =
    [
        "years_since_last_purchase", "log_purchases_90d", "log_purchase_value_365d", "email_open_rate",
        "log_web_visits_90d", "log_content_views_90d", "log_complaints_90d", "avg_sentiment_90d",
    ];

    private readonly ICustomerDAO _customers;
    private readonly CustomerFeatureBuilder _features;
    private readonly ComputeOptions _compute;
    private readonly AnalyticsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SegmentationService> _logger;

    /// <summary>สร้างบริการ</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="features">feature builder</param>
    /// <param name="compute">ค่าตั้ง SIMD/Parallel</param>
    /// <param name="options">ค่าตั้ง analytics</param>
    /// <param name="clock">นาฬิกา</param>
    /// <param name="logger">logger</param>
    public SegmentationService(ICustomerDAO customers, CustomerFeatureBuilder features, IOptions<ComputeOptions> compute, IOptions<AnalyticsOptions> options, TimeProvider clock, ILogger<SegmentationService> logger)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentNullException.ThrowIfNull(options);
        _customers = customers;
        _features = features;
        _compute = compute.Value;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>จัดกลุ่มลูกค้าทั้งหมด</summary>
    /// <param name="user">ผู้เรียก (analytics.read; ถ้า persist ต้องมี customer.write ด้วย)</param>
    /// <param name="segmentCount">จำนวนกลุ่ม (2-12, null = ค่าตั้ง)</param>
    /// <param name="persist">บันทึกชื่อกลุ่มกลับลงลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลการจัดกลุ่ม</returns>
    public async Task<SegmentationResultDTO> SegmentAsync(UserContextDTO user, int? segmentCount = null, bool persist = false, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ (บันทึกผล = แก้ข้อมูลลูกค้า)
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        if (persist)
        {
            AccessGuard.Require(user, Permissions.CustomerWrite);
        }

        // Step 2: ทำงานจริง
        return await RunAsync(segmentCount ?? _options.SegmentCount, persist, ct).ConfigureAwait(false);
    }

    /// <summary>จัดกลุ่มโดยไม่ตรวจสิทธิ์ (ใช้โดย background worker ภายในเท่านั้น)</summary>
    /// <param name="segmentCount">จำนวนกลุ่ม</param>
    /// <param name="persist">บันทึกผล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลการจัดกลุ่ม</returns>
    internal async Task<SegmentationResultDTO> RunAsync(int segmentCount, bool persist, CancellationToken ct)
    {
        // Step 1: ลูกค้าที่ยังไม่ churn + feature
        IReadOnlyList<CustomerDTO> customers = (await CustomerScan.LoadAllAsync(_customers, _options.MaxCustomersPerRun, ct).ConfigureAwait(false))
            .Where(c => c.Status != CustomerStatus.Churned).ToList();
        if (customers.Count == 0)
        {
            return new SegmentationResultDTO([], 0, 0, _clock.GetUtcNow());
        }

        IReadOnlyDictionary<long, CustomerFeatures> feats = await _features.BuildAsync(customers, ct).ConfigureAwait(false);
        CustomerFeatures[] ordered = customers.Select(c => feats[c.Id]).ToArray();

        // Step 2: เวกเตอร์ดิบ แล้ว z-score ต่อมิติ (ให้ทุกมิติมีน้ำหนักเท่ากันในระยะทาง)
        float[][] raw = ordered.Select(f => Dimensions.Select(d => f[d]).ToArray()).ToArray();
        (float[][] scaled, float[] mean, float[] std) = Standardize(raw);

        // Step 3: k-means++
        int k = Math.Clamp(segmentCount, 2, 12);
        KMeansResult km = KMeansClusterer.Cluster(scaled, k, _compute, ct: ct);

        // Step 4: แปลง centroid กลับเป็นหน่วยเดิมเพื่อให้อ่านเข้าใจ แล้วตั้งชื่อกลุ่ม
        var segments = new List<SegmentDTO>(km.Centroids.Length);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var assignment = new Dictionary<long, string>(ordered.Length);
        for (int c = 0; c < km.Centroids.Length; c++)
        {
            var centroid = new Dictionary<string, double>(Dimensions.Length);
            for (int d = 0; d < Dimensions.Length; d++)
            {
                centroid[Dimensions[d]] = Math.Round((km.Centroids[c][d] * std[d]) + mean[d], 4);
            }

            string name = Unique(Name(km.Centroids[c]), names);
            List<long> members = Enumerable.Range(0, ordered.Length).Where(i => km.Assignments[i] == c).Select(i => ordered[i].Customer.Id).ToList();
            foreach (long id in members) assignment[id] = name;
            segments.Add(new SegmentDTO(name, members.Count, centroid, members));
        }

        // Step 5: บันทึก (bulk) ถ้าต้องการ
        if (persist)
        {
            await _customers.UpdateSegmentsAsync(assignment, ct).ConfigureAwait(false);
            _logger.LogInformation("Segmented {Customers} customers into {Segments} segments", ordered.Length, segments.Count);
        }

        return new SegmentationResultDTO(segments.OrderByDescending(s => s.Size).ToList(), km.Iterations, Math.Round(km.Inertia, 4), _clock.GetUtcNow());
    }

    /// <summary>z-score ต่อคอลัมน์ (std = 0 → 1 เพื่อไม่หารศูนย์)</summary>
    /// <param name="rows">ข้อมูล</param>
    /// <returns>ข้อมูลที่ปรับแล้ว, ค่าเฉลี่ย, ส่วนเบี่ยงเบน</returns>
    internal static (float[][] Rows, float[] Mean, float[] Std) Standardize(float[][] rows)
    {
        int n = rows.Length, d = rows[0].Length;
        float[] mean = new float[d], std = new float[d];
        foreach (float[] r in rows)
        {
            for (int j = 0; j < d; j++) mean[j] += r[j] / n;
        }

        foreach (float[] r in rows)
        {
            for (int j = 0; j < d; j++) std[j] += (r[j] - mean[j]) * (r[j] - mean[j]) / n;
        }

        for (int j = 0; j < d; j++) std[j] = std[j] > 1e-12f ? MathF.Sqrt(std[j]) : 1f;
        float[][] scaled = rows.Select(r => r.Select((v, j) => (v - mean[j]) / std[j]).ToArray()).ToArray();
        return (scaled, mean, std);
    }

    /// <summary>
    /// ตั้งชื่อกลุ่มจาก centroid ที่เป็น z-score (ค่าบวก = สูงกว่าค่าเฉลี่ยของลูกค้าทั้งหมด)
    /// ลำดับกฎสำคัญ: ร้องเรียนมาก → ซื้อบ่อย/มูลค่าสูง → เคยซื้อแต่ห่างหาย → สนใจแต่ยังไม่ซื้อ → เงียบ
    /// </summary>
    /// <param name="z">centroid (z-score) ตามลำดับ <see cref="Dimensions"/></param>
    /// <returns>ชื่อกลุ่ม</returns>
    internal static string Name(float[] z)
    {
        // Step 1: รวมมิติเป็นคะแนนที่ตีความง่าย (recency สูง = ห่างหายนาน → กลับเครื่องหมาย)
        float recency = -z[0];
        float frequency = z[1];
        float monetary = z[2];
        float engagement = (z[3] + z[4] + z[5]) / 3f;
        float complaints = z[6];
        float sentiment = z[7];

        // Step 2: กฎตั้งชื่อ
        if (complaints > 0.8f || sentiment < -0.8f) return "Needs Care";
        if (monetary > 0.5f && frequency > 0.3f && recency > 0) return "Champions";
        if (monetary > 0.3f && recency < -0.3f) return "At Risk";
        if (frequency > 0 && recency > 0) return "Loyal";
        if (engagement > 0.3f) return "Engaged Prospects";
        if (recency < -0.5f && engagement < 0) return "Hibernating";
        return "Potential";
    }

    /// <summary>ทำให้ชื่อไม่ซ้ำ (เช่น "Loyal 2")</summary>
    private static string Unique(string name, HashSet<string> used)
    {
        string candidate = name;
        for (int i = 2; !used.Add(candidate); i++) candidate = $"{name} {i}";
        return candidate;
    }
}
