using Common.Core.Compute;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Analytics;

/// <summary>
/// Feature engineering ของลูกค้า — แปลงข้อมูลดิบ (พฤติกรรม, การติดต่อ, อารมณ์, ขนาดธุรกิจ) เป็นเวกเตอร์ตัวเลขชุดเดียว
/// ที่โมเดลทุกตัวใช้ร่วมกัน (lead scoring, churn, renewal, segmentation)
/// โหลดข้อมูลเป็นชุด (≤ 1,000 ลูกค้าต่อ query) แล้วคำนวณขนานต่อลูกค้า
/// </summary>
public sealed class CustomerFeatureBuilder
{
    /// <summary>ชื่อ feature ตามลำดับในเวกเตอร์</summary>
    public static readonly string[] FeatureNames =
    [
        "log_employees", "log_revenue_m", "email_open_rate", "email_click_rate", "log_web_visits_90d", "log_purchases_90d",
        "log_purchase_value_365d", "years_since_last_purchase", "purchase_trend", "log_complaints_90d", "log_tickets_90d",
        "avg_sentiment_90d", "days_since_contact_scaled", "log_activities_90d", "log_content_views_90d", "tenure_years",
    ];

    private const int ChunkSize = 1_000;
    private readonly IEngagementDAO _engagement;
    private readonly IActivityDAO _activities;
    private readonly IConversationDAO _conversations;
    private readonly ComputeOptions _compute;
    private readonly TimeProvider _clock;

    /// <summary>สร้างบริการ</summary>
    /// <param name="engagement">DAO พฤติกรรม</param>
    /// <param name="activities">DAO กิจกรรม</param>
    /// <param name="conversations">DAO แชต</param>
    /// <param name="compute">ค่าตั้ง SIMD/Parallel</param>
    /// <param name="clock">นาฬิกา</param>
    public CustomerFeatureBuilder(IEngagementDAO engagement, IActivityDAO activities, IConversationDAO conversations, IOptions<ComputeOptions> compute, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(compute);
        _engagement = engagement;
        _activities = activities;
        _conversations = conversations;
        _compute = compute.Value;
        _clock = clock;
    }

    /// <summary>สร้าง feature ของลูกค้าหลายราย</summary>
    /// <param name="customers">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รหัสลูกค้า → (feature, เหตุการณ์ 365 วันล่าสุด)</returns>
    public async Task<IReadOnlyDictionary<long, CustomerFeatures>> BuildAsync(IReadOnlyList<CustomerDTO> customers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(customers);
        var result = new Dictionary<long, CustomerFeatures>(customers.Count);
        DateTimeOffset now = _clock.GetUtcNow();

        foreach (CustomerDTO[] chunk in customers.Chunk(ChunkSize))
        {
            // Step 1: โหลดข้อมูลของชุดนี้พร้อมกัน 3 แหล่ง
            long[] ids = chunk.Select(c => c.Id).ToArray();
            Task<IReadOnlyList<EngagementEventDTO>> eventsTask = _engagement.ListAsync(ids, now.AddDays(-365), null, ct);
            Task<IReadOnlyDictionary<long, (DateTimeOffset? LastContactAt, int Done90Days)>> contactTask = _activities.ContactStatsAsync(ids, ct);
            Task<IReadOnlyDictionary<long, (double Average, int Count)>> sentimentTask = _conversations.SentimentByCustomerAsync(ids, now.AddDays(-90), ct);
            await Task.WhenAll(eventsTask, contactTask, sentimentTask).ConfigureAwait(false);

            // Step 2: จัดกลุ่มเหตุการณ์ตามลูกค้า
            ILookup<long, EngagementEventDTO> byCustomer = eventsTask.Result.ToLookup(e => e.CustomerId);
            var features = new CustomerFeatures[chunk.Length];

            // Step 3: คำนวณ feature ขนานต่อลูกค้า (แต่ละ index เขียนช่องของตัวเอง → ไม่ต้อง lock)
            ParallelBatch.For(chunk.Length, _compute, i =>
            {
                CustomerDTO c = chunk[i];
                EngagementEventDTO[] ev = byCustomer[c.Id].ToArray();
                contactTask.Result.TryGetValue(c.Id, out var contact);
                sentimentTask.Result.TryGetValue(c.Id, out var sentiment);
                features[i] = new CustomerFeatures(c, Compute(c, ev, contact.LastContactAt, contact.Done90Days, sentiment.Count == 0 ? 0 : sentiment.Average, now), ev);
            }, ct);

            foreach (CustomerFeatures f in features)
            {
                result[f.Customer.Id] = f;
            }
        }

        return result;
    }

