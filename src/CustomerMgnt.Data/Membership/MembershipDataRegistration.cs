using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Data.MemberAnalytics;
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
        services.TryAddSingleton<AnaADO>();
        return services.MapAll<MembershipADO, AnaADO>();
    }

    /// <summary>ทุก DAO ของระบบสมาชิกชี้ไปที่ store ในหน่วยความจำตัวเดียว</summary>
    /// <param name="services">DI container</param>
    /// <returns>services เดิม</returns>
    public static IServiceCollection AddMembershipInMemoryData(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryMembershipStore>();
        services.TryAddSingleton<InMemoryAnaStore>();
        return services.MapAll<InMemoryMembershipStore, InMemoryAnaStore>();
    }

    private static IServiceCollection MapAll<T, TAna>(this IServiceCollection services) where T : class where TAna : class
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
        // ระบบวิเคราะห์: อ่านต้นทางจาก store เดียวกัน, read model/เอกสาร/ข้อความจาก store ของ ana_*
        services.TryAddSingleton<IAnaSourceDAO>(sp => (IAnaSourceDAO)sp.GetRequiredService<T>());
        services.TryAddSingleton<IAnaReadModelDAO>(sp => (IAnaReadModelDAO)sp.GetRequiredService<TAna>());
        services.TryAddSingleton<IAnaDocumentDAO>(sp => (IAnaDocumentDAO)sp.GetRequiredService<TAna>());
        services.TryAddSingleton<IAnaMessageDAO>(sp => (IAnaMessageDAO)sp.GetRequiredService<TAna>());

        // บันทึกสำเร็จแล้วทำเครื่องหมายวันที่ read model ยอดขายต้องคำนวณใหม่
        services.TryAddSingleton<IMembershipUnitOfWork>(sp => ActivatorUtilities.CreateInstance<AnaDirtyDayUnitOfWork>(sp, (IMembershipUnitOfWork)sp.GetRequiredService<T>()));
        return services;
    }
}
