// =====================================================================================
//  ตัวอย่าง: AI เรียกฟังก์ชันของ CustomerMgnt ผ่าน Microsoft.Extensions.AI
//
//  1) เรียก AIFunction ตรง ๆ ด้วย argument แบบ JSON (แบบที่ MCP server / agent framework ทำ)
//  2) ใช้ IChatClient + UseFunctionInvocation(): โมเดลขอเรียก tool → middleware เรียกฟังก์ชันจริง → ส่งผลกลับให้โมเดล
//     ใช้ ScriptedChatClient (โมเดลจำลอง) เพื่อให้รันได้โดยไม่ต้องมี API key;
//     ของจริงเปลี่ยนเป็น IChatClient ของผู้ให้บริการใดก็ได้ (Azure OpenAI, Ollama, Anthropic ฯลฯ) โค้ดส่วนอื่นเหมือนเดิม
//  ข้อมูลอยู่ในหน่วยความจำ (Database:InMemory) และคีย์เข้ารหัสเป็นแบบชั่วคราว — สำหรับตัวอย่างเท่านั้น
// =====================================================================================
using System.Text.Json;
using Common.Core.Domain;
using CustomerMgnt.AI;
using CustomerMgnt.Core;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;
using CustomerMgnt.Data;
using CustomerMgnt.Integrations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// ---- Step 1: ประกอบ DI — business layer ตัวเดียวกับที่ Web API และ MCP server ใช้ ---------------
IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Database:InMemory"] = "true",
    ["Pqc:AllowEphemeralKeys"] = "true",
    ["Privacy:AllowEphemeralKey"] = "true",
}).Build();

ServiceCollection services = new();
services.AddLogging();
services.AddCustomerMgntCore(config, enableBackgroundWorkers: false);
services.AddCustomerMgntData(config);
services.AddCustomerMgntIntegrations(config); // ไม่ตั้ง BaseUrl = ปิดการเชื่อมระบบอื่น (ตัวอย่างไม่ต้องใช้)
// ผู้ใช้ของ AI = ตัวตนที่กำหนดในค่าตั้ง (สิทธิ์อ่านอย่างเดียวเป็นค่าเริ่มต้น + สิทธิ์สร้าง activity สำหรับตัวอย่าง)
services.AddSingleton<IOptions<McpIdentityOptions>>(Options.Create(new McpIdentityOptions
{
    Subject = "ai-sample",
    UserName = "AI sample",
    Permissions = [.. McpIdentityOptions.DefaultPermissions, Permissions.ActivityWrite],
}));
services.Replace(ServiceDescriptor.Singleton<IAiUserContextAccessor, ConfiguredAiUserContextAccessor>());
services.AddCustomerMgntAiTools();
await using ServiceProvider sp = services.BuildServiceProvider();

// ---- Step 2: ข้อมูลตัวอย่าง — ลงทะเบียนลูกค้านิติบุคคล 1 ราย --------------------------------------
UserContextDTO admin = new("seed", "Seeder", [], Permissions.All.ToArray(), "127.0.0.1");
CustomerDTO customer = await sp.GetRequiredService<CustomerService>().RegisterAsync(admin, new RegisterCustomerRequest
{
    Kind = CustomerKind.Juristic,
    LegalName = "บริษัท ตัวอย่าง จำกัด",
    Country = "TH",
    TimeZoneId = "Asia/Bangkok",
    Status = CustomerStatus.Active,
}, CancellationToken.None);
Console.WriteLine($"ลูกค้าตัวอย่าง: {customer.Code} {customer.LegalName}");

// ---- Step 3: tools ที่ AI มองเห็น ---------------------------------------------------------------
CustomerMgntAiTools tools = sp.GetRequiredService<CustomerMgntAiTools>();
Console.WriteLine($"\n== Tools ที่ AI มองเห็น ({tools.Functions.Count}) ==");
foreach (AIFunction t in tools.Functions.Values)
{
    Console.WriteLine($"- {t.Name}: {t.Description[..Math.Min(70, t.Description.Length)]}");
}

// ---- Step 4: เรียก AIFunction ตรง ๆ ด้วย argument JSON ที่ LLM ส่งมา ------------------------------
AIFunction slaTool = tools.Functions["calculate_sla_targets"];
using JsonDocument toolArgs = JsonDocument.Parse($$"""{"customerCode":"{{customer.Code}}","severityCode":"S1","startedAt":"2026-11-23T09:30:00+07:00"}""");
object? direct = await slaTool.InvokeAsync(new AIFunctionArguments(toolArgs.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())));
Console.WriteLine("\n== ผลจากการเรียก calculate_sla_targets ตรง ==");
Console.WriteLine(JsonSerializer.Serialize(direct, CustomerMgntAIFunctions.JsonOptions));

// ---- Step 5: IChatClient + Function invocation middleware ----------------------------------------
IChatClient client = new ChatClientBuilder(new ScriptedChatClient())
    .UseFunctionInvocation()          // เรียก tool อัตโนมัติเมื่อโมเดลขอ
    .Build();

ChatOptions options = new() { Tools = tools.AsTools() };
ChatResponse response = await client.GetResponseAsync("ลูกค้าเขียนมาว่า 'ระบบล่มอีกแล้ว ช้ามาก ไม่พอใจเลย' อารมณ์เป็นอย่างไร?", options);
Console.WriteLine("\n== คำตอบของ AI (หลังเรียก analyze_sentiment) ==");
Console.WriteLine(response.Text);
return 0;

/// <summary>
/// โมเดลจำลอง: รอบแรกขอเรียก tool <c>analyze_sentiment</c> ด้วยข้อความในคำถาม, รอบสองสรุปผลที่ได้จาก tool
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    /// <summary>ตอบกลับหนึ่งรอบ</summary>
    /// <param name="messages">ประวัติการสนทนา</param>
    /// <param name="options">ตัวเลือก (มี Tools)</param>
    /// <param name="cancellationToken">ยกเลิก</param>
    /// <returns>คำขอเรียก tool หรือคำตอบสรุป</returns>
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Step 1: ถ้ามีผลจาก tool แล้ว → สรุปเป็นคำตอบ
        List<ChatMessage> history = messages.ToList();
        FunctionResultContent? result = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        if (result is not null)
        {
            string json = JsonSerializer.Serialize(result.Result, CustomerMgntAIFunctions.JsonOptions);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"ผลวิเคราะห์อารมณ์จาก CustomerMgnt: {json}")));
        }

        // Step 2: ยังไม่มี → ขอเรียก tool (แบบที่ LLM จริงทำเมื่อเห็น tool definition ใน options.Tools)
        string question = history.Last(m => m.Role == ChatRole.User).Text;
        int start = question.IndexOf('\'', StringComparison.Ordinal);
        int end = question.LastIndexOf('\'');
        string quoted = start >= 0 && end > start ? question[(start + 1)..end] : question;
        FunctionCallContent call = new("call-1", "analyze_sentiment", new Dictionary<string, object?> { ["text"] = quoted });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
    }

    /// <summary>แบบ streaming (แปลงจากคำตอบปกติ)</summary>
    /// <param name="messages">ประวัติการสนทนา</param>
    /// <param name="options">ตัวเลือก</param>
    /// <param name="cancellationToken">ยกเลิก</param>
    /// <returns>update ทีละชิ้น</returns>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (ChatResponseUpdate u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
        {
            yield return u;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
