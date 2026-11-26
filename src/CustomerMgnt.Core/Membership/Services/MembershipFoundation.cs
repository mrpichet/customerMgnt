using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>สิทธิ์ของระบบสมาชิก (Application <c>customermgnt</c> ใน AuthServer — Claude.MD §17)</summary>
public static class MembershipPermissions
{
    public const string MemberRead = "member.read";
    public const string MemberWrite = "member.write";
    public const string MemberPii = "member.pii";
    public const string MemberStatus = "member.status";
    public const string MemberMerge = "member.merge";
    public const string MemberMergeApprove = "member.merge.approve";
    public const string MemberVerifyOverride = "member.verify.override";
    public const string MemberExport = "member.export";
    public const string TierManage = "tier.manage";
    public const string PointsRead = "points.read";
    public const string PointsAdjust = "points.adjust";
    public const string PointsAdjustApprove = "points.adjust.approve";
    public const string PointsRuleManage = "points.rule.manage";
    public const string PointsRuleApprove = "points.rule.approve";
    public const string PromotionRead = "promotion.read";
    public const string PromotionWrite = "promotion.write";
    public const string PromotionApprove = "promotion.approve";
    public const string CouponRead = "coupon.read";
    public const string CouponWrite = "coupon.write";
    public const string SegmentRead = "segment.read";
    public const string SegmentWrite = "segment.write";
    public const string CampaignMessageSend = "campaign.message.send";
    public const string TxnRead = "txn.read";
    public const string ReportRead = "report.read";
    public const string ReportExport = "report.export";
    public const string SettingsManage = "settings.manage";
    public const string AuditRead = "audit.read";
    public const string PrivacyManage = "privacy.manage";
    public const string PosLookup = "pos.lookup";
    public const string PosCheckout = "pos.checkout";
    public const string PosTransaction = "pos.transaction";
    public const string IntegrationRead = "integration.read";

    /// <summary>ทุกสิทธิ์</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        MemberRead, MemberWrite, MemberPii, MemberStatus, MemberMerge, MemberMergeApprove, MemberVerifyOverride, MemberExport, TierManage,
        PointsRead, PointsAdjust, PointsAdjustApprove, PointsRuleManage, PointsRuleApprove, PromotionRead, PromotionWrite, PromotionApprove,
        CouponRead, CouponWrite, SegmentRead, SegmentWrite, CampaignMessageSend, TxnRead, ReportRead, ReportExport, SettingsManage, AuditRead,
        PrivacyManage, PosLookup, PosCheckout, PosTransaction, IntegrationRead,
    ];

    /// <summary>บทบาทที่แนะนำ (spec หัวข้อ 1.1) — ใช้สร้าง Role ใน AuthServer</summary>
    public static IReadOnlyList<RoleTemplateDTO> RoleTemplates { get; } =
    [
        new() { Role = "membership-system-admin", Permissions = [MemberRead, TierManage, PointsRead, PromotionRead, CouponRead, SegmentRead, TxnRead, ReportRead, SettingsManage, AuditRead] },
        new() { Role = "membership-member-admin", Permissions = [MemberRead, MemberWrite, MemberPii, MemberStatus, MemberMerge, MemberMergeApprove, MemberVerifyOverride, MemberExport, TierManage, PointsRead, PointsAdjust, PointsAdjustApprove, PromotionRead, CouponRead, SegmentRead, TxnRead, ReportRead, ReportExport, PrivacyManage, AuditRead] },
        new() { Role = "membership-promotion-manager", Permissions = [MemberRead, PointsRead, PointsRuleManage, PromotionRead, PromotionWrite, CouponRead, CouponWrite, SegmentRead, SegmentWrite, CampaignMessageSend, TxnRead, ReportRead, ReportExport] },
        new() { Role = "membership-promotion-approver", Permissions = [MemberRead, PointsRead, PointsRuleApprove, PromotionRead, PromotionApprove, CouponRead, SegmentRead, ReportRead] },
        new() { Role = "membership-customer-service", Permissions = [MemberRead, MemberWrite, PointsRead, PointsAdjust, PromotionRead, CouponRead, SegmentRead, TxnRead] },
        new() { Role = "membership-analyst", Permissions = [MemberRead, MemberExport, PointsRead, PromotionRead, CouponRead, SegmentRead, TxnRead, ReportRead, ReportExport] },
        new() { Role = "membership-pos-client (scope)", Permissions = [PosLookup, PosCheckout, PosTransaction] },
        new() { Role = "membership-integration-client (scope)", Permissions = [IntegrationRead] },
    ];
}

/// <summary>
/// ผู้เรียกของระบบสมาชิก: ผู้ใช้ + client_id ของ token (= ระบบต้นทาง) + X-Request-Id
/// </summary>
/// <param name="User">ผู้ใช้/ client</param>
/// <param name="ClientId">claim <c>client_id</c></param>
/// <param name="RequestId">X-Request-Id</param>
public sealed record MembershipCaller(UserContextDTO User, string? ClientId, string? RequestId)
{
    /// <summary>token แบบ client_credentials (sub = client_id หรือไม่มีชื่อผู้ใช้)</summary>
    public bool IsClient => ClientId is not null && (string.Equals(User.Subject, ClientId, StringComparison.Ordinal) || string.IsNullOrEmpty(User.UserName));

    /// <summary>ระบบต้นทาง (ใช้แยก idempotency key / transaction ID)</summary>
    public string SourceSystem => ClientId ?? User.Subject;

