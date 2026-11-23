using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomerMgnt.Web.Services;

/// <summary>ข้อผิดพลาดจาก API (แปลงจาก ProblemDetails เป็นข้อความที่แสดงผู้ใช้ได้)</summary>
/// <param name="status">HTTP status</param>
/// <param name="message">ข้อความ</param>
public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    /// <summary>HTTP status</summary>
    public HttpStatusCode Status { get; } = status;
}

/// <summary>client ของ CustomerMgnt REST API: แนบ Bearer token, แปลง ProblemDetails, เด้งไปหน้า login เมื่อ 401</summary>
/// <param name="http">HttpClient same-origin</param>
/// <param name="auth">สถานะการเข้าสู่ระบบ</param>
public sealed class ApiClient(HttpClient http, AuthState auth)
{
    /// <summary>JSON options เดียวกับฝั่ง API (camelCase + enum เป็น string)</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>GET</summary>
    /// <typeparam name="T">ชนิดผลลัพธ์</typeparam>
    /// <param name="path">path สัมพัทธ์ (เช่น api/customers)</param>
    /// <returns>ผลลัพธ์</returns>
    public Task<T> GetAsync<T>(string path) => SendAsync<T>(HttpMethod.Get, path, null);

    /// <summary>POST</summary>
    /// <typeparam name="T">ชนิดผลลัพธ์</typeparam>
    /// <param name="path">path</param>
    /// <param name="body">body</param>
    /// <returns>ผลลัพธ์</returns>
    public Task<T> PostAsync<T>(string path, object? body) => SendAsync<T>(HttpMethod.Post, path, body);

    /// <summary>PUT</summary>
    /// <typeparam name="T">ชนิดผลลัพธ์</typeparam>
    /// <param name="path">path</param>
    /// <param name="body">body</param>
    /// <returns>ผลลัพธ์</returns>
    public Task<T> PutAsync<T>(string path, object? body) => SendAsync<T>(HttpMethod.Put, path, body);

    /// <summary>PATCH</summary>
    /// <typeparam name="T">ชนิดผลลัพธ์</typeparam>
    /// <param name="path">path</param>
    /// <param name="body">body</param>
    /// <returns>ผลลัพธ์</returns>
    public Task<T> PatchAsync<T>(string path, object? body) => SendAsync<T>(HttpMethod.Patch, path, body);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body)
    {
        // Step 1: สร้างคำขอพร้อม token
        using HttpRequestMessage req = new(method, path);
        if (auth.Token is { } token)
        {
            req.Headers.Authorization = new("Bearer", token);
        }

        if (body is not null)
        {
            req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        // Step 2: ส่งและแปลงผลลัพธ์
        using HttpResponseMessage r = await http.SendAsync(req);
        if (r.StatusCode == HttpStatusCode.Unauthorized)
        {
            await auth.LogoutAsync();
            throw new ApiException(r.StatusCode, "หมดเวลาการเข้าสู่ระบบ กรุณาเข้าสู่ระบบใหม่");
        }

        if (!r.IsSuccessStatusCode)
        {
            throw new ApiException(r.StatusCode, await ReadProblemAsync(r));
        }

        if (r.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
        {
            return default!;
        }

        return (await r.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage r)
    {
        // Step 3: ProblemDetails → title/detail + errors ของ field
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            JsonElement root = doc.RootElement;
            string msg = root.TryGetProperty("detail", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString()!
                : root.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : r.ReasonPhrase ?? "Error";
            if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
            {
                IEnumerable<string> details = errors.EnumerateObject().SelectMany(p => p.Value.ValueKind == JsonValueKind.Array
                    ? p.Value.EnumerateArray().Select(v => $"{p.Name}: {v.GetString()}")
                    : [$"{p.Name}: {p.Value}"]);
                msg += " — " + string.Join("; ", details);
            }

            return r.StatusCode switch
            {
                HttpStatusCode.Forbidden => "ไม่มีสิทธิ์ทำรายการนี้",
                HttpStatusCode.Conflict => "ข้อมูลถูกแก้ไขโดยผู้อื่นหรือซ้ำกับที่มีอยู่ กรุณาโหลดใหม่: " + msg,
                HttpStatusCode.ServiceUnavailable => "ระบบที่เชื่อมต่อไม่พร้อมใช้งาน: " + msg,
                _ => msg,
            };
        }
        catch (JsonException)
        {
            return $"{(int)r.StatusCode} {r.ReasonPhrase}";
        }
    }
}
