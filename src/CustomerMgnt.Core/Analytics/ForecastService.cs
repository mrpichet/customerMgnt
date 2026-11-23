using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;

namespace CustomerMgnt.Core.Analytics;

/// <summary>
/// พยากรณ์ยอดขายและงบประมาณตามฤดูกาล:
/// <list type="bullet">
/// <item><b>Sales Forecasting</b> — Holt-Winters บนยอดซื้อรายเดือนย้อนหลัง + มูลค่า pipeline ถ่วงน้ำหนักตามเดือนที่คาดว่าจะปิด</item>
/// <item><b>Seasonal Budget Forecasting</b> — พยากรณ์งบ/ยอดซื้อของลูกค้ารายเดียวพร้อมดัชนีฤดูกาลรายเดือน
/// (เช่น หน่วยงานรัฐใช้งบช่วงปลายปีงบประมาณ) เพื่อวางแผนเสนอขายให้ตรงจังหวะ</item>
/// </list>
/// </summary>
public sealed class ForecastService
{
    /// <summary>จำนวนปีย้อนหลังที่ใช้ฝึก</summary>
    private const int HistoryYears = 3;

    /// <summary>z ของช่วงความเชื่อมั่น ~80% (ตรงกับเอกสารของ ForecastPointDTO)</summary>
    private const double Z80 = 1.2816;

    private readonly ICustomerDAO _customers;
    private readonly IOpportunityDAO _opportunities;
    private readonly IEngagementDAO _engagement;
    private readonly TimeProvider _clock;

    /// <summary>สร้างบริการ</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="opportunities">DAO โอกาสการขาย</param>
    /// <param name="engagement">DAO พฤติกรรม (ยอดซื้อรายเดือน)</param>
    /// <param name="clock">นาฬิกา</param>
    public ForecastService(ICustomerDAO customers, IOpportunityDAO opportunities, IEngagementDAO engagement, TimeProvider clock)
    {
        _customers = customers;
        _opportunities = opportunities;
        _engagement = engagement;
        _clock = clock;
    }

    /// <summary>พยากรณ์ยอดขายรวม (หรือของลูกค้ารายเดียว) ล่วงหน้า</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="horizonMonths">จำนวนเดือนล่วงหน้า (1-24)</param>
    /// <param name="customerId">เฉพาะลูกค้า (null = ทั้งหมด)</param>
    /// <param name="includePipeline">บวกมูลค่า pipeline ถ่วงน้ำหนัก</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลพยากรณ์รายเดือน</returns>
    public async Task<ForecastDTO> ForecastSalesAsync(UserContextDTO user, int horizonMonths = 6, long? customerId = null, bool includePipeline = true, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ตรวจช่วง
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        horizonMonths = Math.Clamp(horizonMonths, 1, 24);

        // Step 2: ยอดซื้อรายเดือน + pipeline ที่ยังเปิด โหลดพร้อมกัน
        (DateOnly start, DateOnly current) = HistoryWindow();
        Task<IReadOnlyDictionary<DateOnly, decimal>> historyTask = _engagement.MonthlyPurchasesAsync(customerId, ToUtc(start), ct);
        Task<IReadOnlyList<OpportunityDTO>> pipelineTask = includePipeline ? _opportunities.ListAsync(customerId, true, null, ct) : Task.FromResult<IReadOnlyList<OpportunityDTO>>([]);
        await Task.WhenAll(historyTask, pipelineTask).ConfigureAwait(false);

        // Step 3: มูลค่า pipeline ถ่วงน้ำหนักตามเดือนที่คาดว่าจะปิด (ที่เลยกำหนดแล้วนับเป็นเดือนแรก)
        var pipelineByMonth = new Dictionary<DateOnly, double>();
        foreach (OpportunityDTO o in pipelineTask.Result)
        {
            DateOnly month = MonthOf(o.ExpectedCloseDate);
            if (month < current) month = current;
            pipelineByMonth[month] = pipelineByMonth.GetValueOrDefault(month) + ((double)o.Amount * PipelineService.EffectiveProbability(o) / 100);
        }

        // Step 4: พยากรณ์อนุกรมเวลาแล้วรวมกับ pipeline
        return Build(historyTask.Result, start, current, horizonMonths, pipelineByMonth);
    }

    /// <summary>พยากรณ์งบประมาณ/ยอดซื้อตามฤดูกาลของลูกค้ารายเดียว (12 เดือนข้างหน้าโดยปริยาย)</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="horizonMonths">จำนวนเดือนล่วงหน้า (1-24)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลพยากรณ์พร้อมดัชนีฤดูกาล (เดือน 1-12 → ส่วนต่างจากค่าเฉลี่ย)</returns>
    /// <exception cref="NotFoundException">ไม่พบลูกค้า</exception>
    public async Task<ForecastDTO> ForecastSeasonalBudgetAsync(UserContextDTO user, long customerId, int horizonMonths = 12, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้าต้องมีอยู่จริง
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        _ = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
        horizonMonths = Math.Clamp(horizonMonths, 1, 24);

        // Step 2: ยอดซื้อรายเดือนของลูกค้า (ไม่รวม pipeline เพราะต้องการรูปแบบการใช้งบจริง)
        (DateOnly start, DateOnly current) = HistoryWindow();
        IReadOnlyDictionary<DateOnly, decimal> history = await _engagement.MonthlyPurchasesAsync(customerId, ToUtc(start), ct).ConfigureAwait(false);
        return Build(history, start, current, horizonMonths, new Dictionary<DateOnly, double>());
    }

