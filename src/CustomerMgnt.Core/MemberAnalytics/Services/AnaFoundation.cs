using Common.Core.Exceptions;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Membership.Services;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

/// <summary>สิทธิ์ของระบบวิเคราะห์ (Claude.MD §29)</summary>
public static class AnaPermissions
{
    public const string DashboardRead = "ana.dashboard.read";
    public const string MemberRead = "ana.member.read";
    public const string MemberDetail = "ana.member.detail";
    public const string PromotionRead = "ana.promotion.read";
    public const string PromotionAll = "ana.promotion.all";
    public const string ReportBuild = "ana.report.build";
    public const string ReportShare = "ana.report.share";
    public const string Export = "ana.export";
    public const string AlertManage = "ana.alert.manage";
    public const string SettingsManage = "ana.settings.manage";
    public const string ScopeAll = "ana.scope.all";
    public const string Ingest = "analytics.ingest";

    /// <summary>ทุกสิทธิ์ของผู้ใช้ (ไม่รวม scope ของ client)</summary>
    public static IReadOnlyList<string> All { get; } =
        [DashboardRead, MemberRead, MemberDetail, PromotionRead, PromotionAll, ReportBuild, ReportShare, Export, AlertManage, SettingsManage, ScopeAll];

    /// <summary>มีสิทธิ์ ana.* อย่างน้อย 1 ตัว</summary>
    public static bool HasAny(MembershipCaller caller) => All.Any(caller.Has);

    /// <summary>ต้องมีสิทธิ์ ana.* อย่างน้อย 1 ตัว</summary>
    public static void RequireAny(MembershipCaller caller)
    {
        if (!HasAny(caller))
        {
            throw new ForbiddenException("Analytics permission required.");
        }
    }
}

/// <summary>ข้อผิดพลาดของระบบวิเคราะห์ (รหัสใน Claude.MD §41)</summary>
public static class AnaErrors
{
    public static MembershipException Bad(string code, string message) => new(400, code, message);
}

/// <summary>ค่าตั้งของระบบวิเคราะห์ (เอกสาร settings/main, cache 30 วินาที)</summary>
public sealed class AnaSettingsProvider
{
    private const string Id = "main";
    private readonly IAnaDocumentDAO _docs;
    private readonly TimeProvider _clock;
    private (AnalyticsSettingsDTO Value, DateTimeOffset At)? _cache;

    /// <summary>สร้าง</summary>
    public AnaSettingsProvider(IAnaDocumentDAO docs, TimeProvider clock)
    {
        _docs = docs;
        _clock = clock;
    }

    /// <summary>ค่าตั้ง (ยังไม่มี = ค่าเริ่มต้น version 0)</summary>
    public async Task<AnalyticsSettingsDTO> GetAsync(CancellationToken ct)
    {
        if (_cache is { } c && _clock.GetUtcNow() - c.At < TimeSpan.FromSeconds(30))
        {
            return c.Value;
        }

        AnaDocument<AnalyticsSettingsDTO>? doc = await _docs.GetAsync<AnalyticsSettingsDTO>(AnaCollections.Settings, Id, ct).ConfigureAwait(false);
        AnalyticsSettingsDTO v = doc?.Value ?? new AnalyticsSettingsDTO();
        v.Version = doc?.Version ?? 0;
        _cache = (v, _clock.GetUtcNow());
        return v;
    }

    /// <summary>บันทึก (ตรวจ version)</summary>
    public async Task<AnalyticsSettingsDTO> SaveAsync(AnalyticsSettingsDTO value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        DateTimeOffset now = _clock.GetUtcNow();
        bool ok = value.Version == 0
            ? await _docs.InsertAsync(AnaCollections.Settings, Id, null, value, now, ct).ConfigureAwait(false)
            : await _docs.UpdateAsync(AnaCollections.Settings, Id, null, value.Version, value, now, ct).ConfigureAwait(false);
        if (!ok)
        {
            throw MembershipException.VersionConflict();
        }

        _cache = null;
        return await GetAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>ขอบเขตข้อมูล (สาขา / ช่องทาง / แบรนด์) ของผู้ใช้ — deny by default เว้นแต่มี ana.scope.all</summary>
public sealed class DataScopeService
{
    private readonly IAnaDocumentDAO _docs;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public DataScopeService(IAnaDocumentDAO docs, TimeProvider clock)
    {
        _docs = docs;
        _clock = clock;
    }

    /// <summary>ขอบเขตของผู้เรียก</summary>
    public async Task<DataScope> ResolveAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.User == Common.Core.Domain.UserContextDTO.System || caller.Has(AnaPermissions.ScopeAll))
        {
            return DataScope.All;
        }

        EffectiveScopeDTO e = await EffectiveAsync(caller.User.Subject, caller.User.Roles, ct).ConfigureAwait(false);
        return ToScope(e);
    }

