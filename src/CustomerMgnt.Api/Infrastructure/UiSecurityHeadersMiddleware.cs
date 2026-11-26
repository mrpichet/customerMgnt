namespace CustomerMgnt.Api.Infrastructure;

/// <summary>
/// CSP สำหรับหน้า Blazor WebAssembly — Common ตั้ง <c>default-src 'none'</c> (เหมาะกับ API) ซึ่ง UI ใช้ไม่ได้
/// middleware นี้ต้องลงทะเบียน <b>ก่อน</b> UseCommonWebDefaults: OnStarting ทำงานย้อนลำดับ จึงรันหลังสุดและแทนค่าเฉพาะ path ของ UI
/// </summary>
/// <param name="next">middleware ถัดไป</param>
/// <param name="configuration">ค่าตั้ง (Membership:Ui:ConnectSrc = origin เพิ่มเติมที่หน้าระบบสมาชิกเรียกได้ เช่น AuthServer ที่อยู่คนละ origin)</param>
public sealed class UiSecurityHeadersMiddleware(RequestDelegate next, IConfiguration configuration)
{
    /// <summary>CSP ที่เข้มที่สุดที่ Blazor WASM + DevExpress ทำงานได้ (ไม่มี script inline, ต่อเฉพาะ origin เดียวกัน)</summary>
    internal const string UiCsp =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    /// <summary>
    /// CSP ของหน้าระบบสมาชิก (/membership): เหมือน UI หลัก + อนุญาต Google Identity Services และ Facebook SDK สำหรับ login ของ Member Portal
    /// (script/frame/connect เฉพาะ origin ของ provider; ไม่มี inline script)
    /// </summary>
    private const string MembershipCspHead =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval' https://accounts.google.com/gsi/client https://connect.facebook.net; " +
        "style-src 'self' 'unsafe-inline' https://accounts.google.com/gsi/style; img-src 'self' data: https://*.googleusercontent.com https://*.fbcdn.net; font-src 'self' data:; " +
        "connect-src 'self' https://accounts.google.com/gsi/ https://graph.facebook.com https://www.facebook.com";

    private const string MembershipCspTail =
        "; frame-src https://accounts.google.com/gsi/ https://www.facebook.com https://web.facebook.com; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    private readonly string _membershipCsp = MembershipCspHead + ExtraConnect(configuration) + MembershipCspTail;

    private static string ExtraConnect(IConfiguration configuration)
    {
        // รับเฉพาะ origin https://host[:port] ที่ไม่มีอักขระพิเศษ กัน header injection จากค่าตั้งที่ผิด
        string[] origins = (configuration?["Membership:Ui:ConnectSrc"] ?? string.Empty)
            .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(o => Uri.TryCreate(o, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps && u.AbsolutePath == "/" && string.IsNullOrEmpty(u.Query) && o.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ':' or '/' or '.' or '-'))
            .ToArray();
        return origins.Length == 0 ? string.Empty : " " + string.Join(' ', origins);
    }

    /// <summary>path ของหน้าระบบสมาชิก</summary>
    /// <param name="path">path</param>
    /// <returns>true = /membership</returns>
    internal static bool IsMembershipPath(PathString path) => path.StartsWithSegments("/membership");

    /// <summary>path ที่เป็น API/ระบบ (คง CSP ของ Common)</summary>
    /// <param name="path">path</param>
    /// <returns>true = API</returns>
    internal static bool IsApiPath(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp") || path.StartsWithSegments("/health") || path.StartsWithSegments("/openapi");

    /// <summary>ประมวลผลคำขอ</summary>
    /// <param name="context">HttpContext</param>
    /// <returns>Task</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Step 1: เฉพาะหน้า UI
        if (!IsApiPath(context.Request.Path))
        {
            string csp = IsMembershipPath(context.Request.Path) ? _membershipCsp : UiCsp;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.ContentSecurityPolicy = csp;

                // _framework มีชื่อไฟล์แบบ fingerprint → cache ได้; index.html ต้องตรวจใหม่ทุกครั้ง
                bool framework = context.Request.Path.StartsWithSegments("/_framework") || context.Request.Path.StartsWithSegments("/membership/_framework");
                context.Response.Headers.CacheControl = framework ? "public, max-age=31536000, immutable" : "no-cache";
                return Task.CompletedTask;
            });
        }

        // Step 2: ส่งต่อ
        return next(context);
    }
}
