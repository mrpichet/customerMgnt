using CustomerMgnt.Core.Integration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Integrations;

/// <summary>ลงทะเบียน gateway ไปยังระบบอื่นในตระกูล</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// ลงทะเบียน HttpClient แบบมีชื่อ (IHttpClientFactory → หมุน handler อัตโนมัติ ปลอดภัยใน singleton) พร้อม
    /// standard resilience (timeout, retry เฉพาะ method ที่ปลอดภัย, circuit breaker) และ bearer token handler
    /// </summary>
    /// <param name="services">DI container</param>
    /// <param name="configuration">configuration (section Integrations)</param>
    /// <returns>services เดิม</returns>
    public static IServiceCollection AddCustomerMgntIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Step 1: options + token
        services.AddOptions<IntegrationsOptions>().Bind(configuration.GetSection(IntegrationsOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICallerTokenAccessor, NoCallerTokenAccessor>();
        services.TryAddSingleton<ServiceTokenProvider>();
        services.TryAddTransient<BearerTokenHandler>();

        // Step 2: AuthServer (สำหรับ login บัญชีบริการ — ไม่ใส่ bearer)
        services.AddHttpClient(ServiceTokenProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10)).AddStandardResilienceHandler(DisableUnsafeRetry);

        // Step 3: ระบบปลายทาง
        AddSystem(services, IssueTrackingGateway.HttpClientName, o => o.IssueTracking);
        AddSystem(services, ProjectMgntGateway.HttpClientName, o => o.ProjectMgnt);
        AddSystem(services, RequirementMgntGateway.HttpClientName, o => o.RequirementMgnt);
        AddSystem(services, MessageHubGateway.HttpClientName, o => o.MessageHub);

        // Step 4: gateway (singleton — สร้าง HttpClient จาก factory ทุกครั้งที่เรียก)
        services.TryAddSingleton<IIssueTrackingGateway, IssueTrackingGateway>();
        services.TryAddSingleton<IProjectMgntGateway, ProjectMgntGateway>();
        services.TryAddSingleton<IRequirementMgntGateway, RequirementMgntGateway>();
        services.TryAddSingleton<IMessageHubGateway, MessageHubGateway>();
        return services;
    }

    /// <summary>ลงทะเบียน HttpClient ของระบบหนึ่ง</summary>
    private static void AddSystem(IServiceCollection services, string name, Func<IntegrationsOptions, ExternalSystemOptions> select)
    {
        services.AddHttpClient(name, (sp, client) =>
            {
                // Step 1: base address + header มาตรฐาน (BaseUrl ว่าง → gateway โยน DependencyUnavailable ก่อนส่ง)
                ExternalSystemOptions o = select(sp.GetRequiredService<IOptions<IntegrationsOptions>>().Value);
                if (o.IsConfigured)
                {
                    client.BaseAddress = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
                }

                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                client.DefaultRequestHeaders.UserAgent.ParseAdd("CustomerMgnt/1.0");
                client.Timeout = Timeout.InfiniteTimeSpan; // ให้ resilience pipeline เป็นผู้คุม timeout
            })
            .AddHttpMessageHandler<BearerTokenHandler>()
            .AddStandardResilienceHandler(DisableUnsafeRetry);
    }

    /// <summary>ไม่ retry POST/PUT/PATCH/DELETE (ป้องกันสร้างข้อมูลซ้ำที่ปลายทาง)</summary>
    private static void DisableUnsafeRetry(HttpStandardResilienceOptions options)
    {
        options.Retry.DisableForUnsafeHttpMethods();
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    }
}
