using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Membership.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Api.Infrastructure;

/// <summary>ส่วนประกอบ HTTP ของ Membership API (Claude.MD §16): X-Request-Id, Idempotency-Key, error code, rate limit</summary>
public static class MembershipApi
{
    /// <summary>rate limit ของ POS / integration (token bucket ต่อ client)</summary>
    public const string PosRateLimit = "pos";

    /// <summary>rate limit ของ Portal สมาชิก (ต่อผู้ใช้)</summary>
    public const string PortalRateLimit = "member-portal";

    /// <summary>rate limit ของ Analytics API (120 คำขอ/นาที ต่อผู้ใช้)</summary>
    public const string AnalyticsRateLimit = "analytics";

    /// <summary>rate limit ของ ingest (20 คำขอ/วินาที ต่อ client)</summary>
    public const string IngestRateLimit = "analytics-ingest";

    /// <summary>key ใน HttpContext.Items ของ request id</summary>
    public const string RequestIdItem = "membership.requestId";

    /// <summary>ลงทะเบียน service ของ API (ต้องเรียกก่อน AddCommonWebDefaults เพื่อให้ exception handler ทำงานก่อนของ Common)</summary>
    /// <param name="services">DI</param>
    /// <returns>services เดิม</returns>
    public static IServiceCollection AddMembershipApi(this IServiceCollection services)
    {
        services.AddExceptionHandler<MembershipExceptionHandler>();
        services.AddScoped<IdempotencyFilter>();
        services.Configure<RateLimiterOptions>(o =>
        {
            o.AddPolicy(PosRateLimit, ctx => RateLimitPartition.GetTokenBucketLimiter("pos:" + CallerKey(ctx), _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 50, TokensPerPeriod = 50, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 0, AutoReplenishment = true,
            }));
            o.AddPolicy(PortalRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter("portal:" + CallerKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            }));
            o.AddPolicy(AnalyticsRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter("ana:" + CallerKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            }));
            o.AddPolicy(IngestRateLimit, ctx => RateLimitPartition.GetTokenBucketLimiter("ana-ingest:" + CallerKey(ctx), _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 20, TokensPerPeriod = 20, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 0, AutoReplenishment = true,
            }));
        });
        return services;
    }

    /// <summary>ใส่/คืน X-Request-Id ให้ทุกคำขอใต้ /api/v1</summary>
    /// <param name="app">app</param>
    /// <returns>app เดิม</returns>
    public static IApplicationBuilder UseMembershipRequestId(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        string? incoming = ctx.Request.Headers["X-Request-Id"].FirstOrDefault();
        string id = IsSafeRequestId(incoming) ? incoming! : Guid.NewGuid().ToString("N");
        ctx.Items[RequestIdItem] = id;
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers["X-Request-Id"] = id;
            return Task.CompletedTask;
        });
        await next(ctx).ConfigureAwait(false);
    });

    /// <summary>ตัวตนผู้เรียกของระบบสมาชิก</summary>
    /// <param name="http">HTTP context</param>
    /// <param name="user">ผู้ใช้จาก JWT</param>
    /// <returns>caller</returns>
    public static MembershipCaller CallerOf(HttpContext http, Common.Core.Domain.UserContextDTO user) =>
        new(user, http.User.FindFirstValue("client_id") is { Length: > 0 } c && IsClientToken(http.User, c) ? c : null, http.Items[RequestIdItem] as string);

    /// <summary>token แบบ client_credentials (sub = client_id หรือไม่มีชื่อผู้ใช้)</summary>
    private static bool IsClientToken(ClaimsPrincipal user, string clientId)
    {
        string? sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return string.IsNullOrEmpty(sub) || string.Equals(sub, clientId, StringComparison.Ordinal) || user.FindFirstValue("preferred_username") is null;
    }

    private static bool IsSafeRequestId(string? v) =>
        !string.IsNullOrEmpty(v) && v.Length <= 64 && v.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');

    private static string CallerKey(HttpContext ctx) =>
        ctx.User.FindFirstValue("client_id") ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
}

