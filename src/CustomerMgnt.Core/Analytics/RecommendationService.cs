using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Analytics;

/// <summary>
/// ระบบแนะนำสินค้า/บริการ/เนื้อหา (Product &amp; Content Recommendation) แบบ item-based collaborative filtering:
/// <list type="number">
/// <item>สร้างเมทริกซ์ลูกค้า × รายการ จากพฤติกรรม 365 วัน (ซื้อ = 3, ดูสินค้า = 1, ดูเนื้อหา = 1)</item>
/// <item>คำนวณ cosine similarity ระหว่างรายการ (co-occurrence) แบบขนาน แล้ว cache ตาม TTL</item>
/// <item>คะแนนของรายการ i = Σ sim(i, j) × น้ำหนักที่ลูกค้าใช้ j โดยไม่แนะนำสินค้าที่ซื้อไปแล้ว</item>
/// <item>ลูกค้าใหม่ไม่มีประวัติ → ใช้ความนิยม (popularity) เป็นค่า fallback</item>
/// </list>
/// </summary>
public sealed class RecommendationService : IDisposable
{
    /// <summary>ประเภทเหตุการณ์ที่ใช้เป็นสัญญาณความสนใจ</summary>
    private static readonly EngagementKind[] SignalKinds = [EngagementKind.Purchase, EngagementKind.ProductView, EngagementKind.ContentView];

    private readonly ICustomerDAO _customers;
    private readonly IEngagementDAO _engagement;
    private readonly CustomerFeatureBuilder _features;
    private readonly AnalyticsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RecommendationService> _logger;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private ItemModel? _model;

