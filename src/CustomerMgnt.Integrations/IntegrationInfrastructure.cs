using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Integrations;

/// <summary>ค่าตั้งของระบบปลายทางหนึ่งระบบ</summary>
public sealed class ExternalSystemOptions
{
    /// <summary>URL ฐาน เช่น https://issuetracking.internal/ (ว่าง = ไม่เปิดใช้การเชื่อมต่อ)</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>timeout ต่อคำขอ (วินาที)</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>เปิดใช้หรือไม่ (มี BaseUrl ที่ถูกต้อง)</summary>
    public bool IsConfigured => Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}

/// <summary>บัญชีบริการสำหรับงานเบื้องหลัง (ไม่มีผู้ใช้ให้ส่งต่อ token) — login ผ่าน AuthServer <c>/api/auth/login</c></summary>
public sealed class ServiceAccountOptions
{
    /// <summary>URL ของ AuthServer</summary>
    public string AuthServerUrl { get; set; } = string.Empty;

    /// <summary>ชื่อผู้ใช้ของบัญชีบริการ</summary>
    public string Login { get; set; } = string.Empty;

    /// <summary>รหัสผ่าน (ควรมาจาก secret file /run/secrets)</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>ตั้งค่าครบหรือไม่</summary>
    public bool IsConfigured => Uri.TryCreate(AuthServerUrl, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(Login) && !string.IsNullOrEmpty(Password);
}

/// <summary>ค่าตั้งการเชื่อมต่อทั้งหมด (section <c>Integrations</c>)</summary>
public sealed class IntegrationsOptions
{
    /// <summary>ชื่อ section</summary>
    public const string SectionName = "Integrations";

    /// <summary>IssueTracking</summary>
    public ExternalSystemOptions IssueTracking { get; set; } = new();

    /// <summary>ProjectMgnt</summary>
    public ExternalSystemOptions ProjectMgnt { get; set; } = new();

    /// <summary>RequirementMgnt</summary>
    public ExternalSystemOptions RequirementMgnt { get; set; } = new();

    /// <summary>MessageHub</summary>
    public ExternalSystemOptions MessageHub { get; set; } = new();

    /// <summary>บัญชีบริการ</summary>
    public ServiceAccountOptions ServiceAccount { get; set; } = new();

    /// <summary>จำนวน issue สูงสุดที่อ่านต่อการค้นหา (ป้องกันหน่วยความจำล้น)</summary>
    public int MaxIssues { get; set; } = 20_000;
}

/// <summary>อ่าน bearer token ของผู้เรียกปัจจุบัน (Web API implement ด้วย IHttpContextAccessor; MCP stdio ไม่มี → ใช้ service account)</summary>
public interface ICallerTokenAccessor
{
    /// <summary>token ของผู้เรียก หรือ null</summary>
    /// <returns>JWT (ไม่รวมคำว่า Bearer)</returns>
    string? GetToken();
}

/// <summary>ค่าปริยาย: ไม่มีผู้เรียก</summary>
public sealed class NoCallerTokenAccessor : ICallerTokenAccessor
{
    /// <inheritdoc />
    public string? GetToken() => null;
}

/// <summary>ขอและ cache token ของบัญชีบริการ (login ครั้งเดียว ใช้จนใกล้หมดอายุ)</summary>
public sealed class ServiceTokenProvider : IDisposable
{
    /// <summary>ชื่อ HttpClient ของ AuthServer</summary>
    internal const string HttpClientName = "customermgnt.authserver";

    private readonly IHttpClientFactory _factory;
    private readonly IOptions<IntegrationsOptions> _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    /// <summary>สร้าง provider</summary>
    /// <param name="factory">HttpClient factory</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="clock">นาฬิกา</param>
    public ServiceTokenProvider(IHttpClientFactory factory, IOptions<IntegrationsOptions> options, TimeProvider clock)
    {
        _factory = factory;
        _options = options;
        _clock = clock;
    }

