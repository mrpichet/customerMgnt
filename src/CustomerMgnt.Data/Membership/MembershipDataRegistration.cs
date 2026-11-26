using CustomerMgnt.Core.Membership.DataAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CustomerMgnt.Data.Membership;

/// <summary>ลงทะเบียน DAO ของระบบสมาชิก</summary>
public static class MembershipDataRegistration
{
    /// <summary>ทุก DAO ของระบบสมาชิกชี้ไปที่ store ในหน่วยความจำตัวเดียว</summary>
    /// <param name="services">DI container</param>
    /// <returns>services เดิม</returns>
    public static IServiceCollection AddMembershipInMemoryData(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryMembershipStore>();
        services.TryAddSingleton<IMemberDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<ITierDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<IPointDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<ICampaignDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<ICouponDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<ISegmentDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<ICheckoutDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<IApprovalDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<IMembershipAuditDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<IOutboxDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<IIdempotencyDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<IMembershipSettingsDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<IMemberPrivacyDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<IMembershipJobDAO>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        services.TryAddSingleton<IMembershipUnitOfWork>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
        return services;
    }
}
