using Common.Core;
using CustomerMgnt.Core.Analytics;
using CustomerMgnt.Core.Membership;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CustomerMgnt.Core;

/// <summary>ลงทะเบียน business layer ของ CustomerMgnt (ใช้ร่วมกันทั้ง REST API และ MCP server)</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// ลงทะเบียน options, services, analytics และ background workers
    /// ต้องลงทะเบียน DAO (CustomerMgnt.Data) และ gateway (CustomerMgnt.Integrations) แยกต่างหาก
    /// </summary>
    /// <param name="services">DI container</param>
    /// <param name="configuration">configuration</param>
    /// <param name="enableBackgroundWorkers">เปิดงานเบื้องหลัง (reminder, notification, analytics refresh) — ปิดได้ใน MCP stdio/เทสต์</param>
    /// <returns>services เดิม (chain ได้)</returns>
    public static IServiceCollection AddCustomerMgntCore(this IServiceCollection services, IConfiguration configuration, bool enableBackgroundWorkers = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Step 1: Common.Core (TimeProvider, PQC, ComputeOptions)
        services.AddCommonCore(configuration);
        services.TryAddSingleton(TimeProvider.System);

        // Step 2: options พร้อมตรวจสอบตอน start
        services.AddOptions<CustomerMgntOptions>().Bind(configuration.GetSection(CustomerMgntOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AnalyticsOptions>().Bind(configuration.GetSection(AnalyticsOptions.SectionName)).ValidateOnStart();
        services.AddOptions<PrivacyOptions>().Bind(configuration.GetSection(PrivacyOptions.SectionName)).ValidateOnStart();

        // Step 3: services (stateless หรือ thread-safe → singleton; gateway ใช้ IHttpClientFactory ภายในจึงปลอดภัย)
        services.TryAddSingleton<PrivacyService>();
        services.TryAddSingleton<NotificationQueue>();
        services.TryAddSingleton<SentimentAnalysisService>();
        services.TryAddSingleton<SlaService>();
        services.TryAddSingleton<CustomerService>();
        services.TryAddSingleton<RelationshipService>();
        services.TryAddSingleton<OmnichannelService>();
        services.TryAddSingleton<ActivityService>();
        services.TryAddSingleton<CustomerRequestService>();
        services.TryAddSingleton<PipelineService>();
        services.TryAddSingleton<InsightService>();
        services.TryAddSingleton<DataIngestionService>();
        services.TryAddSingleton<UserProfileService>();

        // Step 4: analytics/ML (มี cache ของโมเดล → ต้องเป็น singleton)
        services.TryAddSingleton<CustomerFeatureBuilder>();
        services.TryAddSingleton<RecommendationService>();
        services.TryAddSingleton<PredictionService>();
        services.TryAddSingleton<ForecastService>();
        services.TryAddSingleton<SegmentationService>();
        services.TryAddSingleton<SendTimeOptimizationService>();
        services.TryAddSingleton<NextBestActionService>();
        services.TryAddSingleton<CustomerRiskService>();

        // Step 5: ระบบสมาชิกและโปรโมชัน
        services.AddMembershipCore(configuration, enableBackgroundWorkers);

        // Step 6: background workers
        if (enableBackgroundWorkers)
        {
            services.AddHostedService<NotificationDispatcher>();
            services.AddHostedService<ActivityReminderWorker>();
            services.AddHostedService<AnalyticsRefreshWorker>();
        }

        return services;
    }
}