    /// <summary>คำนวณเวกเตอร์ feature ของลูกค้า 1 ราย (Pure function)</summary>
    /// <param name="c">ลูกค้า</param>
    /// <param name="events">เหตุการณ์ 365 วันล่าสุด</param>
    /// <param name="lastContactAt">ติดต่อล่าสุด</param>
    /// <param name="activities90">จำนวนกิจกรรมที่ทำใน 90 วัน</param>
    /// <param name="avgSentiment">อารมณ์เฉลี่ย 90 วัน</param>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <returns>เวกเตอร์ (ลำดับตาม <see cref="FeatureNames"/>)</returns>
    public static float[] Compute(CustomerDTO c, IReadOnlyList<EngagementEventDTO> events, DateTimeOffset? lastContactAt, int activities90, double avgSentiment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(events);

        // Step 1: นับเหตุการณ์ตามช่วงเวลา
        DateTimeOffset d90 = now.AddDays(-90), d180 = now.AddDays(-180);
        int sent = 0, opens = 0, clicks = 0, visits = 0, purch90 = 0, purchPrev90 = 0, complaints = 0, tickets = 0, content = 0;
        decimal value365 = 0;
        DateTimeOffset? lastPurchase = null;
        foreach (EngagementEventDTO e in events)
        {
            bool in90 = e.OccurredAt >= d90;
            switch (e.Kind)
            {
                case EngagementKind.EmailSent: sent++; break;
                case EngagementKind.EmailOpen: opens++; break;
                case EngagementKind.EmailClick: clicks++; break;
                case EngagementKind.WebVisit when in90: visits++; break;
                case EngagementKind.Purchase:
                    value365 += e.Value;
                    if (in90) purch90++;
                    else if (e.OccurredAt >= d180) purchPrev90++;
                    if (lastPurchase is null || e.OccurredAt > lastPurchase) lastPurchase = e.OccurredAt;
                    break;
                case EngagementKind.Complaint when in90: complaints++; break;
                case EngagementKind.SupportTicket when in90: tickets++; break;
                case EngagementKind.ContentView or EngagementKind.ProductView when in90: content++; break;
            }
        }

        // Step 2: อัตราเปิด/คลิกแบบ smoothing (prior 20% / 3%) — ลูกค้าที่เพิ่งได้อีเมลฉบับแรกไม่ได้ค่า 0 หรือ 100%
        float openRate = (opens + (0.2f * 5)) / (Math.Max(sent, opens) + 5f);
        float clickRate = (clicks + (0.03f * 5)) / (Math.Max(sent, opens) + 5f);

        // Step 3: แนวโน้มการซื้อ (90 วันล่าสุดเทียบ 90 วันก่อนหน้า) −1..1
        float trend = purch90 + purchPrev90 == 0 ? 0 : Math.Clamp((purch90 - purchPrev90) / (float)Math.Max(1, purchPrev90), -1f, 1f);
        float yearsSincePurchase = lastPurchase is null ? 2f : (float)Math.Min(2, (now - lastPurchase.Value).TotalDays / 365);
        float daysSinceContact = lastContactAt is null ? 2f : (float)Math.Min(2, (now - lastContactAt.Value).TotalDays / 180);

        return
        [
            MathF.Log(1 + (c.EmployeeCount ?? 0)),
            MathF.Log(1 + (float)((c.AnnualRevenue ?? 0) / 1_000_000m)),
            openRate,
            clickRate,
            MathF.Log(1 + visits),
            MathF.Log(1 + purch90),
            MathF.Log(1 + (float)(value365 / 1_000m)),
            yearsSincePurchase,
            trend,
            MathF.Log(1 + complaints),
            MathF.Log(1 + tickets),
            (float)avgSentiment,
            daysSinceContact,
            MathF.Log(1 + activities90),
            MathF.Log(1 + content),
            (float)Math.Min(10, (now - c.CreatedAt).TotalDays / 365),
        ];
    }
}

/// <summary>feature ของลูกค้า 1 ราย</summary>
/// <param name="Customer">ลูกค้า</param>
/// <param name="Vector">เวกเตอร์ feature</param>
/// <param name="Events">เหตุการณ์ 365 วันล่าสุด (ใช้ต่อใน recommendation / send-time)</param>
public sealed record CustomerFeatures(CustomerDTO Customer, float[] Vector, IReadOnlyList<EngagementEventDTO> Events)
{
    /// <summary>อ่านค่า feature ตามชื่อ</summary>
    /// <param name="name">ชื่อ feature</param>
    /// <returns>ค่า</returns>
    public float this[string name] => Vector[Array.IndexOf(CustomerFeatureBuilder.FeatureNames, name)];
}
