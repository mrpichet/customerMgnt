using CustomerMgnt.Membership.Web;
using CustomerMgnt.Membership.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WebCommon.UI;
using WebCommon.UI.Auth;
using WebCommon.UI.Navigation;

// =====================================================================
// ระบบสมาชิกและโปรโมชัน (Back Office + Member Portal) — Blazor WebAssembly บน mrpichet/webcommon
// =====================================================================
WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);

// Step 1: root components
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Step 2: service กลางของ webcommon (Theme, Splash, Toast, Dialog, Export, token store)
// ข้อความของ webcommon เป็นภาษาไทยเสมอ (ไม่ขึ้นกับภาษาของเบราว์เซอร์; ไม่เปลี่ยน culture เพื่อไม่ให้วันที่เป็นปฏิทินพุทธในช่องกรอก)
WebCommon.UI.Localization.UiText.Override = WebCommon.UI.Localization.UiTextLanguage.Thai;
builder.Services.AddWebCommonUI(builder.Configuration, builder.HostEnvironment.BaseAddress);

// Step 3: login กับ AuthServer ผ่าน api/v1 (แทน client ของ webcommon ที่ยังเรียก path เดิม)
builder.Services.AddScoped<IAuthClient>(sp => AuthServerV1Client.Create(sp, builder.HostEnvironment.BaseAddress));
builder.Services.AddScoped<ExternalLoginService>();
builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.SectionName));

// Step 4: เมนูของ Back Office
builder.Services.AddScoped<IAppMenuProvider, MembershipMenuProvider>();

// Step 5: REST API ของ CustomerMgnt (same origin) แนบ Bearer token อัตโนมัติ; 401 = ออกจากระบบ
builder.Services.AddScoped(sp =>
{
    BearerTokenHandler handler = sp.GetRequiredService<BearerTokenHandler>();
    handler.InnerHandler = new HttpClientHandler();
    var http = new HttpClient(handler) { BaseAddress = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "/"), Timeout = TimeSpan.FromSeconds(60) };
    return new MembershipApiClient(http);
});

await builder.Build().RunAsync();
