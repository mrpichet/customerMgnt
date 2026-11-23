using Common.AspNetCore.Authentication;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.AI;
using CustomerMgnt.Integrations;
using Microsoft.Net.Http.Headers;

namespace CustomerMgnt.Api.Infrastructure;

/// <summary>ส่งต่อ bearer token ของผู้เรียกไปยังระบบอื่น (ปลายทางตรวจสิทธิ์ของผู้ใช้คนเดียวกัน — ไม่ยกระดับสิทธิ์ผ่าน CustomerMgnt)</summary>
/// <param name="http">HttpContext accessor</param>
public sealed class HttpCallerTokenAccessor(IHttpContextAccessor http) : ICallerTokenAccessor
{
    /// <inheritdoc />
    public string? GetToken()
    {
        // Step 1: อ่าน Authorization: Bearer xxx ของคำขอปัจจุบัน (ไม่มี HttpContext = งานเบื้องหลัง)
        string? header = http.HttpContext?.Request.Headers[HeaderNames.Authorization];
        return header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}

/// <summary>ผู้ใช้ของ AI tool = ผู้ใช้ที่ยืนยันตัวตนกับ /mcp หรือ endpoint ที่เรียก IChatClient</summary>
/// <param name="http">HttpContext accessor</param>
public sealed class HttpAiUserContextAccessor(IHttpContextAccessor http) : IAiUserContextAccessor
{
    /// <inheritdoc />
    public ValueTask<UserContextDTO> GetAsync(CancellationToken ct)
    {
        HttpContext ctx = http.HttpContext ?? throw new UnauthenticatedException();
        return ValueTask.FromResult(ctx.GetUserContext());
    }
}
