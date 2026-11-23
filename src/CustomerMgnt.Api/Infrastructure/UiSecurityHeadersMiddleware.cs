namespace CustomerMgnt.Api.Infrastructure;

/// <summary>
/// CSP สำหรับหน้า Blazor WebAssembly — Common ตั้ง <c>default-src 'none'</c> (เหมาะกับ API) ซึ่ง UI ใช้ไม่ได้
/// middleware นี้ต้องลงทะเบียน <b>ก่อน</b> UseCommonWebDefaults: OnStarting ทำงานย้อนลำดับ จึงรันหลังสุดและแทนค่าเฉพาะ path ของ UI
/// </summary>
/// <param name="next">middleware ถัดไป</param>
public sealed class UiSecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>CSP ที่เข้มที่สุดที่ Blazor WASM + DevExpress ทำงานได้ (ไม่มี script inline, ต่อเฉพาะ origin เดียวกัน)</summary>
    internal const string UiCsp =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

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
            context.Response.OnStarting(static state =>
            {
                HttpContext ctx = (HttpContext)state;
                ctx.Response.Headers.ContentSecurityPolicy = UiCsp;

                // _framework มีชื่อไฟล์แบบ fingerprint → cache ได้; index.html ต้องตรวจใหม่ทุกครั้ง
                ctx.Response.Headers.CacheControl = ctx.Request.Path.StartsWithSegments("/_framework") ? "public, max-age=31536000, immutable" : "no-cache";
                return Task.CompletedTask;
            }, context);
        }

        // Step 2: ส่งต่อ
        return next(context);
    }
}
