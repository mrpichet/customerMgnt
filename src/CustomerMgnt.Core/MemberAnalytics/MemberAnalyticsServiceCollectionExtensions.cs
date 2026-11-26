using CustomerMgnt.Core.MemberAnalytics.Services;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.MemberAnalytics;

/// <summary>ค่าตั้งของงานเบื้องหลังระบบวิเคราะห์ (section <c>MemberAnalytics</c>)</summary>
public sealed class MemberAnalyticsOptions
{
    public const string SectionName = "MemberAnalytics";

    /// <summary>เปิดงานเบื้องหลัง (คำนวณ read model, แจ้งเตือน, ตั้งเวลา, ส่งออก)</summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>รอก่อนรอบแรกหลังเริ่มระบบ (วินาที)</summary>
    public int InitialDelaySeconds { get; set; } = 30;

    /// <summary>รอบตรวจงาน (วินาที)</summary>
    public int TickSeconds { get; set; } = 60;
}

/// <summary>ลงทะเบียน business layer ของระบบวิเคราะห์ข้อมูลสมาชิกและโปรโมชัน (DAO ลงทะเบียนใน CustomerMgnt.Data)</summary>
public static class MemberAnalyticsServiceCollectionExtensions
{
    /// <summary>ลงทะเบียน services และงานเบื้องหลัง</summary>
    public static IServiceCollection AddMemberAnalyticsCore(this IServiceCollection services, IConfiguration configuration, bool enableBackgroundWorkers = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<MemberAnalyticsOptions>().Bind(configuration.GetSection(MemberAnalyticsOptions.SectionName));
        services.TryAddSingleton<AnaSettingsProvider>();
        services.TryAddSingleton<DataScopeService>();
        services.TryAddSingleton<AnaFilterResolver>();
        services.TryAddSingleton<ReadModelRefreshService>();
        services.TryAddSingleton<AnalyticsQueryService>();
        services.TryAddSingleton<AnaReportService>();
        services.TryAddSingleton<AnalyticsIngestService>();
        services.TryAddSingleton<AnaAlertService>();
        services.TryAddSingleton<AnaAdminService>();
        if (enableBackgroundWorkers)
        {
            services.AddHostedService<MemberAnalyticsWorker>();
        }

        return services;
    }
}

/// <summary>
/// งานเบื้องหลังของระบบวิเคราะห์ (lease lock — รันหลาย instance ได้): คำนวณ read model ที่ถึงรอบ → ประเมินแจ้งเตือน → ส่งรายงานตามเวลา → สร้างไฟล์ส่งออก
/// </summary>
public sealed partial class MemberAnalyticsWorker : BackgroundService
{
    private const string LeaseKey = "member-analytics";
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly MemberAnalyticsOptions _options;
    private readonly MembershipOptions _membership;
    private readonly ILogger<MemberAnalyticsWorker> _log;
    private readonly string _holder = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>สร้าง</summary>
    public MemberAnalyticsWorker(IServiceScopeFactory scopes, TimeProvider clock, IOptions<MemberAnalyticsOptions> options, IOptions<MembershipOptions> membership,
        ILogger<MemberAnalyticsWorker> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(membership);
        _scopes = scopes;
        _clock = clock;
        _options = options.Value;
        _membership = membership.Value;
        _log = log;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled || !_membership.WorkersEnabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds)), _clock, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.TickSeconds)), _clock);
        do
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>รอบงาน 1 รอบ</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopes.CreateScope();
        IServiceProvider sp = scope.ServiceProvider;
        try
        {
            if (!await sp.GetRequiredService<IMembershipJobDAO>().TryAcquireLeaseAsync(LeaseKey, _holder, _clock.GetUtcNow(), TimeSpan.FromMinutes(10), ct).ConfigureAwait(false))
            {
                return;
            }

            int refreshed = await sp.GetRequiredService<ReadModelRefreshService>().RunDueAsync(ct).ConfigureAwait(false);
            if (refreshed > 0)
            {
                await sp.GetRequiredService<AnaAlertService>().EvaluateAllAsync(ct).ConfigureAwait(false);
            }

            await sp.GetRequiredService<AnaReportService>().RunDueSchedulesAsync(ct).ConfigureAwait(false);
            await sp.GetRequiredService<AnaReportService>().RunExportsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTickFailed(_log, ex);
        }
    }

    [LoggerMessage(EventId = 4330, Level = LogLevel.Error, Message = "Member analytics worker tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception ex);
}