    /// <summary>สร้างบริการ</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="engagement">DAO พฤติกรรม/แคตตาล็อก</param>
    /// <param name="features">feature builder (ใช้ดึงประวัติของลูกค้ารายเดียว)</param>
    /// <param name="options">ค่าตั้ง analytics</param>
    /// <param name="clock">นาฬิกา</param>
    /// <param name="logger">logger</param>
    public RecommendationService(ICustomerDAO customers, IEngagementDAO engagement, CustomerFeatureBuilder features, IOptions<AnalyticsOptions> options, TimeProvider clock, ILogger<RecommendationService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _customers = customers;
        _engagement = engagement;
        _features = features;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>แนะนำรายการให้ลูกค้า</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="top">จำนวนรายการ (1-50)</param>
    /// <param name="kind">กรองประเภท (null = ทุกประเภท)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการแนะนำเรียงตามคะแนน</returns>
    /// <exception cref="NotFoundException">ไม่พบลูกค้า</exception>
    public async Task<IReadOnlyList<RecommendationDTO>> RecommendAsync(UserContextDTO user, long customerId, int top = 5, CatalogItemKind? kind = null, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้า
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        CustomerDTO customer = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");

        // Step 2: ประวัติของลูกค้า แล้วให้คะแนน
        IReadOnlyDictionary<long, CustomerFeatures> feats = await _features.BuildAsync([customer], ct).ConfigureAwait(false);
        return await RecommendForEventsAsync(feats[customerId].Events, top, kind, ct).ConfigureAwait(false);
    }

    /// <summary>แนะนำจาก feature ที่คำนวณไว้แล้ว (ใช้ภายใน เช่น เลือก upsell ในการพยากรณ์ต่อสัญญา)</summary>
    /// <param name="features">feature ของลูกค้า (มีประวัติเหตุการณ์)</param>
    /// <param name="top">จำนวนรายการ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการแนะนำ</returns>
    public Task<IReadOnlyList<RecommendationDTO>> RecommendForFeaturesAsync(CustomerFeatures features, int top, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        return RecommendForEventsAsync(features.Events, top, null, ct);
    }

    /// <summary>ล้าง cache (บังคับสร้างเมทริกซ์ใหม่)</summary>
    public void Invalidate() => Volatile.Write(ref _model, null);

    /// <summary>ให้คะแนนจากประวัติเหตุการณ์ของลูกค้า</summary>
    private async Task<IReadOnlyList<RecommendationDTO>> RecommendForEventsAsync(IReadOnlyList<EngagementEventDTO> events, int top, CatalogItemKind? kind, CancellationToken ct)
    {
        // Step 1: โมเดลรายการ (cache)
        top = Math.Clamp(top, 1, 50);
        ItemModel model = await GetModelAsync(ct).ConfigureAwait(false);
        if (model.Items.Length == 0)
        {
            return [];
        }

        // Step 2: น้ำหนักความสนใจของลูกค้าต่อแต่ละรายการ + รายการที่ซื้อแล้ว
        var weights = new Dictionary<int, double>();
        var purchased = new HashSet<int>();
        foreach (EngagementEventDTO e in events)
        {
            if (e.ItemCode is null || !model.Index.TryGetValue(e.ItemCode, out int idx)) continue;
            weights[idx] = weights.GetValueOrDefault(idx) + Weight(e.Kind);
            if (e.Kind == EngagementKind.Purchase) purchased.Add(idx);
        }

        // Step 3: คะแนน = Σ sim × weight (ข้ามสินค้าที่ซื้อแล้ว; เนื้อหาที่ดูแล้วก็ไม่แนะนำซ้ำ)
        int n = model.Items.Length;
        var results = new List<RecommendationDTO>();
        for (int i = 0; i < n; i++)
        {
            CatalogItemDTO item = model.Items[i];
            if (kind is not null && item.Kind != kind) continue;
            if (purchased.Contains(i) && item.Kind != CatalogItemKind.Service) continue;
            if (weights.ContainsKey(i) && item.Kind == CatalogItemKind.Content) continue;

            double score = 0;
            int bestJ = -1;
            double bestContribution = 0;
            foreach ((int j, double w) in weights)
            {
                if (j == i) continue;
                double contribution = model.Similarity[(i * n) + j] * w;
                score += contribution;
                if (contribution > bestContribution)
                {
                    bestContribution = contribution;
                    bestJ = j;
                }
            }

            if (score > 0)
            {
                results.Add(new RecommendationDTO(item.Code, item.Name, item.Kind, Math.Round(score, 4), $"Customers interested in '{model.Items[bestJ].Name}' also chose this"));
            }
        }

        // Step 4: เติมด้วยรายการยอดนิยมเมื่อผลไม่พอ (cold start)
        if (results.Count < top)
        {
            var have = results.Select(r => r.ItemCode).ToHashSet(StringComparer.Ordinal);
            double maxPopularity = Math.Max(1e-9, model.Popularity.Max());
            foreach (int i in model.PopularOrder)
            {
                if (results.Count >= top) break;
                CatalogItemDTO item = model.Items[i];
                if ((kind is not null && item.Kind != kind) || purchased.Contains(i) || have.Contains(item.Code)) continue;
                results.Add(new RecommendationDTO(item.Code, item.Name, item.Kind, Math.Round(0.1 * model.Popularity[i] / maxPopularity, 4), "Popular with similar customers"));
            }
        }

        return results.OrderByDescending(r => r.Score).Take(top).ToList();
    }

    /// <summary>น้ำหนักของสัญญาณ</summary>
    private static double Weight(EngagementKind kind) => kind == EngagementKind.Purchase ? 3 : 1;

    /// <summary>คืนโมเดลจาก cache หรือสร้างใหม่ (double-checked lock)</summary>
    private async Task<ItemModel> GetModelAsync(CancellationToken ct)
    {
        // Step 1: cache hit
        ItemModel? current = Volatile.Read(ref _model);
        DateTimeOffset now = _clock.GetUtcNow();
        if (current is not null && now - current.BuiltAt < TimeSpan.FromMinutes(_options.ModelTtlMinutes))
        {
            return current;
        }

        // Step 2: สร้างใหม่ทีละครั้ง
        await _buildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = Volatile.Read(ref _model);
            if (current is not null && now - current.BuiltAt < TimeSpan.FromMinutes(_options.ModelTtlMinutes))
            {
                return current;
            }

            ItemModel built = await BuildModelAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _model, built);
            _logger.LogInformation("Recommendation model built: {Items} items, {Customers} customers", built.Items.Length, built.Customers);
            return built;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>สร้างเมทริกซ์ similarity ระหว่างรายการ</summary>
    private async Task<ItemModel> BuildModelAsync(CancellationToken ct)
    {
        // Step 1: แคตตาล็อกที่ยังขายอยู่
        CatalogItemDTO[] items = (await _engagement.ListCatalogAsync(ct).ConfigureAwait(false)).Where(i => i.IsActive).ToArray();
        int n = items.Length;
        var index = new Dictionary<string, int>(n, StringComparer.Ordinal);
        for (int i = 0; i < n; i++) index[items[i].Code] = i;
        if (n == 0)
        {
            return new ItemModel(items, index, [], [], [], 0, _clock.GetUtcNow());
        }

        // Step 2: เวกเตอร์ความสนใจของลูกค้าแต่ละราย (sparse) จากพฤติกรรม 365 วัน อ่านเป็นชุด
        IReadOnlyList<CustomerDTO> customers = await CustomerScan.LoadAllAsync(_customers, _options.MaxCustomersPerRun, ct).ConfigureAwait(false);
        DateTimeOffset since = _clock.GetUtcNow().AddDays(-365);
        var baskets = new List<(int Item, double Weight)[]>(customers.Count);
        foreach (CustomerDTO[] chunk in customers.Chunk(1_000))
        {
            IReadOnlyList<EngagementEventDTO> events = await _engagement.ListAsync(chunk.Select(c => c.Id).ToArray(), since, SignalKinds, ct).ConfigureAwait(false);
            foreach (IGrouping<long, EngagementEventDTO> g in events.GroupBy(e => e.CustomerId))
            {
                var basket = new Dictionary<int, double>();
                foreach (EngagementEventDTO e in g)
                {
                    if (e.ItemCode is not null && index.TryGetValue(e.ItemCode, out int idx))
                    {
                        basket[idx] = basket.GetValueOrDefault(idx) + Weight(e.Kind);
                    }
                }

                // ใช้ log(1+w) ลดอิทธิพลของลูกค้าที่ใช้งานซ้ำมาก ๆ
                if (basket.Count > 0) baskets.Add(basket.Select(kv => (kv.Key, Math.Log(1 + kv.Value))).ToArray());
            }
        }

        // Step 3: co-occurrence (dot product) แบบขนาน — แต่ละ thread สะสมในเมทริกซ์ของตัวเองแล้วรวม
        double[] dot = new double[n * n];
        object merge = new();
        Parallel.ForEach(
            System.Collections.Concurrent.Partitioner.Create(0, baskets.Count, Math.Max(256, baskets.Count / Environment.ProcessorCount + 1)),
            new ParallelOptions { CancellationToken = ct },
            () => new double[n * n],
            (range, _, local) =>
            {
                for (int b = range.Item1; b < range.Item2; b++)
                {
                    (int Item, double Weight)[] basket = baskets[b];
                    foreach ((int i, double wi) in basket)
                    {
                        foreach ((int j, double wj) in basket)
                        {
                            local[(i * n) + j] += wi * wj;
                        }
                    }
                }

                return local;
            },
            local =>
            {
                lock (merge)
                {
                    for (int k = 0; k < local.Length; k++) dot[k] += local[k];
                }
            });

        // Step 4: cosine = dot(i,j) / (‖i‖‖j‖) และความนิยม = จำนวนการใช้งานรวม
        double[] sim = new double[n * n];
        double[] popularity = new double[n];
        for (int i = 0; i < n; i++) popularity[i] = dot[(i * n) + i];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double denom = Math.Sqrt(popularity[i] * popularity[j]);
                sim[(i * n) + j] = i == j || denom <= 0 ? 0 : dot[(i * n) + j] / denom;
            }
        }

        int[] popularOrder = Enumerable.Range(0, n).OrderByDescending(i => popularity[i]).ToArray();
        return new ItemModel(items, index, sim, popularity, popularOrder, baskets.Count, _clock.GetUtcNow());
    }

    /// <summary>โมเดลที่ cache ไว้ (immutable หลังสร้าง → อ่านพร้อมกันได้)</summary>
    private sealed record ItemModel(CatalogItemDTO[] Items, Dictionary<string, int> Index, double[] Similarity, double[] Popularity, int[] PopularOrder, int Customers, DateTimeOffset BuiltAt);

    /// <summary>คืนทรัพยากร (semaphore)</summary>
    public void Dispose() => _buildLock.Dispose();
}