/// <summary>
/// แปลงข้อผิดพลาดภายในของระบบสมาชิกเป็น ProblemDetails พร้อม <c>code</c> (§25) และแนบ <c>payload</c> (เช่น quote ใหม่ของ quote_changed)
/// ข้อผิดพลาดอื่นส่งต่อให้ CommonExceptionHandler
/// </summary>
/// <param name="problemDetails">ตัวเขียน ProblemDetails</param>
/// <param name="json">ตัวเลือก JSON ของ MVC</param>
public sealed class MembershipExceptionHandler(IProblemDetailsService problemDetails, IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> json) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        (int status, string code, string detail, object? payload) = exception switch
        {
            MembershipConcurrencyException => (409, "version_conflict", "The record was changed by someone else. Reload and try again.", (object?)null),
            MembershipLimitException le => (409, le.Code, le.Code == "budget_exhausted" ? "The campaign budget is exhausted." : "The campaign usage limit was reached.", null),
            MembershipException { Payload: not null } me => (me.StatusCode, me.ErrorCode, me.Message, me.Payload),
            _ => (0, string.Empty, string.Empty, null),
        };
        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = status == 409 ? "Conflict" : "Error", Detail = detail, Type = $"https://httpstatuses.io/{status}" };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? httpContext.TraceIdentifier;
        if (payload is not null)
        {
            problem.Extensions["payload"] = JsonSerializer.SerializeToElement(payload, payload.GetType(), json.Value.JsonSerializerOptions);
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception }).ConfigureAwait(false);
    }
}

/// <summary>บังคับ <c>Idempotency-Key</c> สำหรับ POST จาก POS / integration (§16)</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IdempotentAttribute : ServiceFilterAttribute
{
    /// <summary>สร้าง</summary>
    public IdempotentAttribute() : base(typeof(IdempotencyFilter)) { }
}

/// <summary>
/// เก็บผลต่อ (sourceSystem, key) 7 วัน: ส่งซ้ำ body เดิม = ได้ response เดิม (<c>Idempotent-Replayed: true</c>),
/// body ต่าง = 422 <c>idempotency_key_reused</c>, ยังทำอยู่ = 409 <c>request_in_progress</c>; ล้มด้วย exception = ลบคีย์ให้ลองใหม่ได้
/// </summary>
/// <param name="dao">ที่เก็บคีย์</param>
/// <param name="clock">นาฬิกา</param>
/// <param name="json">ตัวเลือก JSON ของ MVC</param>
public sealed class IdempotencyFilter(IIdempotencyDAO dao, TimeProvider clock, IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> json) : IAsyncActionFilter
{
    /// <summary>ชื่อ header</summary>
    public const string Header = "Idempotency-Key";

    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        HttpContext http = context.HttpContext;
        string? key = http.Request.Headers[Header].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new MembershipException(400, "idempotency_key_required", "The Idempotency-Key header is required.");
        }

        ValidationException.ThrowIf(key.Length > 100, "Idempotency-Key", "Idempotency-Key must be at most 100 characters.");
        string source = http.User.FindFirstValue("client_id") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        JsonSerializerOptions opts = json.Value.JsonSerializerOptions;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(http.Request.Method + " " + http.Request.Path + "\n"
            + JsonSerializer.Serialize(context.ActionArguments.Where(a => a.Value is not CancellationToken).OrderBy(a => a.Key, StringComparer.Ordinal).ToDictionary(a => a.Key, a => a.Value), opts))));
        DateTimeOffset now = clock.GetUtcNow();
        IdempotencyRecordDTO? existing = await dao.TryBeginAsync(new IdempotencyRecordDTO { SourceSystem = source, Key = key, RequestHash = hash, CreatedAt = now, ExpiresAt = now + Retention }, http.RequestAborted).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                throw MembershipException.Unprocessable("idempotency_key_reused", "The Idempotency-Key was already used with a different request.");
            }

            if (existing.StatusCode == 0)
            {
                throw MembershipException.Conflict("request_in_progress", "A request with this Idempotency-Key is still being processed.");
            }

            http.Response.Headers["Idempotent-Replayed"] = "true";
            context.Result = existing.ResponseJson is null
                ? new StatusCodeResult(existing.StatusCode)
                : new ContentResult { StatusCode = existing.StatusCode, Content = existing.ResponseJson, ContentType = "application/json; charset=utf-8" };
            return;
        }

        ActionExecutedContext done;
        try
        {
            done = await next().ConfigureAwait(false);
        }
        catch
        {
            await dao.DeleteAsync(source, key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (done.Exception is not null && !done.ExceptionHandled)
        {
            await dao.DeleteAsync(source, key, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        (int status, string? body) = done.Result switch
        {
            ObjectResult o => (o.StatusCode ?? StatusCodes.Status200OK, o.Value is null ? null : JsonSerializer.Serialize(o.Value, o.Value.GetType(), opts)),
            StatusCodeResult s => (s.StatusCode, (string?)null),
            _ => (StatusCodes.Status200OK, (string?)null),
        };
        await dao.CompleteAsync(source, key, status, body, CancellationToken.None).ConfigureAwait(false);
    }
}
