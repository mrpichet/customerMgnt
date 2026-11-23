// =====================================================================================
//  CustomerMgnt.Web — Blazor WebAssembly + DevExpress Blazor 26.1
//  เรียก REST API ของ CustomerMgnt.Api บน origin เดียวกัน (token เก็บใน sessionStorage ของแท็บนี้เท่านั้น)
// =====================================================================================
using CustomerMgnt.Web;
using CustomerMgnt.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Step 1: HttpClient ชี้ไป origin เดียวกับหน้าเว็บ (API โฮสต์ UI เอง)
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Step 2: สถานะการเข้าสู่ระบบ + client ของ API
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<ApiClient>();

// Step 3: DevExpress Blazor
builder.Services.AddDevExpressBlazor(o => o.SizeMode = DevExpress.Blazor.SizeMode.Medium);

await builder.Build().RunAsync();
