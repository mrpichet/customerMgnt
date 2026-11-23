using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace CustomerMgnt.AI;

/// <summary>
/// สร้าง <see cref="AIFunction"/> จาก <see cref="CustomerMgntAiToolsService"/> ด้วย <see cref="AIFunctionFactory"/> (Microsoft.Extensions.AI)
/// — AIFunction ชุดเดียวกันถูกใช้ทั้งใน IChatClient (ChatOptions.Tools) และ MCP server (McpServerTool.Create)
/// </summary>
public static class CustomerMgntAIFunctions
{
    /// <summary>ข้อกำหนดของ tool หนึ่งตัว</summary>
    /// <param name="Name">ชื่อ tool (snake_case)</param>
    /// <param name="Method">ชื่อ method ใน <see cref="CustomerMgntAiToolsService"/></param>
    /// <param name="ReadOnly">อ่านอย่างเดียว (MCP annotation readOnlyHint)</param>
    public sealed record ToolSpec(string Name, string Method, bool ReadOnly);

    /// <summary>รายการ tool ทั้งหมด</summary>
    public static readonly IReadOnlyList<ToolSpec> Specs =
    [
        // risk & ML
        new("calculate_customer_risk", nameof(CustomerMgntAiToolsService.CalculateCustomerRisk), true),
        new("assess_portfolio_risk", nameof(CustomerMgntAiToolsService.AssessPortfolioRisk), true),
        new("predict_churn", nameof(CustomerMgntAiToolsService.PredictChurn), true),
        new("score_leads", nameof(CustomerMgntAiToolsService.ScoreLeads), true),
        new("forecast_renewals", nameof(CustomerMgntAiToolsService.ForecastRenewals), true),
        new("forecast_sales", nameof(CustomerMgntAiToolsService.ForecastSales), true),
        new("forecast_seasonal_budget", nameof(CustomerMgntAiToolsService.ForecastSeasonalBudget), true),
        new("recommend_next_best_action", nameof(CustomerMgntAiToolsService.RecommendNextBestAction), true),
        new("recommend_products", nameof(CustomerMgntAiToolsService.RecommendProducts), true),
        new("get_best_send_time", nameof(CustomerMgntAiToolsService.GetBestSendTime), true),
        new("segment_customers", nameof(CustomerMgntAiToolsService.SegmentCustomers), true),
        new("analyze_sentiment", nameof(CustomerMgntAiToolsService.AnalyzeSentiment), true),

        // customer / SLA / issues / projects
        new("search_customers", nameof(CustomerMgntAiToolsService.SearchCustomers), true),
        new("get_customer_360", nameof(CustomerMgntAiToolsService.GetCustomer360), true),
        new("get_issue_aging", nameof(CustomerMgntAiToolsService.GetIssueAging), true),
        new("get_sla_compliance", nameof(CustomerMgntAiToolsService.GetSlaCompliance), true),
        new("get_sla_policy", nameof(CustomerMgntAiToolsService.GetSlaPolicy), true),
        new("calculate_sla_targets", nameof(CustomerMgntAiToolsService.CalculateSlaTargets), true),
        new("get_project_portfolio", nameof(CustomerMgntAiToolsService.GetProjectPortfolio), true),
        new("get_pipeline_summary", nameof(CustomerMgntAiToolsService.GetPipelineSummary), true),
        new("get_customer_agenda", nameof(CustomerMgntAiToolsService.GetCustomerAgenda), true),
        new("list_customer_requests", nameof(CustomerMgntAiToolsService.ListCustomerRequests), true),

        // write
        new("create_follow_up", nameof(CustomerMgntAiToolsService.CreateFollowUp), false),
        new("create_customer_request", nameof(CustomerMgntAiToolsService.CreateCustomerRequest), false),
    ];

    /// <summary>JSON ของ tool: camelCase + enum เป็น string (LLM อ่าน/เขียนง่าย)</summary>
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>สร้าง AIFunction ทั้งหมด</summary>
    /// <param name="tools">ตัว tools</param>
    /// <returns>ชื่อ → AIFunction</returns>
    /// <exception cref="InvalidOperationException">spec อ้าง method ที่ไม่มี</exception>
    public static IReadOnlyDictionary<string, AIFunction> Create(CustomerMgntAiToolsService tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var result = new Dictionary<string, AIFunction>(StringComparer.Ordinal);
        foreach (ToolSpec spec in Specs)
        {
            // Step 1: reflect method (คำอธิบายมาจาก [Description])
            MethodInfo method = typeof(CustomerMgntAiToolsService).GetMethod(spec.Method, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Tool method {spec.Method} not found.");

            // Step 2: AIFunctionFactory สร้าง JSON schema + invoker (CancellationToken bind อัตโนมัติ)
            result[spec.Name] = AIFunctionFactory.Create(method, tools, new AIFunctionFactoryOptions { Name = spec.Name, SerializerOptions = JsonOptions });
        }

        return result;
    }

    /// <summary>สร้าง JSON options</summary>
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var o = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}