    /// <summary>token ของบัญชีบริการ (null = ไม่ได้ตั้งค่า)</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>JWT หรือ null</returns>
    /// <exception cref="DependencyUnavailableException">login ไม่สำเร็จ</exception>
    public async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        // Step 1: ไม่ได้ตั้งค่า → ไม่มี token
        ServiceAccountOptions o = _options.Value.ServiceAccount;
        if (!o.IsConfigured)
        {
            return null;
        }

        // Step 2: cache ที่ยังไม่หมดอายุ (เผื่อ 1 นาที)
        if (_token is { } cached && _expires > _clock.GetUtcNow().AddMinutes(1))
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } again && _expires > _clock.GetUtcNow().AddMinutes(1))
            {
                return again;
            }

            // Step 3: login (ไม่ log รหัสผ่าน/token)
            HttpClient http = _factory.CreateClient(HttpClientName);
            using HttpResponseMessage r = await http.PostAsJsonAsync(new Uri(new Uri(o.AuthServerUrl), "api/auth/login"), new { login = o.Login, password = o.Password }, IntegrationJson.Options, ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                throw new DependencyUnavailableException("Service account login to AuthServer failed.");
            }

            TokenResponse token = await r.Content.ReadFromJsonAsync<TokenResponse>(IntegrationJson.Options, ct).ConfigureAwait(false)
                ?? throw new DependencyUnavailableException("AuthServer returned an empty login response.");
            _token = token.AccessToken;
            _expires = token.ExpiresAt == default ? _clock.GetUtcNow().AddMinutes(15) : token.ExpiresAt;
            return _token;
        }
        catch (HttpRequestException ex)
        {
            throw new DependencyUnavailableException("AuthServer is unreachable.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>คืนทรัพยากร</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>คำตอบ login</summary>
    private sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);
}

/// <summary>
/// ใส่ Authorization: Bearer ให้ทุกคำขอไปยังระบบปลายทาง — token ของผู้เรียก (สิทธิ์ของผู้ใช้จริงถูกตรวจที่ปลายทาง) หรือของบัญชีบริการ
/// </summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly ICallerTokenAccessor _caller;
    private readonly ServiceTokenProvider _service;

    /// <summary>สร้าง handler</summary>
    /// <param name="caller">token ของผู้เรียก</param>
    /// <param name="service">token ของบัญชีบริการ</param>
    public BearerTokenHandler(ICallerTokenAccessor caller, ServiceTokenProvider service)
    {
        _caller = caller;
        _service = service;
    }

    /// <summary>ใส่ header แล้วส่งต่อ</summary>
    /// <param name="request">คำขอ</param>
    /// <param name="cancellationToken">ยกเลิก</param>
    /// <returns>คำตอบ</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Step 1: เลือก token (ผู้เรียกก่อน)
        string? token = _caller.GetToken() ?? await _service.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // Step 2: ส่งต่อ
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>JSON และการแปลงข้อผิดพลาด HTTP → exception ของ Common</summary>
internal static class IntegrationJson
{
    /// <summary>JSON แบบ web + enum เป็น string (ตรงกับทุกระบบในตระกูล)</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>ฐานของ gateway: ส่งคำขอ JSON และแปลงผลลัพธ์/ข้อผิดพลาดให้เป็นแบบเดียวกันทุกระบบ</summary>
public abstract class GatewayBase
{
    private readonly IHttpClientFactory _factory;

    /// <summary>สร้าง gateway</summary>
    /// <param name="factory">HttpClient factory</param>
    /// <param name="system">ชื่อระบบ (ใช้ในข้อความ/ log)</param>
    /// <param name="clientName">ชื่อ HttpClient</param>
    /// <param name="options">ค่าตั้งของระบบ</param>
    /// <param name="logger">logger</param>
    protected GatewayBase(IHttpClientFactory factory, string system, string clientName, ExternalSystemOptions options, ILogger logger)
    {
        _factory = factory;
        System = system;
        ClientName = clientName;
        SystemOptions = options;
        Logger = logger;
    }