    /// <summary>สร้างผลพยากรณ์จากยอดรายเดือน (pure function — ทดสอบได้)</summary>
    /// <param name="history">เดือน (วันที่ 1) → ยอด</param>
    /// <param name="start">เดือนแรกของข้อมูล (ต้องเป็นมกราคม เพื่อให้ดัชนีฤดูกาลตรงเดือน)</param>
    /// <param name="current">เดือนปัจจุบัน (ยังไม่จบ → ไม่ใช้ฝึก)</param>
    /// <param name="horizon">จำนวนเดือนล่วงหน้า</param>
    /// <param name="pipelineByMonth">มูลค่า pipeline ถ่วงน้ำหนักต่อเดือน</param>
    /// <returns>ผลพยากรณ์</returns>
    internal static ForecastDTO Build(IReadOnlyDictionary<DateOnly, decimal> history, DateOnly start, DateOnly current, int horizon, IReadOnlyDictionary<DateOnly, double> pipelineByMonth)
    {
        // Step 1: เติมเดือนที่ไม่มียอดเป็น 0 ตั้งแต่ start ถึงเดือนก่อนปัจจุบัน
        var months = new List<DateOnly>();
        var series = new List<double>();
        for (DateOnly m = start; m < current; m = m.AddMonths(1))
        {
            months.Add(m);
            series.Add((double)history.GetValueOrDefault(m));
        }

        // ตัดเดือนศูนย์ช่วงต้นออก (ลูกค้า/ระบบยังไม่เริ่ม) แต่ต้องรักษาจุดเริ่มเป็นมกราคม
        int lead = 0;
        while (lead + 12 <= series.Count && series.Take(lead + 12).All(v => v == 0)) lead += 12;
        months.RemoveRange(0, lead);
        series.RemoveRange(0, lead);

        // Step 2: พยากรณ์
        TimeSeriesResult r = TimeSeriesForecaster.Forecast(series, horizon);

        // Step 3: ประวัติ (ค่าจริง) และจุดพยากรณ์ + ช่วงความเชื่อมั่นที่กว้างขึ้นตาม √h
        var past = new List<ForecastPointDTO>(series.Count);
        for (int i = 0; i < series.Count; i++)
        {
            double actual = Math.Round(series[i], 2);
            past.Add(new ForecastPointDTO(months[i], actual, actual, actual, 0));
        }

        var points = new List<ForecastPointDTO>(horizon);
        for (int h = 0; h < horizon; h++)
        {
            DateOnly m = current.AddMonths(h);
            double pipeline = pipelineByMonth.GetValueOrDefault(m);
            double baseline = r.Forecast[h];
            double band = Z80 * r.ResidualStdDev * Math.Sqrt(h + 1);

            // pipeline ที่จะปิดส่วนหนึ่งซ้อนกับ baseline อยู่แล้ว → ใช้ค่าที่มากกว่าระหว่าง baseline กับ (baseline×0.5 + pipeline)
            double forecast = Math.Max(baseline, (baseline * 0.5) + pipeline);
            points.Add(new ForecastPointDTO(m, Math.Round(forecast, 2), Math.Round(Math.Max(0, forecast - band), 2), Math.Round(forecast + band, 2), Math.Round(pipeline, 2)));
        }

        // Step 4: ดัชนีฤดูกาลตามเดือนปฏิทิน (ข้อมูลเริ่มมกราคม → index 0 = มกราคม)
        // แปลงจากแบบบวกเป็นอัตราส่วน (1.0 = เดือนเฉลี่ย, 1.3 = สูงกว่าเฉลี่ย 30%)
        double mean = series.Count == 0 ? 0 : series.Average();
        var seasonal = new Dictionary<int, double>(12);
        for (int i = 0; i < r.Seasonal.Length && i < 12; i++)
        {
            seasonal[i + 1] = mean <= 0 ? 1 : Math.Round(Math.Max(0, 1 + (r.Seasonal[i] / mean)), 3);
        }

        return new ForecastDTO(r.Method, past, points, seasonal, r.Mape);
    }

    /// <summary>ช่วงข้อมูล: มกราคมของ <see cref="HistoryYears"/> ปีก่อน ถึงเดือนปัจจุบัน</summary>
    private (DateOnly Start, DateOnly Current) HistoryWindow()
    {
        DateOnly today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        return (new DateOnly(today.Year - HistoryYears, 1, 1), MonthOf(today));
    }

    /// <summary>วันที่ 1 ของเดือน</summary>
    private static DateOnly MonthOf(DateOnly d) => new(d.Year, d.Month, 1);

    /// <summary>DateOnly → DateTimeOffset UTC เที่ยงคืน</summary>
    private static DateTimeOffset ToUtc(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
