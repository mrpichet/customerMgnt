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
[Route("api/session")]
public sealed class SessionController(IHttpClientFactory factory, ILogger<SessionController> logger) : CustomerMgntControllerBase
{
    /// <summary>ชื่อ HttpClient ที่ชี้ไป AuthServer:Authority</summary>
    public const string HttpClientName = "customermgnt.session";

    /// <summary>เข้าสู่ระบบด้วยชื่อผู้ใช้/รหัสผ่าน (จำกัดอัตราแบบเข้ม)</summary>
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
        // Step 1: ส่งต่อไป AuthServer (ไม่ log รหัสผ่าน/token)
        HttpClient http = factory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("api/auth/login", new { login = request.Login, password = request.Password }, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DependencyUnavailableException("AuthServer is unreachable.", ex);
        }

        using (response)
        {
            // Step 2: ข้อมูลรับรองผิด → 401 แบบไม่บอกรายละเอียด (กัน user enumeration)
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                logger.LogInformation("Login rejected by AuthServer ({Status}).", (int)response.StatusCode);
                throw new UnauthenticatedException("Invalid credentials.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DependencyUnavailableException($"AuthServer login failed ({(int)response.StatusCode}).");
            }

            // Step 3: ส่งเฉพาะ token และเวลาหมดอายุกลับไป
            SessionTokenDTO? token = await response.Content.ReadFromJsonAsync<SessionTokenDTO>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new DependencyUnavailableException("AuthServer returned an empty login response.");
            }

            return Ok(token);
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
public sealed record SessionTokenDTO(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>ผู้ใช้ปัจจุบัน</summary>
/// <param name="Subject">subject</param>
/// <param name="UserName">ชื่อผู้ใช้</param>
/// <param name="Roles">บทบาท</param>
/// <param name="Permissions">สิทธิ์</param>
public sealed record SessionUserDTO(string Subject, string UserName, IReadOnlyCollection<string> Roles, IReadOnlyCollection<string> Permissions);