    /// <summary>แปลงขอบเขตที่มีผลเป็นตัวกรอง</summary>
    public static DataScope ToScope(EffectiveScopeDTO e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.All)
        {
            return DataScope.All;
        }

        if (e.Sources.Count == 0)
        {
            return DataScope.None;
        }

        static HashSet<string>? Set(List<string> v) => v.Contains("*") ? null : new HashSet<string>(v, StringComparer.OrdinalIgnoreCase);
        HashSet<string>? channels = Set(e.Channels);
        return new DataScope(false, Set(e.Branches),
            channels is null ? null : new HashSet<SalesChannel>(channels.Select(c => Enum.TryParse(c, true, out SalesChannel x) ? (SalesChannel?)x : null).Where(c => c is not null).Select(c => c!.Value)),
            Set(e.Brands));
    }

    /// <summary>ขอบเขตรวมของผู้ใช้ (จากกฎของผู้ใช้และทุก role)</summary>
    public async Task<EffectiveScopeDTO> EffectiveAsync(string sub, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var e = new EffectiveScopeDTO { Sub = sub };
        var rules = (await _docs.ListAsync<DataScopeRuleDTO>(AnaCollections.DataScope, null, ct).ConfigureAwait(false))
            .Select(d => d.Value)
            .Where(r => (r.SubjectType == ScopeSubjectType.User && string.Equals(r.Subject, sub, StringComparison.Ordinal))
                || (r.SubjectType == ScopeSubjectType.Role && roles.Contains(r.Subject, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        foreach (DataScopeRuleDTO r in rules)
        {
            e.Sources.Add(new ScopeSourceDTO { SubjectType = r.SubjectType, Subject = r.Subject, RuleId = r.Id });
            Merge(e.Branches, r.Branches);
            Merge(e.Channels, r.Channels);
            Merge(e.Brands, r.Brands);
        }

        // มิติที่ไม่มีกฎไหนกำหนด = ไม่จำกัด (กฎมีอย่างน้อย 1 ข้อแล้ว)
        if (rules.Count > 0)
        {
            foreach (List<string> dim in new[] { e.Branches, e.Channels, e.Brands })
            {
                if (dim.Count == 0)
                {
                    dim.Add("*");
                }
            }
        }

        e.All = rules.Count > 0 && e.Branches.Contains("*") && e.Channels.Contains("*") && e.Brands.Contains("*");
        return e;

        static void Merge(List<string> target, List<string> add)
        {
            if (target.Contains("*"))
            {
                return;
            }

            if (add.Contains("*"))
            {
                target.Clear();
                target.Add("*");
                return;
            }

            foreach (string v in add.Where(v => !target.Contains(v, StringComparer.OrdinalIgnoreCase)))
            {
                target.Add(v);
            }
        }
    }

    /// <summary>กฎทั้งหมด (กรองตามผู้ถูกกำหนด)</summary>
    public async Task<IReadOnlyList<DataScopeRuleDTO>> ListAsync(MembershipCaller caller, string? subject, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.SettingsManage);
        return [.. (await _docs.ListAsync<DataScopeRuleDTO>(AnaCollections.DataScope, null, ct).ConfigureAwait(false))
            .Select(WithVersion)
            .Where(r => string.IsNullOrWhiteSpace(subject) || r.Subject.Contains(subject.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.SubjectType).ThenBy(r => r.Subject, StringComparer.Ordinal)];
    }

    /// <summary>อ่านกฎ</summary>
    public async Task<DataScopeRuleDTO> GetAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.SettingsManage);
        return WithVersion(await _docs.GetAsync<DataScopeRuleDTO>(AnaCollections.DataScope, id.ToString(), ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Data scope rule not found."));
    }

    /// <summary>สร้างกฎ</summary>
    public async Task<DataScopeRuleDTO> CreateAsync(MembershipCaller caller, DataScopeRuleDTO rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(rule);
        caller.Require(AnaPermissions.SettingsManage);
        Normalize(rule);
        rule.Id = Guid.NewGuid();
        rule.UpdatedBy = caller.Actor;
        rule.UpdatedAt = _clock.GetUtcNow();
        await _docs.InsertAsync(AnaCollections.DataScope, rule.Id.ToString(), rule.Subject, rule, rule.UpdatedAt, ct).ConfigureAwait(false);
        rule.Version = 1;
        return rule;
    }

    /// <summary>แก้กฎ (ตรวจ version)</summary>
    public async Task<DataScopeRuleDTO> UpdateAsync(MembershipCaller caller, Guid id, DataScopeRuleDTO rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(rule);
        caller.Require(AnaPermissions.SettingsManage);
        Normalize(rule);
        rule.Id = id;
        rule.UpdatedBy = caller.Actor;
        rule.UpdatedAt = _clock.GetUtcNow();
        if (!await _docs.UpdateAsync(AnaCollections.DataScope, id.ToString(), rule.Subject, rule.Version, rule, rule.UpdatedAt, ct).ConfigureAwait(false))
        {
            _ = await _docs.GetAsync<DataScopeRuleDTO>(AnaCollections.DataScope, id.ToString(), ct).ConfigureAwait(false) ?? throw new NotFoundException("Data scope rule not found.");
            throw MembershipException.VersionConflict();
        }

        rule.Version++;
        return rule;
    }

    /// <summary>ลบกฎ</summary>
    public async Task DeleteAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.SettingsManage);
        if (!await _docs.DeleteAsync(AnaCollections.DataScope, id.ToString(), ct).ConfigureAwait(false))
        {
            throw new NotFoundException("Data scope rule not found.");
        }
    }

    private static DataScopeRuleDTO WithVersion(AnaDocument<DataScopeRuleDTO> d)
    {
        d.Value.Version = d.Version;
        return d.Value;
    }

    private static void Normalize(DataScopeRuleDTO r)
    {
        r.Subject = r.Subject.Trim();
        static List<string> Clean(List<string> v) => v.Contains("*") ? ["*"] : [.. v.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
        r.Branches = Clean(r.Branches);
        r.Channels = Clean(r.Channels);
        r.Brands = Clean(r.Brands);
        foreach (string c in r.Channels.Where(c => c != "*"))
        {
            ValidationException.ThrowIf(!Enum.TryParse(c, true, out SalesChannel _), "channels", $"Unknown channel '{c}'.");
        }

        ValidationException.ThrowIf(r.Branches.Count == 0 && r.Channels.Count == 0 && r.Brands.Count == 0, "branches", "Choose at least one value or \"*\" (all).");
    }
}

/// <summary>ตัวกรองที่แก้แล้ว: ช่วงวัน ช่วงเปรียบเทียบ ขอบเขต และค่าที่ตัดแล้ว</summary>
public sealed class ResolvedFilter
{
    public required AppliedFilterDTO Applied { get; init; }
    public required DataScope Scope { get; init; }
    public bool Clamped { get; init; }
    /// <summary>ขอบเขตว่าง (หรือตัดแล้วไม่เหลือค่าใดในมิติที่เลือก)</summary>
    public bool Empty { get; init; }

    public DateOnly From => Applied.From;
    public DateOnly To => Applied.To;

    /// <summary>สาขาที่ใช้ query (null = ทั้งหมด)</summary>
    public IReadOnlyCollection<string>? Branches => Applied.Branch.Count > 0 ? Applied.Branch : Scope.Branches;

    /// <summary>ช่องทางที่ใช้ query (null = ทั้งหมด)</summary>
    public IReadOnlyCollection<SalesChannel>? Channels => Applied.Channel.Count > 0 ? Applied.Channel : Scope.Channels;

    /// <summary>แบรนด์ที่ใช้ (null = ทั้งหมด)</summary>
    public IReadOnlyCollection<string>? Brands => Applied.Brand.Count > 0 ? Applied.Brand : Scope.Brands;

    /// <summary>ตัวกรองนี้ใช้กับช่วงอื่น</summary>
    public ResolvedFilter WithRange(DateOnly from, DateOnly to) => new()
    {
        Applied = new AppliedFilterDTO
        {
            From = from, To = to, Compare = CompareMode.None, Granularity = Applied.Granularity, Branch = Applied.Branch, Channel = Applied.Channel, Brand = Applied.Brand,
            Category = Applied.Category, Tier = Applied.Tier, RegisteredChannel = Applied.RegisteredChannel, SourceCampaign = Applied.SourceCampaign, Campaign = Applied.Campaign,
        },
        Scope = Scope, Clamped = Clamped, Empty = Empty,
    };
}

/// <summary>แก้ตัวกรองตามขอบเขต (§28.1)</summary>
public sealed class AnaFilterResolver
{
    /// <summary>ช่วงยาวสุด (วัน)</summary>
    public const int MaxRangeDays = 731;

    private readonly DataScopeService _scopes;
    private readonly IAnaDocumentDAO _docs;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public AnaFilterResolver(DataScopeService scopes, IAnaDocumentDAO docs, TimeProvider clock)
    {
        _scopes = scopes;
        _docs = docs;
        _clock = clock;
    }

    /// <summary>ตัวกรองที่ใช้จริง</summary>
    public async Task<ResolvedFilter> ResolveAsync(MembershipCaller caller, AnalyticsFilterDTO? filter, CancellationToken ct, DataScope? scopeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        AnalyticsFilterDTO f = filter?.Clone() ?? new AnalyticsFilterDTO();

        // Step 1: saved filter (ค่าที่ส่งมาทับค่าใน saved filter)
        if (f.SavedFilterId is Guid sid && await _docs.GetAsync<SavedFilterDTO>(AnaCollections.SavedFilter, sid.ToString(), ct).ConfigureAwait(false) is { } saved
            && string.Equals(saved.Owner, caller.User.Subject, StringComparison.Ordinal))
        {
            AnalyticsFilterDTO s = saved.Value.Filter;
            f.From ??= s.From;
            f.To ??= s.To;
            if (f.Branch.Count == 0) { f.Branch = [.. s.Branch]; }
            if (f.Channel.Count == 0) { f.Channel = [.. s.Channel]; }
            if (f.Brand.Count == 0) { f.Brand = [.. s.Brand]; }
            if (f.Category.Count == 0) { f.Category = [.. s.Category]; }
            if (f.Tier.Count == 0) { f.Tier = [.. s.Tier]; }
            if (f.RegisteredChannel.Count == 0) { f.RegisteredChannel = [.. s.RegisteredChannel]; }
            if (f.SourceCampaign.Count == 0) { f.SourceCampaign = [.. s.SourceCampaign]; }
            if (f.Campaign.Count == 0) { f.Campaign = [.. s.Campaign]; }
        }

        // Step 2: ช่วงวัน (ค่าเริ่มต้น 30 วันล่าสุดถึงเมื่อวาน)
        DateOnly today = AnaTime.Today(_clock);
        DateOnly to = f.To ?? today.AddDays(-1);
        DateOnly from = f.From ?? to.AddDays(-29);
        ValidationException.ThrowIf(from > to, "from", "from must not be after to.");
        if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
        {
            throw AnaErrors.Bad("range_too_large", $"The date range must not exceed {MaxRangeDays} days.");
        }

        int days = to.DayNumber - from.DayNumber + 1;
        Granularity g = f.Granularity ?? (days <= 62 ? Granularity.Day : days <= 366 ? Granularity.Week : Granularity.Month);
        (DateOnly From, DateOnly To)? cmp = AnaMath.CompareRange(from, to, f.Compare);

        // Step 3: ตัดตามขอบเขต
        DataScope scope = scopeOverride ?? await _scopes.ResolveAsync(caller, ct).ConfigureAwait(false);
        bool clamped = false;
        List<string> branch = Clamp(f.Branch, scope.Branches, ref clamped);
        List<SalesChannel> channel = scope.Channels is null ? [.. f.Channel.Distinct()] : [.. f.Channel.Distinct().Where(c => { bool ok = scope.Channels.Contains(c); clamped |= !ok; return ok; })];
        List<string> brand = Clamp(f.Brand, scope.Brands, ref clamped);
        bool empty = scope.Empty
            || (f.Branch.Count > 0 && branch.Count == 0) || (f.Channel.Count > 0 && channel.Count == 0) || (f.Brand.Count > 0 && brand.Count == 0);

        return new ResolvedFilter
        {
            Applied = new AppliedFilterDTO
            {
                From = from, To = to, Compare = f.Compare, CompareFrom = cmp?.From, CompareTo = cmp?.To, Granularity = g, Branch = branch, Channel = channel, Brand = brand,
                Category = [.. f.Category.Distinct(StringComparer.OrdinalIgnoreCase)], Tier = [.. f.Tier.Distinct(StringComparer.OrdinalIgnoreCase)],
                RegisteredChannel = [.. f.RegisteredChannel.Distinct()], SourceCampaign = [.. f.SourceCampaign.Select(x => x.ToUpperInvariant()).Distinct()],
                Campaign = [.. f.Campaign.Select(x => x.ToUpperInvariant()).Distinct()],
            },
            Scope = scope,
            Clamped = clamped,
            Empty = empty,
        };

        static List<string> Clamp(List<string> wanted, IReadOnlySet<string>? allowed, ref bool clamped)
        {
            var result = new List<string>();
            foreach (string v in wanted.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (allowed is null || allowed.Contains(v))
                {
                    result.Add(v);
                }
                else
                {
                    clamped = true;
                }
            }

            return result;
        }
    }
}
