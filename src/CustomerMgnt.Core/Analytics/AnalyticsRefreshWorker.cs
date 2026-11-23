using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Analytics;

/// <summary>
/// งานเบื้องหลังที่ฝึกโมเดล ML ใหม่ ล้าง cache ระบบแนะนำ และจัดกลุ่มลูกค้าใหม่ตามรอบ <see cref="AnalyticsOptions.RefreshHours"/>
/// ความผิดพลาดในรอบหนึ่งถูกบันทึก log แล้วรอรอบถัดไป (ไม่ทำให้ host ล่ม)
/// </summary>
public sealed class AnalyticsRefreshWorker : BackgroundService
{
    private readonly PredictionService _predictions;
    private readonly RecommendationService _recommendations;
    private readonly SegmentationService _segmentation;
    private readonly AnalyticsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AnalyticsRefreshWorker> _logger;

    /// <summary>สร้าง worker</summary>
    /// <param name="predictions">โมเดลทำนาย</param>
    /// <param name="recommendations">ระบบแนะนำ</param>
    /// <param name="segmentation">การจัดกลุ่ม</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="clock">นาฬิกา</param>
    /// <param name="logger">logger</param>
    public AnalyticsRefreshWorker(PredictionService predictions, RecommendationService recommendations, SegmentationService segmentation, IOptions<AnalyticsOptions> options, TimeProvider clock, ILogger<AnalyticsRefreshWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _predictions = predictions;
        _recommendations = recommendations;
        _segmentation = segmentation;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>วนรอบจนกว่า host จะหยุด</summary>
    /// <param name="stoppingToken">สัญญาณหยุด</param>
    /// <returns>Task</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Step 1: ปิดได้ด้วย RefreshHours = 0
        if (_options.RefreshHours <= 0)
        {
            return;
        }

        try
        {
            // Step 2: รอให้ระบบ start เสร็จ แล้วทำรอบแรก
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.RefreshInitialDelaySeconds)), _clock, stoppingToken).ConfigureAwait(false);
            await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);

            // Step 3: ทำซ้ำตามรอบ
            using var timer = new PeriodicTimer(TimeSpan.FromHours(_options.RefreshHours), _clock);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host กำลังหยุด — ออกตามปกติ
        }
    }

    /// <summary>หนึ่งรอบ: ฝึกโมเดล → ล้าง cache ระบบแนะนำ → จัดกลุ่ม</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Task</returns>
    internal async Task RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            await _predictions.WarmUpAsync(ct).ConfigureAwait(false);
            _recommendations.Invalidate();
            await _segmentation.RunAsync(_options.SegmentCount, _options.PersistSegments, ct).ConfigureAwait(false);
            _logger.LogInformation("Analytics refresh completed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Analytics refresh failed; will retry next cycle");
        }
    }
}
