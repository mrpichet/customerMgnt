using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomerMgnt.Membership.Web.Services;

/// <summary>ข้อผิดพลาดจาก API (ProblemDetails: status + code + detail)</summary>
public sealed class ApiException(int status, string code, string message, JsonElement? payload = null, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    /// <summary>HTTP status</summary>
    public int Status { get; } = status;

    /// <summary>รหัส error (Claude.MD §25)</summary>
    public string Code { get; } = code;

    /// <summary>ข้อมูลแนบ (เช่น quote ใหม่)</summary>
    public JsonElement? Payload { get; } = payload;

    /// <summary>รายละเอียดราย field</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
}

/// <summary>เรียก REST API ของระบบสมาชิก (api/v1) — JSON camelCase, enum เป็นข้อความ</summary>
/// <param name="http">HttpClient ที่แนบ Bearer token แล้ว</param>
public sealed class MembershipApiClient(HttpClient http)
{
    /// <summary>ตัวเลือก JSON เดียวกับ server</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>GET แล้วอ่านเป็น T</summary>
    public Task<T> GetAsync<T>(string url, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Get, url, null, null, ct);

    /// <summary>GET พร้อม query จาก object (เฉพาะค่าที่ไม่ว่าง)</summary>
    public Task<T> GetAsync<T>(string url, object? query, CancellationToken ct = default) => GetAsync<T>(url + Query(query), ct);

    /// <summary>POST JSON</summary>
    public Task<T> PostAsync<T>(string url, object? body, CancellationToken ct = default, string? idempotencyKey = null) => SendAsync<T>(HttpMethod.Post, url, body, idempotencyKey, ct);

    /// <summary>POST JSON ไม่สนผลลัพธ์</summary>
    public Task PostAsync(string url, object? body, CancellationToken ct = default) => SendAsync<JsonElement?>(HttpMethod.Post, url, body, null, ct);

    /// <summary>PUT JSON</summary>
    public Task<T> PutAsync<T>(string url, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Put, url, body, null, ct);

    /// <summary>ดาวน์โหลดไฟล์</summary>
    /// <summary>DELETE (204)</summary>
    public Task DeleteAsync(string url, CancellationToken ct = default) => SendAsync<JsonElement?>(HttpMethod.Delete, url, null, null, ct);

    public async Task<(byte[] Content, string FileName, string ContentType)> DownloadAsync(string url, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await http.GetAsync(url, ct);
        await EnsureAsync(res, ct);
        byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct);
        string name = res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "export";
        return (bytes, name, res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, string? idempotencyKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("X-Request-Id", Guid.NewGuid().ToString("N"));
        if (idempotencyKey is not null)
        {
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        using HttpResponseMessage res = await http.SendAsync(req, ct);
        await EnsureAsync(res, ct);
        if (res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0)
        {
            return default!;
        }

        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task EnsureAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            return;
        }

        string code = res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "unauthorized",
            HttpStatusCode.Forbidden => "access_denied",
            HttpStatusCode.NotFound => "not_found",
            HttpStatusCode.TooManyRequests => "rate_limited",
            _ => "error",
        };
        string message = string.Empty;
        JsonElement? payload = null;
        Dictionary<string, string[]>? errors = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String) code = c.GetString()!;
            if (root.TryGetProperty("detail", out JsonElement d) && d.ValueKind == JsonValueKind.String) message = d.GetString()!;
            if (root.TryGetProperty("payload", out JsonElement p)) payload = p.Clone();
            if (root.TryGetProperty("errors", out JsonElement e) && e.ValueKind == JsonValueKind.Object)
            {
                errors = e.Deserialize<Dictionary<string, string[]>>(Json);
            }
        }
        catch (JsonException)
        {
            // body ไม่ใช่ ProblemDetails — ใช้รหัสจาก status
        }

        throw new ApiException((int)res.StatusCode, code, message, payload, errors);
    }

    /// <summary>สร้าง query string จาก property ที่มีค่า (รองรับ list และ DateTimeOffset)</summary>
    public static string Query(object? query)
    {
        if (query is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (PropertyInfo p in query.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? v = p.GetValue(query);
            if (v is null || v is string { Length: 0 })
            {
                continue;
            }

            string name = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
            IEnumerable<object?> values = v is System.Collections.IEnumerable list and not string ? list.Cast<object?>() : [v];
            foreach (object? item in values)
            {
                string s = item switch
                {
                    null => string.Empty,
                    DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
                    bool b => b ? "true" : "false",
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => item.ToString() ?? string.Empty,
                };
                if (s.Length == 0) continue;
                sb.Append(sb.Length == 0 ? '?' : '&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(s));
            }
        }

        return sb.ToString();
    }
}
