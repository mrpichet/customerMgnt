using Common.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;

namespace CustomerMgnt.AI;

/// <summary>ลงทะเบียน AI tools ของ CustomerMgnt</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// ลงทะเบียน tools + รายการ AIFunction (inject <see cref="CustomerMgntAiTools"/> เพื่อใส่ ChatOptions.Tools ของ IChatClient)
    /// ผู้เรียกต้องลงทะเบียน <see cref="IAiUserContextAccessor"/> เอง (Web API ใช้ผู้ใช้จาก JWT, stdio ใช้ ConfiguredAiUserContextAccessor)
    /// </summary>
    /// <param name="services">DI container</param>
    /// <returns>services เดิม</returns>
    public static IServiceCollection AddCustomerMgntAiTools(this IServiceCollection services)
    {
        services.TryAddSingleton<CustomerMgntAiToolsService>();
        services.TryAddSingleton(sp => new CustomerMgntAiTools(CustomerMgntAIFunctions.Create(sp.GetRequiredService<CustomerMgntAiToolsService>())));
        return services;
    }

    /// <summary>เปิด tools ของ CustomerMgnt (และ tools ของ Common เช่น risk matrix, anomaly, PQC) ผ่าน MCP server</summary>
    /// <param name="builder">MCP server builder</param>
    /// <returns>builder เดิม</returns>
    public static IMcpServerBuilder WithCustomerMgntTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddCustomerMgntAiTools();
        foreach (CustomerMgntAIFunctions.ToolSpec spec in CustomerMgntAIFunctions.Specs)
        {
            // Step 1: McpServerTool จาก AIFunction ตัวเดียวกับที่ IChatClient ใช้ (single source of truth)
            builder.Services.AddSingleton(sp => McpServerTool.Create(sp.GetRequiredService<CustomerMgntAiTools>().Functions[spec.Name], new McpServerToolCreateOptions
            {
                Name = spec.Name,
                ReadOnly = spec.ReadOnly,
                Destructive = false,
                Idempotent = spec.ReadOnly,
                OpenWorld = false,
                Services = sp,
            }));
        }

        // Step 2: tools กลางของ Common
        return builder.WithCommonTools();
    }
}

/// <summary>AIFunction ทั้งหมดของ CustomerMgnt (ใช้กับ <see cref="ChatOptions.Tools"/>)</summary>
/// <param name="Functions">ชื่อ → AIFunction</param>
public sealed record CustomerMgntAiTools(IReadOnlyDictionary<string, AIFunction> Functions)
{
    /// <summary>รายการสำหรับใส่ ChatOptions.Tools</summary>
    public IList<AITool> AsTools() => Functions.Values.Cast<AITool>().ToList();
}
