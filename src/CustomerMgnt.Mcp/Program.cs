// =====================================================================================
//  CustomerMgnt.Mcp — MCP server แบบ stdio (สำหรับ AI client บนเครื่อง เช่น Claude Desktop, VS Code)
//  - tools ชุดเดียวกับ /mcp ของ Web API (AIFunction จาก CustomerMgnt.AI)
//  - ตัวตน/สิทธิ์ของผู้ใช้มาจาก config "Mcp:Identity" (ค่าเริ่มต้น = อ่านอย่างเดียว, ไม่มี admin)
//  - ไม่รัน background worker (ปล่อยให้ Web API เป็นผู้รัน) และ log ไป stderr เท่านั้น
// =====================================================================================
using CustomerMgnt.AI;
using CustomerMgnt.Core;
using CustomerMgnt.Data;
using CustomerMgnt.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// ---- Step 1: logging → stderr (stdout ใช้สำหรับ JSON-RPC ของ MCP เท่านั้น) ------------
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// ---- Step 2: ฐานข้อมูล — in-memory เฉพาะ Development -------------------------------
if (string.IsNullOrWhiteSpace(builder.Configuration["Database:ConnectionString"]) || builder.Configuration.GetValue<bool>("Database:InMemory"))
{
    if (!builder.Environment.IsDevelopment())
    {
        Console.Error.WriteLine("Database:ConnectionString is required (set DOTNET_ENVIRONMENT=Development to use in-memory data).");
        return 2;
    }

    builder.Configuration["Database:InMemory"] = "true";
}

// ---- Step 3: business layer (ไม่มี background worker), data, integrations --------------
builder.Services.AddCustomerMgntCore(builder.Configuration, enableBackgroundWorkers: false);
builder.Services.AddCustomerMgntData(builder.Configuration);
builder.Services.AddCustomerMgntIntegrations(builder.Configuration);

// ---- Step 4: ตัวตนของผู้ใช้ MCP จาก config (ตรวจสิทธิ์ที่ไม่รู้จัก + ไม่ให้ admin) -------
builder.Services.AddOptions<McpIdentityOptions>().Bind(builder.Configuration.GetSection(McpIdentityOptions.SectionName));
builder.Services.Replace(ServiceDescriptor.Singleton<IAiUserContextAccessor, ConfiguredAiUserContextAccessor>());

// ---- Step 5: MCP server over stdio --------------------------------------------------
builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "customermgnt", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithCustomerMgntTools();

await builder.Build().RunAsync();
return 0;
