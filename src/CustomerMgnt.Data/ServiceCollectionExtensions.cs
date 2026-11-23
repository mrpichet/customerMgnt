using Common.Data;
using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Data.Ado;
using CustomerMgnt.Data.InMemory;
using CustomerMgnt.Data.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CustomerMgnt.Data;

/// <summary>ลงทะเบียน data access ของ CustomerMgnt</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// ลงทะเบียน DAO → ADO
    /// <list type="bullet">
    /// <item><c>Database:InMemory=true</c> → เก็บในหน่วยความจำ (dev/demo, ไม่ต้องมีฐานข้อมูล)</item>
    /// <item>ไม่เช่นนั้น → PostgreSQL ผ่าน Dapper (ต้องตั้ง <c>Database:ConnectionString</c>) และรัน migration ตอน start</item>
    /// </list>
    /// </summary>
    /// <param name="services">DI container</param>
    /// <param name="configuration">configuration</param>
    /// <returns>services เดิม</returns>
    public static IServiceCollection AddCustomerMgntData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Step 1: in-memory
        if (configuration.GetValue<bool>("Database:InMemory"))
        {
            return services.AddCustomerMgntInMemoryData();
        }

        // Step 2: PostgreSQL — factory + type handler + ADO (singleton: stateless, ใช้ connection pool)
        DapperTypeHandlers.Register();
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.PostConfigure<DatabaseOptions>(o =>
        {
            o.Provider = DatabaseProvider.Postgres;
            if (o.ApplicationName == "Common") o.ApplicationName = "CustomerMgnt";
        });
        services.TryAddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.TryAddSingleton<ICustomerDAO, CustomerADO>();
        services.TryAddSingleton<IRelationshipDAO, RelationshipADO>();
        services.TryAddSingleton<IConversationDAO, ConversationADO>();
        services.TryAddSingleton<IActivityDAO, ActivityADO>();
        services.TryAddSingleton<ICustomerRequestDAO, CustomerRequestADO>();
        services.TryAddSingleton<IOpportunityDAO, OpportunityADO>();
        services.TryAddSingleton<ISlaDAO, SlaADO>();
        services.TryAddSingleton<IExternalLinkDAO, ExternalLinkADO>();
        services.TryAddSingleton<IEngagementDAO, EngagementADO>();
        services.TryAddSingleton<IUserProfileDAO, UserProfileADO>();

        // Step 3: migration ก่อน hosted service อื่น (ลงทะเบียนก่อน = start ก่อน)
        services.AddHostedService<CustomerMgntMigrator>();

        // Step 4: DAO ของ Common (audit trail, risk profile) ที่ service/tools กลางต้องใช้ — ตาราง/เวอร์ชัน migration แยกกัน (common_schema_version)
        services.AddCommonData(configuration);
        return services;
    }

    /// <summary>ลงทะเบียน DAO แบบในหน่วยความจำ (ใช้ใน unit/integration test ด้วย)</summary>
    /// <param name="services">DI container</param>
    /// <returns>services เดิม</returns>
    public static IServiceCollection AddCustomerMgntInMemoryData(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryDatabase>();
        services.TryAddSingleton<ICustomerDAO, InMemoryCustomerADO>();
        services.TryAddSingleton<IRelationshipDAO, InMemoryRelationshipADO>();
        services.TryAddSingleton<IConversationDAO, InMemoryConversationADO>();
        services.TryAddSingleton<IActivityDAO, InMemoryActivityADO>();
        services.TryAddSingleton<ICustomerRequestDAO, InMemoryCustomerRequestADO>();
        services.TryAddSingleton<IOpportunityDAO, InMemoryOpportunityADO>();
        services.TryAddSingleton<ISlaDAO, InMemorySlaADO>();
        services.TryAddSingleton<IExternalLinkDAO, InMemoryExternalLinkADO>();
        services.TryAddSingleton<IEngagementDAO, InMemoryEngagementADO>();
        services.TryAddSingleton<IUserProfileDAO, InMemoryUserProfileADO>();
        services.AddCommonInMemoryData();
        return services;
    }
}
