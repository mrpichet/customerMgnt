using CustomerMgnt.Core.Membership.DataAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CustomerMgnt.Data.Membership;

/// <summary>ลงทะเบียน DAO ของระบบสมาชิก</summary>
public static class MembershipDataRegistration
{
    /// <summary>ทุก DAO ของระบบสมาชิกชี้ไปที่ <see cref="MembershipADO"/> ตัวเดียว (PostgreSQL / SQL Server)</summary>
    /// <param name="services">DI container</param>
    /// <returns>services เดิม</returns>
    public static IServiceCollection AddMembershipAdoData(this IServiceCollection services)
    {
        services.TryAddSingleton<MembershipADO>();
        return services.MapAll<MembershipADO>();
    }

    /// <summary>ทุก DAO ของระบบสมาชิกชี้ไปที่ store ในหน่วยความจำตัวเดียว</summary>
    /// <param name="services">DI container</param>
    /// <returns>services เดิม</returns>
    public static IServiceCollection AddMembershipInMemoryData(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryMembershipStore>();
        return services.MapAll<InMemoryMembershipStore>();
    }

    private static IServiceCollection MapAll<T>(this IServiceCollection services) where T : class
    {
        services.TryAddSingleton<IMemberDAO>(sp => (IMemberDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<ITierDAO>(sp => (ITierDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<IPointDAO>(sp => (IPointDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<ICampaignDAO>(sp => (ICampaignDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<ICouponDAO>(sp => (ICouponDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<ISegmentDAO>(sp => (ISegmentDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<ICheckoutDAO>(sp => (ICheckoutDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<IApprovalDAO>(sp => (IApprovalDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<IMembershipAuditDAO>(sp => (IMembershipAuditDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<IOutboxDAO>(sp => (IOutboxDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<IIdempotencyDAO>(sp => (IIdempotencyDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<IMembershipSettingsDAO>(sp => (IMembershipSettingsDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<IMemberPrivacyDAO>(sp => (IMemberPrivacyDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<IMembershipJobDAO>(sp => (IMembershipJobDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<IMembershipUnitOfWork>(sp => (IMembershipUnitOfWork)sp.GetRequiredService<T>());
        return services;
    }
}
