using CustomerMgnt.Core.Membership.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CustomerMgnt.Core.Membership;

/// <summary>ลงทะเบียน business layer ของระบบสมาชิกและโปรโมชัน (DAO ลงทะเบียนใน CustomerMgnt.Data)</summary>
public static class MembershipServiceCollectionExtensions
{
    /// <summary>ลงทะเบียน services, approval handlers และงานเบื้องหลังของระบบสมาชิก</summary>
    /// <param name="services">DI container</param>
    /// <param name="configuration">configuration (section <c>Membership</c>)</param>
    /// <param name="enableBackgroundWorkers">เปิดงานเบื้องหลัง</param>
    /// <returns>services เดิม</returns>
    public static IServiceCollection AddMembershipCore(this IServiceCollection services, IConfiguration configuration, bool enableBackgroundWorkers = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<MembershipOptions>().Bind(configuration.GetSection(MembershipOptions.SectionName));

        // Step 1: services (stateless → singleton; ข้อมูลทั้งหมดอยู่ใน DAO)
        services.TryAddSingleton<MembershipRulesProvider>();
        services.TryAddSingleton<PointLedgerService>();
        services.TryAddSingleton<OtpService>();
        services.TryAddSingleton<ApprovalService>();
        services.TryAddSingleton<MemberService>();
        services.TryAddSingleton<TierService>();
        services.TryAddSingleton<PointService>();
        services.TryAddSingleton<CampaignService>();
        services.TryAddSingleton<CouponService>();
        services.TryAddSingleton<SegmentService>();
        services.TryAddSingleton<CheckoutService>();
        services.TryAddSingleton<PrivacyRequestService>();
        services.TryAddSingleton<PortalService>();
        services.TryAddSingleton<ReportService>();
        services.TryAddSingleton<SettingsService>();
        services.TryAddSingleton<CampaignMessageService>();
        services.TryAddSingleton<OutboxDispatcher>();
        services.TryAddSingleton<MembershipMaintenanceService>();
        services.TryAddSingleton<IMembershipEventSink, LoggingMembershipEventSink>();

        // Step 2: approval handlers (ApprovalService หา handler ตอนใช้งาน กัน DI วน)
        services.AddSingleton<IApprovalHandler>(sp => sp.GetRequiredService<MemberService>());
        services.AddSingleton<IApprovalHandler>(sp => sp.GetRequiredService<CampaignService>());
        services.AddSingleton<IApprovalHandler, AdjustmentApprovalHandler>();
        services.AddSingleton<IApprovalHandler, EarnRuleApprovalHandler>();

        // Step 3: งานเบื้องหลัง (lease lock — รันหลาย instance ได้)
        if (enableBackgroundWorkers)
        {
            services.AddHostedService<MembershipWorker>();
        }

        return services;
    }
}
