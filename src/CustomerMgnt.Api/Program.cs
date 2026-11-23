// =====================================================================================
//  CustomerMgnt.Api — REST API + MCP server (HTTP, /mcp) + โฮสต์ Blazor WASM UI
//  Business logic ทั้งหมดอยู่ใน CustomerMgnt.Core (Pure C#); ไฟล์นี้แค่ประกอบ host
//  โหมดพิเศษ:
//    --health-probe <url>       ใช้เป็น HEALTHCHECK ใน container (ไม่มี curl ใน image chiseled)
//    --export-catalog <dir>     เขียน API catalog (REST + AI tools) แล้วจบ
// =====================================================================================
using System.Text.Json.Serialization;
using Common.AspNetCore.Authentication;
using Common.AspNetCore.Catalog;
using Common.AspNetCore.Hosting;
using CustomerMgnt.AI;
using CustomerMgnt.Api.Controllers;
using CustomerMgnt.Api.Infrastructure;
using CustomerMgnt.Core;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Data;
using CustomerMgnt.Integrations;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ---- Step 0: health probe สำหรับ container --------------------------------------------
if (await CommonWebHostExtensions.TryRunHealthProbeAsync(args) is int probeExit)
{
    return probeExit;
}

bool exportCatalog = args.Contains("--export-catalog");
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---- Step 1: ค่าเริ่มต้นของ Common (secrets, Kestrel hardening, LogServer, ProblemDetails, rate limit, health) ----
builder.AddCommonWebDefaults(maxRequestBodyBytes: 16 * 1024 * 1024);

// ---- Step 2: ฐานข้อมูล — in-memory อนุญาตเฉพาะ Development / export catalog (กัน deploy ผิดพลาดแล้วข้อมูลหาย) ----
bool allowInMemory = builder.Environment.IsDevelopment() || exportCatalog;
bool noConnectionString = string.IsNullOrWhiteSpace(builder.Configuration["Database:ConnectionString"]);
if (builder.Configuration.GetValue<bool>("Database:InMemory") || noConnectionString)
{
    if (!allowInMemory)
    {
        throw new InvalidOperationException("Database:ConnectionString is required outside Development (Database:InMemory is for development only).");
    }

    builder.Configuration["Database:InMemory"] = "true";
}

// ---- Step 3: Business layer + Data layer + การเชื่อมต่อระบบอื่น -------------------------
builder.Services.AddCustomerMgntCore(builder.Configuration, enableBackgroundWorkers: !exportCatalog);
builder.Services.AddCustomerMgntData(builder.Configuration);
builder.Services.AddCustomerMgntIntegrations(builder.Configuration);

// ---- Step 4: ตัวตนผู้เรียกมาจาก HTTP request (JWT) — ใช้ส่งต่อ token ไประบบอื่น และเป็นผู้ใช้ของ AI tools ----
builder.Services.AddHttpContextAccessor();
builder.Services.Replace(ServiceDescriptor.Singleton<ICallerTokenAccessor, HttpCallerTokenAccessor>());
builder.Services.Replace(ServiceDescriptor.Singleton<IAiUserContextAccessor, HttpAiUserContextAccessor>());
builder.Services.AddOptions<WebhookOptions>().Bind(builder.Configuration.GetSection(WebhookOptions.SectionName));

// ---- Step 5: login proxy ไป AuthServer (UI เรียก /api/session/login) ------------------
builder.Services.AddHttpClient(SessionController.HttpClientName, (sp, c) =>
{
    string authority = builder.Configuration["AuthServer:Authority"] ?? string.Empty;
    if (Uri.TryCreate(authority.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri))
    {
        c.BaseAddress = baseUri;
    }

    c.Timeout = TimeSpan.FromSeconds(15);
});

// ---- Step 6: AuthServer authentication + permission policies (deny by default) ---------
builder.Services.AddAuthServerAuthentication(builder.Configuration);

// ---- Step 7: MVC controllers, JSON (enum เป็น string เหมือนระบบอื่นใน ecosystem), OpenAPI ----
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// ---- Step 8: AI tools (Microsoft.Extensions.AI AIFunction) → MCP over HTTP (/mcp) -------
builder.Services.AddCustomerMgntAiTools();
builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "customermgnt", Version = "1.0.0" })
    .WithHttpTransport(o => o.Stateless = true)
    .WithCustomerMgntTools();

WebApplication app = builder.Build();

// ---- Step 9: export catalog แล้วจบ (CI: dotnet run --project src/CustomerMgnt.Api -- --export-catalog docs/api-catalog) ----
if (exportCatalog)
{
    int idx = Array.IndexOf(args, "--export-catalog");
    string dir = idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal) ? args[idx + 1] : "docs/api-catalog";
    CustomerMgntAiTools tools = app.Services.GetRequiredService<CustomerMgntAiTools>();
    await ApiCatalogExporter.WriteAsync(ApiCatalogExporter.Build(typeof(Program).Assembly, "CustomerMgnt", tools.Functions.Values), dir);
    Console.WriteLine($"API catalog written to {Path.GetFullPath(dir)}");
    return 0;
}

// ---- Step 10: middleware pipeline -----------------------------------------------------
// UiSecurityHeaders ต้องอยู่ก่อน UseCommonWebDefaults: OnStarting callback ทำงานแบบ LIFO → ของเราทำงานหลังสุดและกำหนด CSP ของหน้า UI ได้
bool hasUi = File.Exists(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html"));
if (hasUi)
{
    app.UseMiddleware<UiSecurityHeadersMiddleware>();
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();
}

app.UseCommonWebDefaults();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous(); // /openapi/v1.json
}

app.MapControllers();
app.MapMcp("/mcp").RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.AnalyticsRead);
if (hasUi)
{
    // SPA fallback เฉพาะเส้นทางที่ไม่ใช่ API (path ของ API ที่ไม่มีจริงต้องได้ 404 ไม่ใช่หน้า HTML)
    Microsoft.Extensions.FileProviders.IFileInfo index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    app.MapFallback(async ctx =>
    {
        if (UiSecurityHeadersMiddleware.IsApiPath(ctx.Request.Path) || Path.HasExtension(ctx.Request.Path.Value))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(index, ctx.RequestAborted);
    }).AllowAnonymous();
}

await app.RunAsync();
return 0;

/// <summary>Entry point (ประกาศ partial เพื่อให้ WebApplicationFactory ใช้ใน integration test ได้)</summary>
public partial class Program;