    /// <summary>ชื่อระบบ</summary>
    protected string System { get; }

    /// <summary>ชื่อ HttpClient</summary>
    protected string ClientName { get; }

    /// <summary>ค่าตั้ง</summary>
    protected ExternalSystemOptions SystemOptions { get; }

    /// <summary>logger</summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// ส่งคำขอ JSON และอ่านผล
    /// <list type="bullet">
    /// <item>404 → default (null) เมื่อ <paramref name="nullOnNotFound"/> ไม่เช่นนั้น NotFoundException</item>
    /// <item>400/422 → ValidationException พร้อมข้อความจากปลายทาง</item>
    /// <item>401/403 → ForbiddenException, 409 → ConflictException</item>
    /// <item>5xx/timeout/เครือข่าย → DependencyUnavailableException</item>
    /// </list>
    /// </summary>
    /// <typeparam name="T">ชนิดผลลัพธ์</typeparam>
    /// <param name="method">HTTP method</param>
    /// <param name="path">path สัมพัทธ์ (ห้ามมี input ที่ไม่ได้ escape)</param>
    /// <param name="body">body (null = ไม่มี)</param>
    /// <param name="nullOnNotFound">404 คืน null</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลลัพธ์</returns>
    protected async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, bool nullOnNotFound, CancellationToken ct)
    {
        // Step 1: ต้องตั้งค่าก่อน
        if (!SystemOptions.IsConfigured)
        {
            throw new DependencyUnavailableException($"{System} integration is not configured (Integrations:{System}:BaseUrl).");
        }

        try
        {
            // Step 2: ส่งคำขอ
            HttpClient http = _factory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: IntegrationJson.Options);
            }

            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // Step 3: สำเร็จ → อ่าน JSON (204 → default)
            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default;
                return await response.Content.ReadFromJsonAsync<T>(IntegrationJson.Options, ct).ConfigureAwait(false);
            }

            // Step 4: แปลงข้อผิดพลาด
            if (response.StatusCode == HttpStatusCode.NotFound && nullOnNotFound) return default;
            string detail = await ReadProblemAsync(response, ct).ConfigureAwait(false);
            Logger.LogWarning("{System} {Method} {Path} returned {Status}: {Detail}", System, method, path, (int)response.StatusCode, detail);
            throw response.StatusCode switch
            {
                HttpStatusCode.NotFound => new NotFoundException($"{System}: the requested item was not found."),
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new ValidationException($"{System} rejected the request: {detail}"),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ForbiddenException($"{System} denied access for the current user."),
                HttpStatusCode.Conflict => new ConflictException($"{System}: {detail}"),
                _ => new DependencyUnavailableException(string.Create(CultureInfo.InvariantCulture, $"{System} returned HTTP {(int)response.StatusCode}.")),
            };
        }
        catch (HttpRequestException ex)
        {
            throw new DependencyUnavailableException($"{System} is unreachable.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException($"{System} did not respond in time.", ex);
        }
        catch (JsonException ex)
        {
            throw new DependencyUnavailableException($"{System} returned an unexpected response.", ex);
        }
    }

    /// <summary>อ่าน title/detail จาก ProblemDetails (ตัดความยาว ป้องกัน log ใหญ่เกิน)</summary>
    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;
            string? text = root.TryGetProperty("detail", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString()
                : root.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
            {
                text += " " + string.Join("; ", errors.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.Array ? $"{p.Name}: {string.Join(", ", p.Value.EnumerateArray().Select(v => v.ToString()))}" : $"{p.Name}: {p.Value}"));
            }

            raw = text ?? raw;
        }
        catch (JsonException)
        {
            // ไม่ใช่ JSON → ใช้ข้อความดิบ
        }

        raw = raw.Trim();
        return raw.Length > 300 ? raw[..300] : raw;
    }
}
