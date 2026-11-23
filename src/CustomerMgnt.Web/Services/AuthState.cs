using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace CustomerMgnt.Web.Services;

/// <summary>
/// สถานะการเข้าสู่ระบบของแท็บนี้: token เก็บใน sessionStorage (หายเมื่อปิดแท็บ, ไม่แชร์ข้ามแท็บ)
/// สิทธิ์ของผู้ใช้อ่านจาก /api/session/me เพื่อซ่อน/แสดงเมนู (การบังคับสิทธิ์จริงอยู่ที่ API เสมอ)
/// </summary>
/// <param name="js">JS runtime (เรียก sessionStorage โดยตรง — ไม่มี JS ของแอปเอง)</param>
/// <param name="http">HttpClient same-origin</param>
public sealed class AuthState(IJSRuntime js, HttpClient http)
{
    private const string TokenKey = "cm.token";
    private const string ExpiresKey = "cm.expires";
    private bool _loaded;

    /// <summary>access token ปัจจุบัน</summary>
    public string? Token { get; private set; }

    /// <summary>เวลาหมดอายุ</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>ข้อมูลผู้ใช้</summary>
    public SessionUser? User { get; private set; }

    /// <summary>เข้าสู่ระบบอยู่และ token ยังไม่หมดอายุ</summary>
    public bool IsAuthenticated => Token is not null && ExpiresAt > DateTimeOffset.UtcNow;

    /// <summary>แจ้งเมื่อสถานะเปลี่ยน</summary>
    public event Action? Changed;

    /// <summary>มีสิทธิ์หรือไม่</summary>
    /// <param name="permission">ชื่อสิทธิ์</param>
    /// <returns>true = มี</returns>
    public bool Has(string permission) => User?.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>โหลด token จาก sessionStorage (ครั้งแรกเท่านั้น)</summary>
    /// <returns>Task</returns>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        string? token = await js.InvokeAsync<string?>("sessionStorage.getItem", TokenKey);
        string? expires = await js.InvokeAsync<string?>("sessionStorage.getItem", ExpiresKey);
        if (token is not null && DateTimeOffset.TryParse(expires, out DateTimeOffset exp) && exp > DateTimeOffset.UtcNow)
        {
            Token = token;
            ExpiresAt = exp;
            await LoadUserAsync();
        }
    }

    /// <summary>เข้าสู่ระบบ</summary>
    /// <param name="login">ชื่อผู้ใช้</param>
    /// <param name="password">รหัสผ่าน</param>
    /// <returns>null = สำเร็จ, ไม่เช่นนั้นข้อความผิดพลาด</returns>
    public async Task<string?> LoginAsync(string login, string password)
    {
        using HttpResponseMessage r = await http.PostAsJsonAsync("api/session/login", new { login, password });
        if (!r.IsSuccessStatusCode)
        {
            return (int)r.StatusCode switch
            {
                401 => "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง",
                429 => "พยายามเข้าสู่ระบบบ่อยเกินไป กรุณารอสักครู่",
                _ => "ไม่สามารถเชื่อมต่อระบบยืนยันตัวตนได้",
            };
        }

        TokenResponse? t = await r.Content.ReadFromJsonAsync<TokenResponse>(ApiClient.Json);
        if (t is null)
        {
            return "คำตอบจากระบบไม่ถูกต้อง";
        }

        Token = t.AccessToken;
        ExpiresAt = t.ExpiresAt == default ? DateTimeOffset.UtcNow.AddMinutes(15) : t.ExpiresAt;
        await js.InvokeVoidAsync("sessionStorage.setItem", TokenKey, Token);
        await js.InvokeVoidAsync("sessionStorage.setItem", ExpiresKey, ExpiresAt.ToString("O"));
        await LoadUserAsync();
        Changed?.Invoke();
        return null;
    }

    /// <summary>ออกจากระบบ (ลบ token ออกจาก sessionStorage)</summary>
    /// <returns>Task</returns>
    public async Task LogoutAsync()
    {
        Token = null;
        User = null;
        await js.InvokeVoidAsync("sessionStorage.removeItem", TokenKey);
        await js.InvokeVoidAsync("sessionStorage.removeItem", ExpiresKey);
        Changed?.Invoke();
    }

    private async Task LoadUserAsync()
    {
        using HttpRequestMessage req = new(HttpMethod.Get, "api/session/me");
        req.Headers.Authorization = new("Bearer", Token);
        using HttpResponseMessage r = await http.SendAsync(req);
        if (r.IsSuccessStatusCode)
        {
            User = await r.Content.ReadFromJsonAsync<SessionUser>(ApiClient.Json);
        }
        else
        {
            Token = null;
        }
    }

    private sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);
}

/// <summary>ผู้ใช้ปัจจุบัน</summary>
/// <param name="Subject">subject</param>
/// <param name="UserName">ชื่อผู้ใช้</param>
/// <param name="Roles">บทบาท</param>
/// <param name="Permissions">สิทธิ์</param>
public sealed record SessionUser(string Subject, string UserName, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);