    /// <summary>ผู้กระทำใน ledger/audit</summary>
    public string Actor => User == UserContextDTO.System ? "system" : IsClient ? $"client:{SourceSystem}" : $"user:{User.Subject}";

    /// <summary>ชนิดผู้กระทำ</summary>
    public ActorType ActorType => User == UserContextDTO.System ? ActorType.System : IsClient ? ActorType.Client : ActorType.User;

    /// <summary>ชื่อที่แสดง</summary>
    public string DisplayName => string.IsNullOrEmpty(User.UserName) ? Actor : User.UserName;

    /// <summary>งานเบื้องหลัง</summary>
    public static MembershipCaller System { get; } = new(UserContextDTO.System, null, null);

    /// <summary>บังคับสิทธิ์</summary>
    /// <param name="permission">สิทธิ์</param>
    public void Require(string permission) => Security.AccessGuard.Require(User, permission);

    /// <summary>มีสิทธิ์หรือไม่</summary>
    /// <param name="permission">สิทธิ์</param>
    /// <returns>true ถ้ามี</returns>
    public bool Has(string permission) => User.Has(permission);
}

/// <summary>JSON ภายในของระบบสมาชิก (camelCase, enum เป็นข้อความ)</summary>
public static class MembershipJson
{
    /// <summary>ตัวเลือก</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>serialize</summary>
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>เติม memberId / memberNo ลงใน payload ของ outbox (ใช้ตอนเพิ่มสมาชิกใหม่ที่ยังไม่รู้ id)</summary>
    public static string WithMember(string payloadJson, long memberId, string memberNo)
    {
        System.Text.Json.Nodes.JsonObject obj = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson) as System.Text.Json.Nodes.JsonObject ?? [];
        obj["memberId"] = memberId;
        obj["memberNo"] = memberNo;
        return obj.ToJsonString();
    }

    /// <summary>deserialize (null → ค่าเริ่มต้น)</summary>
    public static T Read<T>(string? json) where T : new() =>
        string.IsNullOrWhiteSpace(json) ? new T() : JsonSerializer.Deserialize<T>(json, Options) ?? new T();
}

/// <summary>กฎธุรกิจปัจจุบัน (cache 30 วินาที ลดการอ่านฐานข้อมูลในทุก checkout)</summary>
public sealed class MembershipRulesProvider
{
    private readonly IMembershipSettingsDAO _settings;
    private readonly TimeProvider _clock;
    private MembershipRulesDTO? _cached;
    private DateTimeOffset _cachedAt;

    /// <summary>สร้าง</summary>
    public MembershipRulesProvider(IMembershipSettingsDAO settings, TimeProvider clock)
    {
        _settings = settings;
        _clock = clock;
    }

    /// <summary>กฎปัจจุบัน (สำเนา)</summary>
    public async Task<MembershipRulesDTO> GetAsync(CancellationToken ct)
    {
        MembershipRulesDTO? c = _cached;
        if (c is null || _clock.GetUtcNow() - _cachedAt > TimeSpan.FromSeconds(30))
        {
            c = await _settings.GetRulesAsync(ct).ConfigureAwait(false);
            _cached = c;
            _cachedAt = _clock.GetUtcNow();
        }

        return c.Clone();
    }

    /// <summary>ล้าง cache หลังแก้กฎ</summary>
    public void Invalidate() => _cached = null;
}

/// <summary>ตัวช่วยสร้าง outbox / audit / รหัส</summary>
public static class MembershipEvents
{
    /// <summary>ข้อความ outbox ใหม่</summary>
    public static OutboxMessageDTO Outbox(string topic, object payload, DateTimeOffset now) => new()
    {
        EventId = Guid.CreateVersion7().ToString("N"),
        Topic = topic,
        PayloadJson = MembershipJson.Write(payload),
        NextAttemptAt = now,
        CreatedAt = now,
    };

    /// <summary>รายการ audit ใหม่ (before/after ต้อง mask มาแล้ว)</summary>
    public static MembershipAuditDTO Audit(MembershipCaller caller, string action, string target, object? before, object? after, DateTimeOffset now) => new()
    {
        At = now,
        Actor = caller.Actor,
        ActorType = caller.ActorType,
        Action = action,
        Target = target,
        RequestId = caller.RequestId,
        Ip = caller.User.ClientIp,
        BeforeMasked = before is null ? null : MembershipJson.Write(before),
        AfterMasked = after is null ? null : MembershipJson.Write(after),
    };

    /// <summary>รหัสสุ่มแบบ URL-safe เช่น <c>rsv_Ab3...</c></summary>
    public static string NewId(string prefix) => prefix + "_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(15)).Replace('+', '-').Replace('/', '_');

    /// <summary>บังคับ version (optimistic lock)</summary>
    public static void RequireVersion(long? version)
    {
        ValidationException.ThrowIf(version is null, "version", "Version is required.");
    }

    /// <summary>ลอง operation ใหม่เมื่อบันทึกชนกัน (สูงสุด 3 ครั้ง)</summary>
    public static async Task<T> RetryOnConflictAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (MembershipConcurrencyException) when (attempt < 3)
            {
                await Task.Delay(Random.Shared.Next(10, 50) * attempt).ConfigureAwait(false);
            }
            catch (MembershipConcurrencyException)
            {
                throw MembershipException.Conflict("version_conflict", "The data changed while processing. Please retry.");
            }
        }
    }
}
