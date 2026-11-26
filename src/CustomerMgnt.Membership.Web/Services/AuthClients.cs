using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCommon.UI.Auth;

namespace CustomerMgnt.Membership.Web.Services;

/// <summary>ค่าตั้งของ Member Portal (section <c>Portal</c>)</summary>
public sealed class PortalOptions
{
    /// <summary>ชื่อ section</summary>
    public const string SectionName = "Portal";

    /// <summary>OAuth client ของ Portal ใน AuthServer (ผูก Application customermgnt)</summary>
    public string ClientId { get; set; } = "membership-portal";

    /// <summary>Google OAuth client id (ว่าง = ไม่แสดงปุ่ม Google)</summary>
    public string? GoogleClientId { get; set; }

    /// <summary>Facebook App id (ว่าง = ไม่แสดงปุ่ม Facebook)</summary>
    public string? FacebookAppId { get; set; }
}

/// <summary>เจ้าหน้าที่ login กับ AuthServer ที่ <c>POST api/v1/auth/login</c> (AuthServer CLAUDE.md §2)</summary>
/// <param name="http">HttpClient ที่ชี้ AuthServer</param>
/// <param name="options">ค่าตั้ง Auth</param>
/// <param name="logger">logger (ไม่ log รหัสผ่าน/OTP/token)</param>
public sealed class AuthServerV1Client(HttpClient http, IOptions<AuthOptions> options, ILogger<AuthServerV1Client> logger) : IAuthClient
{
    /// <summary>สร้างจาก DI: base = Auth:AuthServerBaseUrl หรือ origin ของแอป (หลัง gateway)</summary>
    public static AuthServerV1Client Create(IServiceProvider sp, string hostBase) =>
        ActivatorUtilities.CreateInstance<AuthServerV1Client>(sp, CreateHttp(sp.GetRequiredService<IOptions<AuthOptions>>().Value.AuthServerBaseUrl, hostBase));

    /// <summary>HttpClient ไป AuthServer (บังคับ HTTPS ยกเว้น loopback)</summary>
    public static HttpClient CreateHttp(string? configured, string hostBase)
    {
        string baseUrl = string.IsNullOrWhiteSpace(configured) ? new Uri(new Uri(hostBase), "/").ToString() : configured;
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        var uri = new Uri(baseUrl, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            throw new InvalidOperationException("Auth:AuthServerBaseUrl ต้องเป็น HTTPS");
        }

        return new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <inheritdoc />
    public async Task<AuthResult> LoginAsync(string userName, string password, string? otpCode, string identitySource, CancellationToken cancellationToken = default)
    {
        AuthOptions o = options.Value;
        var body = new { userName, password, otpCode = string.IsNullOrWhiteSpace(otpCode) ? null : otpCode, source = identitySource, clientId = o.ClientId, scope = o.Scope, platform = "Web" };
        try
        {
            using HttpResponseMessage res = await http.PostAsJsonAsync("api/v1/auth/login", body, MembershipApiClient.Json, cancellationToken);
            return await AuthResults.ReadAsync(res, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("AuthServer unreachable: {Type}", ex.GetType().Name);
            return new AuthResult { Succeeded = false, ErrorCode = "network_error" };
        }
    }

    /// <inheritdoc />
    public async Task LogoutAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/logout");
            msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage _ = await http.SendAsync(msg, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // ออกจากระบบฝั่ง client ต่อได้แม้ server ติดต่อไม่ได้ (token หมดอายุเองใน 10 นาที)
        }
    }
}

/// <summary>แปลง LoginResultDTO ของ AuthServer เป็น AuthResult ของ webcommon</summary>
internal static class AuthResults
{
    private sealed class LoginResult
    {
        public bool Succeeded { get; set; }
        public bool MfaRequired { get; set; }
        public bool MfaEnrollmentRequired { get; set; }
        public bool PasswordChangeRequired { get; set; }
        public string? Error { get; set; }
        public TokenResponse? Tokens { get; set; }
    }

    private sealed class TokenResponse
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
    }

    public static async Task<AuthResult> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new AuthResult { Succeeded = false, ErrorCode = "rate_limited" };
        }

        LoginResult? r = null;
        try
        {
            r = await res.Content.ReadFromJsonAsync<LoginResult>(MembershipApiClient.Json, ct);
        }
        catch (JsonException)
        {
            // ไม่ใช่ LoginResultDTO
        }

        if (r is null)
        {
            return new AuthResult { Succeeded = false, ErrorCode = res.IsSuccessStatusCode ? "invalid_response" : "server_error" };
        }

        return new AuthResult
        {
            Succeeded = r.Succeeded && r.Tokens?.AccessToken is not null,
            MfaRequired = r.MfaRequired,
            MfaEnrollmentRequired = r.MfaEnrollmentRequired,
            PasswordChangeRequired = r.PasswordChangeRequired,
            ErrorCode = r.Error,
            AccessToken = r.Tokens?.AccessToken,
            RefreshToken = r.Tokens?.RefreshToken,
        };
    }
}

/// <summary>
/// สมาชิก login ด้วย Google / Facebook: browser ได้ token จาก provider แล้วส่งให้ AuthServer <c>POST api/v1/auth/external</c>
/// AuthServer ตรวจ token กับ provider และออก access token ของ AuthServer ให้
/// </summary>
/// <param name="auth">ค่าตั้ง Auth (base URL)</param>
/// <param name="portal">ค่าตั้ง Portal (clientId)</param>
/// <param name="state">ตัวเก็บสถานะ login ของ webcommon</param>
/// <param name="env">ที่อยู่ของแอป</param>
public sealed class ExternalLoginService(IOptions<AuthOptions> auth, IOptions<PortalOptions> portal, AppAuthenticationStateProvider state, Microsoft.AspNetCore.Components.WebAssembly.Hosting.IWebAssemblyHostEnvironment env)
{
    /// <summary>ส่ง token ของ provider ให้ AuthServer แล้วเข้าสู่ระบบ</summary>
    /// <param name="provider">Google | Facebook</param>
    /// <param name="providerToken">id_token (Google) / access token (Facebook)</param>
    /// <param name="otpCode">รหัส MFA (ถ้าบัญชีเปิด MFA)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผล login</returns>
    public async Task<AuthResult> SignInAsync(string provider, string providerToken, string? otpCode, CancellationToken ct = default)
    {
        using HttpClient http = AuthServerV1Client.CreateHttp(auth.Value.AuthServerBaseUrl, env.BaseAddress);
        var body = new { provider, token = providerToken, otpCode, clientId = portal.Value.ClientId, scope = "openid profile email" };
        AuthResult result;
        try
        {
            using HttpResponseMessage res = await http.PostAsJsonAsync("api/v1/auth/external", body, MembershipApiClient.Json, ct);
            result = await AuthResults.ReadAsync(res, ct);
        }
        catch (HttpRequestException)
        {
            return new AuthResult { Succeeded = false, ErrorCode = "network_error" };
        }

        if (result.Succeeded && result.AccessToken is not null)
        {
            await state.SignInAsync(result.AccessToken);
        }

        return result;
    }
}
