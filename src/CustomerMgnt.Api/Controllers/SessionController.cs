using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.AspNetCore.Authentication;
using Common.AspNetCore.Hosting;
using Common.Core.Domain;
using Common.Core.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CustomerMgnt.Api.Controllers;

/// <summary>
/// เข้าสู่ระบบสำหรับหน้า UI: ส่งต่อ login ไป AuthServer (UI ไม่ต้องรู้ที่อยู่ AuthServer และไม่ต้องเปิด CORS)
/// CustomerMgnt ไม่เก็บรหัสผ่านและไม่ log ข้อมูลรับรอง
/// </summary>
/// <param name="factory">HttpClient factory (client ชื่อ <see cref="HttpClientName"/>)</param>
/// <param name="logger">logger</param>
/// <param name="configuration">ค่าตั้ง (AuthServer:LoginClientId, AuthServer:LoginSource)</param>
[Route("api/session")]
public sealed class SessionController(IHttpClientFactory factory, ILogger<SessionController> logger, IConfiguration configuration) : CustomerMgntControllerBase
{
    /// <summary>ชื่อ HttpClient ที่ชี้ไป AuthServer:Authority</summary>
    public const string HttpClientName = "customermgnt.session";

    /// <summary>OAuth client ของหน้าจอหลักใน AuthServer เมื่อไม่ได้ตั้ง <c>AuthServer:LoginClientId</c></summary>
    public const string DefaultClientId = "customermgnt-web";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ProblemDetails Problem401(string title, string code)
    {
        var p = new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = title, Type = "https://httpstatuses.io/401" };
        p.Extensions["code"] = code;
        p.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return p;
    }

    /// <summary>
    /// เข้าสู่ระบบด้วยชื่อผู้ใช้/รหัสผ่าน (จำกัดอัตราแบบเข้ม) ผ่าน AuthServer <c>POST api/v1/auth/login</c>
    /// ผู้ใช้ที่เปิด MFA: ครั้งแรกได้ 401 <c>code = mfa_required</c> แล้วส่งซ้ำพร้อม <c>otpCode</c>
    /// </summary>
    /// <param name="request">ข้อมูลรับรอง</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>access token และเวลาหมดอายุ</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(CommonWebHostExtensions.StrictRateLimitPolicy)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<SessionTokenDTO>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SessionTokenDTO>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Step 1: ส่งต่อไป AuthServer v1 (ไม่ log รหัสผ่าน/OTP/token)
        HttpClient http = factory.CreateClient(HttpClientName);
        var body = new
        {
            userName = request.Login,
            password = request.Password,
            otpCode = string.IsNullOrWhiteSpace(request.OtpCode) ? null : request.OtpCode.Trim(),
            source = configuration["AuthServer:LoginSource"] is { Length: > 0 } src ? src : "Local",
            clientId = configuration["AuthServer:LoginClientId"] is { Length: > 0 } cid ? cid : DefaultClientId,
            scope = "openid profile email",
            platform = "Web",
        };
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("api/v1/auth/login", body, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DependencyUnavailableException("AuthServer is unreachable.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests);
            }

            AuthServerLoginResult? result = null;
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    result = await response.Content.ReadFromJsonAsync<AuthServerLoginResult>(Json, ct);
                }
                catch (JsonException)
                {
                    result = null;
                }
            }

            // Step 2: ต้องใช้ OTP / OTP ผิด → บอกให้ UI ถาม OTP (ผ่านรหัสผ่านแล้ว จึงไม่ใช่ user enumeration)
            if (response.StatusCode == HttpStatusCode.Unauthorized && result is not null && (result.MfaRequired || result.Error is "mfa_required" or "invalid_otp"))
            {
                string code = result.Error == "invalid_otp" ? "invalid_otp" : "mfa_required";
                logger.LogInformation("Login needs MFA ({Code}).", code);
                return Unauthorized(Problem401(code == "invalid_otp" ? "Invalid OTP code." : "OTP code required.", code));
            }

            // Step 3: ข้อมูลรับรองผิดหรือถูกปฏิเสธด้วยเหตุอื่น → 401 แบบไม่บอกรายละเอียด (กัน user enumeration)
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                logger.LogInformation("Login rejected by AuthServer ({Status}).", (int)response.StatusCode);
                throw new UnauthenticatedException("Invalid credentials.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DependencyUnavailableException($"AuthServer login failed ({(int)response.StatusCode}).");
            }

            // Step 4: ส่งเฉพาะ token และเวลาหมดอายุกลับไป
            if (result is not { Succeeded: true, Tokens.AccessToken.Length: > 0 })
            {
                throw new DependencyUnavailableException("AuthServer returned an empty login response.");
            }

            int expiresIn = result.Tokens.ExpiresIn > 0 ? result.Tokens.ExpiresIn : 600;
            return Ok(new SessionTokenDTO(result.Tokens.AccessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn))
            {
                PasswordChangeRequired = result.PasswordChangeRequired,
                MfaEnrollmentRequired = result.MfaEnrollmentRequired,
            });
        }
    }

    /// <summary>ข้อมูลผู้ใช้ปัจจุบัน (สิทธิ์ที่มี — ให้ UI ซ่อน/แสดงเมนู)</summary>
    /// <returns>ผู้ใช้</returns>
    [HttpGet("me")]
    [Authorize]
    public ActionResult<SessionUserDTO> Me()
    {
        UserContextDTO user = CurrentUser;
        return Ok(new SessionUserDTO(user.Subject, user.UserName, user.Roles, user.Permissions));
    }
}

/// <summary>token จาก AuthServer</summary>
/// <param name="AccessToken">JWT</param>
/// <param name="ExpiresAt">เวลาหมดอายุ</param>
public sealed record SessionTokenDTO(string AccessToken, DateTimeOffset ExpiresAt)
{
    /// <summary>true = ต้องเปลี่ยนรหัสผ่านที่ AuthServer ก่อนใช้งาน</summary>
    public bool PasswordChangeRequired { get; init; }

    /// <summary>true = client บังคับ MFA แต่ผู้ใช้ยังไม่ลงทะเบียน</summary>
    public bool MfaEnrollmentRequired { get; init; }
}

/// <summary>LoginResultDTO ของ AuthServer (อ่านเฉพาะ field ที่ใช้)</summary>
internal sealed class AuthServerLoginResult
{
    public bool Succeeded { get; set; }
    public bool MfaRequired { get; set; }
    public bool PasswordChangeRequired { get; set; }
    public bool MfaEnrollmentRequired { get; set; }
    public string? Error { get; set; }
    public AuthServerTokens? Tokens { get; set; }
}

/// <summary>TokenResponseDTO ของ AuthServer</summary>
internal sealed class AuthServerTokens
{
    public string AccessToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
}

/// <summary>ผู้ใช้ปัจจุบัน</summary>
/// <param name="Subject">subject</param>
/// <param name="UserName">ชื่อผู้ใช้</param>
/// <param name="Roles">บทบาท</param>
/// <param name="Permissions">สิทธิ์</param>
public sealed record SessionUserDTO(string Subject, string UserName, IReadOnlyCollection<string> Roles, IReadOnlyCollection<string> Permissions);
