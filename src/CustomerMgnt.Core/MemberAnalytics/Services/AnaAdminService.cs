using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Membership.Services;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

/// <summary>ตั้งค่า สถานะข้อมูล ปัญหาข้อมูล และสั่งคำนวณใหม่ (§39)</summary>
public sealed class AnaAdminService
{
    private readonly AnaSettingsProvider _settings;
    private readonly IAnaDocumentDAO _docs;
    private readonly IAnaReadModelDAO _model;
    private readonly IMembershipAuditDAO _audit;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public AnaAdminService(AnaSettingsProvider settings, IAnaDocumentDAO docs, IAnaReadModelDAO model, IMembershipAuditDAO audit, TimeProvider clock)
    {
        _settings = settings;
        _docs = docs;
        _model = model;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>AN-25 อ่านค่าตั้ง</summary>
    public Task<AnalyticsSettingsDTO> GetSettingsAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.SettingsManage);
        return _settings.GetAsync(ct);
    }

    /// <summary>AN-25 บันทึกค่าตั้ง (เปลี่ยนเกณฑ์ = read model ถูกทำเครื่องหมายย้อนหลัง recomputeDays)</summary>
    public async Task<AnalyticsSettingsDTO> SaveSettingsAsync(MembershipCaller caller, AnalyticsSettingsDTO value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(value);
        caller.Require(AnaPermissions.SettingsManage);
        ValidationException.ThrowIf(value.AtRiskDays <= value.ActiveDays, "atRiskDays", "atRiskDays must be greater than activeDays.");
        ValidationException.ThrowIf(value.PurchaseValueBands.Count is < 1 or > 10 || value.PurchaseValueBands.Any(b => b <= 0), "purchaseValueBands", "Enter 1 to 10 positive amounts.");
        value.PurchaseValueBands = [.. value.PurchaseValueBands.Distinct().Order()];
        value.AllowRecipientDomains = [.. value.AllowRecipientDomains.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim().ToLowerInvariant()).Distinct()];
        AnalyticsSettingsDTO before = await _settings.GetAsync(ct).ConfigureAwait(false);
        AnalyticsSettingsDTO saved = await _settings.SaveAsync(value, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        DateOnly today = AnaTime.Day(now);
        DateOnly[] days = [.. AnaTime.Days(today.AddDays(-saved.RecomputeDays), today)];
        foreach (string t in AnaTables.All)
        {
            await _model.MarkDirtyAsync(t, days, ct).ConfigureAwait(false);
        }

        await _audit.AppendAsync([MembershipEvents.Audit(caller, "ana.settings.update", "ana_settings:main", before, saved, now)], ct).ConfigureAwait(false);
        return saved;
    }

    /// <summary>AN-26 สถานะข้อมูล</summary>
    public async Task<DataStatusDTO> DataStatusAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.DashboardRead);
        DateTimeOffset now = _clock.GetUtcNow();
        var dto = new DataStatusDTO();
        foreach (string t in AnaTables.All)
        {
            TableStatusDTO st = (await _docs.GetAsync<TableStatusDTO>(AnaCollections.TableStatus, t, ct).ConfigureAwait(false))?.Value ?? new TableStatusDTO { Table = t };
            st.DirtyDays = (await _model.ListDirtyAsync(t, ct).ConfigureAwait(false)).Count;
            st.LagMinutes = st.Watermark is DateTimeOffset w ? (int)(now - w).TotalMinutes : null;
            dto.Tables.Add(st);
        }

        string cutoff = now.UtcDateTime.AddHours(-24).ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture);
        dto.Sources = [.. (await _docs.ListAsync<AnaSourceStatsDoc>(AnaCollections.SourceStatus, null, ct).ConfigureAwait(false)).Select(d => d.Value).Select(s => new SourceStatusDTO
        {
            Source = s.Source, ClientId = s.ClientId, LastReceivedAt = s.LastReceivedAt,
            Last24h = s.Hours.Where(h => string.CompareOrdinal(h.Key, cutoff) > 0).Sum(h => h.Value[0]),
            Rejected24h = s.Hours.Where(h => string.CompareOrdinal(h.Key, cutoff) > 0).Sum(h => h.Value[1]),
        }).OrderBy(s => s.Source, StringComparer.Ordinal)];
        dto.OpenIssues = (await _docs.ListAsync<DataIssueDTO>(AnaCollections.DataIssue, null, ct).ConfigureAwait(false)).Count;
        return dto;
    }

    /// <summary>ปัญหาข้อมูล</summary>
    public async Task<PagedResultDTO<DataIssueDTO>> DataIssuesAsync(MembershipCaller caller, string? kind, int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.DashboardRead);
        ValidationException.ThrowIf(pageSize is < 1 or > 200, "pageSize", "pageSize must be between 1 and 200.");
        List<DataIssueDTO> all = [.. (await _docs.ListAsync<DataIssueDTO>(AnaCollections.DataIssue, null, ct).ConfigureAwait(false)).Select(d => d.Value)
            .Where(i => string.IsNullOrWhiteSpace(kind) || i.Kind.StartsWith(kind.Trim(), StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => i.LastSeen)];
        return AnaPaged.Of(all, page, pageSize);
    }

    /// <summary>สั่งคำนวณใหม่ (≤ 731 วัน; มีงานที่ช่วงทับกันรออยู่ = 409 refresh_running)</summary>
    public async Task<RefreshRunDTO> RequestRefreshAsync(MembershipCaller caller, RefreshRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(AnaPermissions.SettingsManage);
        ValidationException.ThrowIf(request.From > request.To, "from", "from must not be after to.");
        if (request.To.DayNumber - request.From.DayNumber + 1 > AnaFilterResolver.MaxRangeDays)
        {
            throw AnaErrors.Bad("range_too_large", $"The date range must not exceed {AnaFilterResolver.MaxRangeDays} days.");
        }

        foreach (string t in request.Tables)
        {
            ValidationException.ThrowIf(!AnaTables.All.Contains(t), "tables", $"Unknown table '{t}'.");
        }

        IEnumerable<string> tables = request.Tables.Count == 0 ? AnaTables.All : request.Tables;
        bool overlap = (await _docs.ListAsync<RefreshRunDTO>(AnaCollections.RefreshRun, null, ct).ConfigureAwait(false)).Select(d => d.Value)
            .Any(r => r.Status is JobStatus.Queued or JobStatus.Running && r.From <= request.To && request.From <= r.To
                && (r.Tables.Count == 0 || request.Tables.Count == 0 || r.Tables.Intersect(tables).Any()));
        if (overlap)
        {
            throw MembershipException.Conflict("refresh_running", "A refresh for an overlapping range is already queued or running.");
        }

        DateTimeOffset now = _clock.GetUtcNow();
        var run = new RefreshRunDTO
        {
            Id = Guid.NewGuid(), Tables = [.. request.Tables.Distinct()], From = request.From, To = request.To, Status = JobStatus.Queued, RequestedBy = caller.Actor, RequestedAt = now,
        };
        await _docs.InsertAsync(AnaCollections.RefreshRun, run.Id.ToString(), run.RequestedBy, run, now, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "ana.refresh.request", $"ana_refresh:{run.Id}", null, request, now)], ct).ConfigureAwait(false);
        return run;
    }

    /// <summary>งานคำนวณใหม่ล่าสุด</summary>
    public async Task<IReadOnlyList<RefreshRunDTO>> ListRefreshRunsAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.DashboardRead);
        return [.. (await _docs.ListAsync<RefreshRunDTO>(AnaCollections.RefreshRun, null, ct).ConfigureAwait(false)).Select(d => d.Value).OrderByDescending(r => r.RequestedAt).Take(50)];
    }

    // ================================================================== Funnel (§37)

    private static FunnelDTO WithVersion(AnaDocument<FunnelDTO> d)
    {
        d.Value.Version = d.Version;
        return d.Value;
    }

    private static void ValidateFunnel(FunnelDTO f)
    {
        f.Name = f.Name.Trim();
        ValidationException.ThrowIf(f.Name.Length == 0, "name", "name is required.");
        ValidationException.ThrowIf(f.Steps.Count is < 2 or > 6, "steps", "A funnel needs 2 to 6 steps.");
        ValidationException.ThrowIf(f.Steps.Distinct().Count() != f.Steps.Count, "steps", "Steps must not repeat.");
        ValidationException.ThrowIf(f.MaxGapHours is < 1 or > 24 * 90, "maxGapHours", "maxGapHours must be between 1 and 2160.");
    }

    /// <summary>funnel ทั้งหมด (ana.promotion.read)</summary>
    public async Task<IReadOnlyList<FunnelDTO>> ListFunnelsAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.PromotionRead);
        return [.. (await _docs.ListAsync<FunnelDTO>(AnaCollections.Funnel, null, ct).ConfigureAwait(false)).Select(WithVersion).OrderBy(f => f.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>อ่าน funnel (ana.promotion.read)</summary>
    public async Task<FunnelDTO> GetFunnelAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.PromotionRead);
        return WithVersion(await _docs.GetAsync<FunnelDTO>(AnaCollections.Funnel, id.ToString(), ct).ConfigureAwait(false) ?? throw new NotFoundException("Funnel not found."));
    }

    /// <summary>AN-16F สร้าง funnel (ana.settings.manage)</summary>
    public async Task<FunnelDTO> CreateFunnelAsync(MembershipCaller caller, FunnelDTO funnel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(funnel);
        caller.Require(AnaPermissions.SettingsManage);
        ValidateFunnel(funnel);
        funnel.Id = Guid.NewGuid();
        DateTimeOffset now = _clock.GetUtcNow();
        await _docs.InsertAsync(AnaCollections.Funnel, funnel.Id.ToString(), caller.Actor, funnel, now, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "ana.funnel.create", $"ana_funnel:{funnel.Id}", null, funnel, now)], ct).ConfigureAwait(false);
        funnel.Version = 1;
        return funnel;
    }

    /// <summary>แก้ funnel (ตรวจ version)</summary>
    public async Task<FunnelDTO> UpdateFunnelAsync(MembershipCaller caller, Guid id, FunnelDTO funnel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(funnel);
        caller.Require(AnaPermissions.SettingsManage);
        AnaDocument<FunnelDTO> before = await _docs.GetAsync<FunnelDTO>(AnaCollections.Funnel, id.ToString(), ct).ConfigureAwait(false) ?? throw new NotFoundException("Funnel not found.");
        ValidateFunnel(funnel);
        funnel.Id = id;
        DateTimeOffset now = _clock.GetUtcNow();
        if (!await _docs.UpdateAsync(AnaCollections.Funnel, id.ToString(), before.Owner, funnel.Version, funnel, now, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        await _audit.AppendAsync([MembershipEvents.Audit(caller, "ana.funnel.update", $"ana_funnel:{id}", before.Value, funnel, now)], ct).ConfigureAwait(false);
        funnel.Version++;
        return funnel;
    }

    /// <summary>ลบ funnel</summary>
    public async Task DeleteFunnelAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.SettingsManage);
        if (!await _docs.DeleteAsync(AnaCollections.Funnel, id.ToString(), ct).ConfigureAwait(false))
        {
            throw new NotFoundException("Funnel not found.");
        }

        await _audit.AppendAsync([MembershipEvents.Audit(caller, "ana.funnel.delete", $"ana_funnel:{id}", null, null, _clock.GetUtcNow())], ct).ConfigureAwait(false);
    }
}
