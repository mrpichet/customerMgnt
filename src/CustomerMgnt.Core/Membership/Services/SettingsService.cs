using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>ตั้งค่า: กฎธุรกิจ 10 ข้อ, ฉบับ consent, ระบบภายนอก, บทบาทที่แนะนำ, audit log</summary>
public sealed class SettingsService
{
    private readonly IMembershipSettingsDAO _settings;
    private readonly IMembershipAuditDAO _audit;
    private readonly MembershipRulesProvider _rules;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public SettingsService(IMembershipSettingsDAO settings, IMembershipAuditDAO audit, MembershipRulesProvider rules, TimeProvider clock)
    {
        _settings = settings;
        _audit = audit;
        _rules = rules;
        _clock = clock;
    }

    /// <summary>กฎธุรกิจปัจจุบัน</summary>
    public async Task<MembershipRulesDTO> GetRulesAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.SettingsManage);
        return await _settings.GetRulesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>บันทึกกฎธุรกิจ (ตรวจ version) — มีผลกับรายการใหม่ภายใน 30 วินาที</summary>
    public async Task<MembershipRulesDTO> SaveRulesAsync(MembershipCaller caller, MembershipRulesDTO rules, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(rules);
        caller.Require(MembershipPermissions.SettingsManage);
        var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        bool valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(rules, new System.ComponentModel.DataAnnotations.ValidationContext(rules), errors, validateAllProperties: true);
        ValidationException.ThrowIf(!valid, errors.FirstOrDefault()?.MemberNames.FirstOrDefault() ?? "rules", errors.FirstOrDefault()?.ErrorMessage ?? "Invalid rules.");
        ValidationException.ThrowIf(!IsValidZone(rules.TimeZoneId), "timeZoneId", "Unknown time zone.");
        rules.NonEarningItemCodes = [.. rules.NonEarningItemCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToUpperInvariant()).Distinct()];
        MembershipRulesDTO before = await _settings.GetRulesAsync(ct).ConfigureAwait(false);
        if (!await _settings.SaveRulesAsync(rules, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        rules.Version++;
        _rules.Invalidate();
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "settings.rules", "settings:rules", before, rules, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        return rules;
    }

    /// <summary>ฉบับ consent ทั้งหมด</summary>
    public Task<IReadOnlyList<ConsentVersionDTO>> ListConsentsAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.SettingsManage);
        return _settings.ListConsentVersionsAsync(ct);
    }

    /// <summary>ร่างฉบับใหม่ (ยังไม่เผยแพร่)</summary>
    public async Task<ConsentVersionDTO> CreateConsentAsync(MembershipCaller caller, ConsentVersionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.SettingsManage);
        DateTimeOffset now = _clock.GetUtcNow();
        var v = new ConsentVersionDTO { Title = request.Title.Trim(), Body = request.Body, EffectiveAt = request.EffectiveAt == default ? now : request.EffectiveAt, CreatedAt = now };
        v.Id = await _settings.InsertConsentVersionAsync(v, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "settings.consent_create", $"consent:{v.Id}", null, new { v.Title, v.EffectiveAt }, now)], ct).ConfigureAwait(false);
        return v;
    }

    /// <summary>เผยแพร่ฉบับ (แก้เนื้อหาไม่ได้อีก) — สมาชิกใหม่ต้องยอมรับฉบับที่มีผลล่าสุด</summary>
    public async Task<ConsentVersionDTO> PublishConsentAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.SettingsManage);
        ConsentVersionDTO v = (await _settings.ListConsentVersionsAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == id) ?? throw new NotFoundException("Consent version not found.");
        if (v.IsPublished)
        {
            return v;
        }

        DateTimeOffset now = _clock.GetUtcNow();
        v.IsPublished = true;
        v.PublishedAt = now;
        await _settings.UpdateConsentVersionAsync(v, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "settings.consent_publish", $"consent:{v.Id}", null, new { v.Title, v.EffectiveAt }, now)], ct).ConfigureAwait(false);
        return v;
    }

    /// <summary>ระบบภายนอกที่เรียก API (POS / integration)</summary>
    public Task<IReadOnlyList<IntegrationClientDTO>> ListIntegrationsAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.SettingsManage);
        return _settings.ListIntegrationClientsAsync(ct);
    }

    /// <summary>เพิ่ม/แก้ระบบภายนอก</summary>
    public async Task<IReadOnlyList<IntegrationClientDTO>> SaveIntegrationAsync(MembershipCaller caller, IntegrationClientDTO client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(client);
        caller.Require(MembershipPermissions.SettingsManage);
        client.ClientId = client.ClientId.Trim();
        client.Name = client.Name.Trim();
        await _settings.UpsertIntegrationClientAsync(client, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "settings.integration", $"client:{client.ClientId}", null, client, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        return await _settings.ListIntegrationClientsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>บทบาทและสิทธิ์ที่แนะนำให้สร้างใน AuthServer (Application <c>customermgnt</c>)</summary>
    public IReadOnlyList<RoleTemplateDTO> ListRoleTemplates(MembershipCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.SettingsManage);
        return MembershipPermissions.RoleTemplates;
    }

    /// <summary>ค้น audit log</summary>
    public Task<PagedResultDTO<MembershipAuditDTO>> SearchAuditAsync(MembershipCaller caller, AuditSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(criteria);
        caller.Require(MembershipPermissions.AuditRead);
        return _audit.SearchAsync(criteria, ct);
    }

    private static bool IsValidZone(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
