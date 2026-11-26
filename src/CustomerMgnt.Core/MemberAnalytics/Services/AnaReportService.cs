using System.Globalization;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Integration;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Membership.Services;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

/// <summary>ข้อมูลของงานส่งออก analytics ที่เก็บใน <c>filters_json</c> ของงานส่งออกระบบสมาชิก</summary>
public sealed class AnaExportMeta
{
    public AnaExportRequest Request { get; set; } = new();
    public string Sub { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];
    public List<string> Permissions { get; set; } = [];
    public Guid? ScheduleId { get; set; }
}

/// <summary>ตัวกรองที่บันทึก รายงาน (มาตรฐาน/สร้างเอง) ตั้งเวลาส่ง และส่งออกไฟล์ (§35)</summary>
public sealed partial class AnaReportService
{
    public const string ExportPrefix = "ana:";
    private const int MaxSavedFiltersPerPage = 50;
    private readonly IAnaDocumentDAO _docs;
    private readonly IAnaReadModelDAO _model;
    private readonly IMembershipJobDAO _jobs;
    private readonly IMembershipAuditDAO _audit;
    private readonly AnaFilterResolver _filters;
    private readonly AnalyticsQueryService _query;
    private readonly AnaSettingsProvider _settings;
    private readonly IMessageHubGateway _hub;
    private readonly TimeProvider _clock;
    private readonly ILogger<AnaReportService> _log;

    /// <summary>สร้าง</summary>
    public AnaReportService(IAnaDocumentDAO docs, IAnaReadModelDAO model, IMembershipJobDAO jobs, IMembershipAuditDAO audit, AnaFilterResolver filters, AnalyticsQueryService query,
        AnaSettingsProvider settings, IMessageHubGateway hub, TimeProvider clock, ILogger<AnaReportService> log)
    {
        _docs = docs;
        _model = model;
        _jobs = jobs;
        _audit = audit;
        _filters = filters;
        _query = query;
        _settings = settings;
        _hub = hub;
        _clock = clock;
        _log = log;
    }

    // ================================================================== ตัวกรองที่บันทึก

    /// <summary>ตัวกรองที่บันทึกของตัวเอง (กรองตามหน้าจอ)</summary>
    public async Task<IReadOnlyList<SavedFilterDTO>> ListSavedFiltersAsync(MembershipCaller caller, string? page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        AnaPermissions.RequireAny(caller);
        return [.. (await _docs.ListAsync<SavedFilterDTO>(AnaCollections.SavedFilter, caller.User.Subject, ct).ConfigureAwait(false))
            .Select(d => { d.Value.Version = d.Version; return d.Value; })
            .Where(f => string.IsNullOrWhiteSpace(page) || string.Equals(f.Page, page, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Page, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>บันทึกตัวกรองใหม่</summary>
    public async Task<SavedFilterDTO> CreateSavedFilterAsync(MembershipCaller caller, SavedFilterDTO filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(filter);
        AnaPermissions.RequireAny(caller);
        IReadOnlyList<SavedFilterDTO> mine = await ListSavedFiltersAsync(caller, filter.Page, ct).ConfigureAwait(false);
        if (mine.Count >= MaxSavedFiltersPerPage)
        {
            throw AnaErrors.Bad("too_many_saved_filters", $"At most {MaxSavedFiltersPerPage} saved filters per page.");
        }

        filter.Id = Guid.NewGuid();
        filter.OwnerSub = caller.User.Subject;
        filter.Name = filter.Name.Trim();
        await ClearDefaultAsync(caller, filter, mine, ct).ConfigureAwait(false);
        await _docs.InsertAsync(AnaCollections.SavedFilter, filter.Id.ToString(), caller.User.Subject, filter, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        filter.Version = 1;
        return filter;
    }

    /// <summary>แก้ตัวกรอง (เจ้าของเท่านั้น)</summary>
    public async Task<SavedFilterDTO> UpdateSavedFilterAsync(MembershipCaller caller, Guid id, SavedFilterDTO filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(filter);
        AnaDocument<SavedFilterDTO> doc = await OwnFilterAsync(caller, id, ct).ConfigureAwait(false);
        filter.Id = id;
        filter.OwnerSub = caller.User.Subject;
        filter.Name = filter.Name.Trim();
        await ClearDefaultAsync(caller, filter, await ListSavedFiltersAsync(caller, filter.Page, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        doc = await OwnFilterAsync(caller, id, ct).ConfigureAwait(false);
        if (filter.Version != doc.Version
            || !await _docs.UpdateAsync(AnaCollections.SavedFilter, id.ToString(), caller.User.Subject, filter.Version, filter, _clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        filter.Version++;
        return filter;
    }

    /// <summary>ลบตัวกรอง</summary>
    public async Task DeleteSavedFilterAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        _ = await OwnFilterAsync(caller, id, ct).ConfigureAwait(false);
        await _docs.DeleteAsync(AnaCollections.SavedFilter, id.ToString(), ct).ConfigureAwait(false);
    }

    private async Task<AnaDocument<SavedFilterDTO>> OwnFilterAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        AnaPermissions.RequireAny(caller);
        AnaDocument<SavedFilterDTO>? doc = await _docs.GetAsync<SavedFilterDTO>(AnaCollections.SavedFilter, id.ToString(), ct).ConfigureAwait(false);
        return doc is not null && string.Equals(doc.Owner, caller.User.Subject, StringComparison.Ordinal) ? doc : throw new NotFoundException("Saved filter not found.");
    }

    /// <summary>ตั้งเป็นค่าเริ่มต้น = ยกเลิกค่าเริ่มต้นเดิมของหน้าเดียวกัน</summary>
    private async Task ClearDefaultAsync(MembershipCaller caller, SavedFilterDTO filter, IReadOnlyList<SavedFilterDTO> mine, CancellationToken ct)
    {
        if (!filter.IsDefault)
        {
            return;
        }

        foreach (SavedFilterDTO f in mine.Where(f => f.IsDefault && f.Id != filter.Id))
        {
            f.IsDefault = false;
            await _docs.UpsertAsync(AnaCollections.SavedFilter, f.Id.ToString(), caller.User.Subject, f, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        }
    }

    // ================================================================== Catalog

    /// <summary>metric ที่ใช้ในรายงานสร้างเอง: (รหัส, ป้าย, หน่วย, ชนิด, สิทธิ์ที่ต้องมี, คำนิยาม)</summary>
    internal static readonly (string Code, string Label, MetricUnit Unit, MetricKind Kind, string Requires, string Definition)[] Metrics =
    [
        ("sales.net", "ยอดขายสุทธิ", MetricUnit.Baht, MetricKind.Fact, AnaPermissions.DashboardRead, "Subtotal − ส่วนลด − ยอดคืน ของธุรกรรมที่นับ"),
        ("sales.gross", "ยอดก่อนส่วนลด", MetricUnit.Baht, MetricKind.Fact, AnaPermissions.DashboardRead, "Σ Subtotal"),
        ("sales.discount", "ส่วนลดรวม", MetricUnit.Baht, MetricKind.Fact, AnaPermissions.DashboardRead, "Σ DiscountTotal"),
        ("sales.returns", "ยอดคืน", MetricUnit.Baht, MetricKind.Fact, AnaPermissions.DashboardRead, "ยอดคืนหักที่วันของธุรกรรมเดิม"),
        ("sales.bills", "จำนวนธุรกรรม", MetricUnit.Count, MetricKind.Fact, AnaPermissions.DashboardRead, "จำนวนบิลที่นับ"),
        ("sales.avgTicket", "ยอดเฉลี่ยต่อธุรกรรม", MetricUnit.Baht, MetricKind.Computed, AnaPermissions.DashboardRead, "ยอดขายสุทธิ ÷ จำนวนธุรกรรม"),
        ("sales.items", "จำนวนชิ้น", MetricUnit.Count, MetricKind.Fact, AnaPermissions.DashboardRead, "Σ (Qty − ReturnedQty)"),
        ("sales.itemsPerTicket", "ชิ้นต่อธุรกรรม", MetricUnit.Ratio, MetricKind.Computed, AnaPermissions.DashboardRead, "จำนวนชิ้น ÷ จำนวนธุรกรรม"),
        ("member.buyers", "สมาชิกที่ซื้อ", MetricUnit.Count, MetricKind.Fact, AnaPermissions.MemberRead, "จำนวนสมาชิกที่มีธุรกรรม ≥ 1"),
        ("member.repeatRate", "อัตราซื้อซ้ำ", MetricUnit.Percent, MetricKind.Computed, AnaPermissions.MemberRead, "สมาชิกที่มีธุรกรรม ≥ 2 ÷ สมาชิกที่ซื้อ"),
        ("member.salesPerMember", "ยอดเฉลี่ยต่อสมาชิก", MetricUnit.Baht, MetricKind.Computed, AnaPermissions.MemberRead, "ยอดขายสุทธิ ÷ สมาชิกที่ซื้อ"),
        ("member.new", "สมาชิกใหม่", MetricUnit.Count, MetricKind.Fact, AnaPermissions.MemberRead, "สมาชิกที่สมัครในช่วง (ใช้ได้กับมิติเวลา Tier และช่องทางสมัคร)"),
        ("points.earned", "คะแนนที่ได้รับ", MetricUnit.Points, MetricKind.Fact, AnaPermissions.DashboardRead, "Σ คะแนนที่ได้จากธุรกรรม (หักที่ย้อน)"),
        ("promo.sales", "ยอดขายที่ใช้สิทธิ์แคมเปญ", MetricUnit.Baht, MetricKind.Fact, AnaPermissions.PromotionRead, "ยอดขายสุทธิของบิลที่มีสิทธิ์แคมเปญ"),
        ("promo.discount", "ส่วนลดจากแคมเปญ", MetricUnit.Baht, MetricKind.Fact, AnaPermissions.PromotionRead, "Σ ส่วนลดใน BenefitsJson"),
    ];

    internal static readonly (string Code, string Label)[] Dimensions =
    [
        ("day", "วัน"), ("week", "สัปดาห์"), ("month", "เดือน"), ("branch", "สาขา"), ("channel", "ช่องทางขาย"), ("tier", "Tier"),
        ("registeredChannel", "ช่องทางสมัคร"), ("brand", "แบรนด์"), ("category", "หมวดสินค้า"), ("campaign", "แคมเปญ"),
    ];

    /// <summary>metric และ dimension ที่ผู้เรียกใช้ได้</summary>
    public MetricCatalogDTO Catalog(MembershipCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        AnaPermissions.RequireAny(caller);
        return new MetricCatalogDTO
        {
            Metrics = [.. Metrics.Where(m => caller.Has(m.Requires)).Select(m => new CatalogMetricDTO
            {
                Code = m.Code, Label = m.Label, Unit = m.Unit, Kind = m.Kind, Requires = m.Requires, Definition = m.Definition,
                Dimensions = m.Code == "member.new" ? ["day", "week", "month", "tier", "registeredChannel"] : [.. Dimensions.Select(d => d.Code)],
                Drilldown = m.Code switch { "sales.net" => "sales.net", "member.buyers" => "member.buyers", "member.repeatRate" => "member.repeat", "member.new" => "member.new", _ => null },
            })],
            Dimensions = [.. Dimensions.Select(d => new CatalogDimensionDTO { Code = d.Code, Label = d.Label })],
        };
    }

    // ================================================================== รายงาน

    /// <summary>รายงานมาตรฐาน (ในโค้ด แก้ไม่ได้ ทำสำเนาได้)</summary>
    internal static IReadOnlyList<ReportDefinitionDTO> StandardReports { get; } =
    [
        new() { Id = Guid.Parse("a0000000-0000-0000-0000-000000000001"), Name = "ยอดขายสมาชิกรายเดือนตามสาขา", Standard = true, OwnerSub = "system",
            Dimensions = ["month", "branch"], Metrics = ["sales.net", "sales.bills", "sales.avgTicket", "member.buyers"], Sort = [new() { Key = "month" }] },
        new() { Id = Guid.Parse("a0000000-0000-0000-0000-000000000002"), Name = "ยอดขายตาม Tier", Standard = true, OwnerSub = "system",
            Dimensions = ["tier"], Metrics = ["sales.net", "member.buyers", "member.salesPerMember", "member.repeatRate"], Sort = [new() { Key = "sales.net", Desc = true }] },
        new() { Id = Guid.Parse("a0000000-0000-0000-0000-000000000003"), Name = "ผลแคมเปญ", Standard = true, OwnerSub = "system",
            Dimensions = ["campaign"], Metrics = ["promo.sales", "promo.discount", "sales.bills", "member.buyers"], Sort = [new() { Key = "promo.sales", Desc = true }] },
        new() { Id = Guid.Parse("a0000000-0000-0000-0000-000000000004"), Name = "สมาชิกใหม่ตามช่องทางสมัคร", Standard = true, OwnerSub = "system",
            Dimensions = ["month", "registeredChannel"], Metrics = ["member.new"], Sort = [new() { Key = "month" }] },
    ];

    private static bool CanOpen(MembershipCaller caller, ReportDefinitionDTO r) =>
        r.Standard || string.Equals(r.OwnerSub, caller.User.Subject, StringComparison.Ordinal)
        || r.Shares.Any(s => (s.SubjectType == ScopeSubjectType.User && string.Equals(s.Subject, caller.User.Subject, StringComparison.Ordinal))
            || (s.SubjectType == ScopeSubjectType.Role && caller.User.Roles.Contains(s.Subject, StringComparer.OrdinalIgnoreCase)));

    private async Task<List<ReportDefinitionDTO>> AllReportsAsync(CancellationToken ct) =>
        [.. StandardReports, .. (await _docs.ListAsync<ReportDefinitionDTO>(AnaCollections.Report, null, ct).ConfigureAwait(false))
            .Select(d => { d.Value.Version = d.Version; d.Value.UpdatedAt = d.UpdatedAt; return d.Value; })];

    /// <summary>AN-18 รายงานที่เปิดได้</summary>
    public async Task<IReadOnlyList<ReportSummaryDTO>> ListReportsAsync(MembershipCaller caller, bool? mine, bool? standard, string? text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        AnaPermissions.RequireAny(caller);
        return [.. (await AllReportsAsync(ct).ConfigureAwait(false)).Where(r => CanOpen(caller, r))
            .Select(r => new ReportSummaryDTO
            {
                Id = r.Id, Name = r.Name, Description = r.Description, Standard = r.Standard, OwnerSub = r.OwnerSub, UpdatedAt = r.UpdatedAt,
                Mine = string.Equals(r.OwnerSub, caller.User.Subject, StringComparison.Ordinal), Shared = !r.Standard && !string.Equals(r.OwnerSub, caller.User.Subject, StringComparison.Ordinal),
            })
            .Where(r => mine is null || r.Mine == mine)
            .Where(r => standard is null || r.Standard == standard)
            .Where(r => string.IsNullOrWhiteSpace(text) || r.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Standard).ThenBy(r => r.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>อ่านนิยามรายงาน</summary>
    public async Task<ReportDefinitionDTO> GetReportAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        AnaPermissions.RequireAny(caller);
        ReportDefinitionDTO r = (await AllReportsAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == id) ?? throw new NotFoundException("Report not found.");
        return CanOpen(caller, r) ? r : throw new NotFoundException("Report not found.");
    }

    private static void Validate(ReportDefinitionDTO r)
    {
        r.Dimensions = [.. r.Dimensions.Distinct(StringComparer.Ordinal)];
        r.Metrics = [.. r.Metrics.Distinct(StringComparer.Ordinal)];
        if (r.Dimensions.Count > 3 || r.Metrics.Count > 10)
        {
            throw AnaErrors.Bad("report_too_complex", "A report may have at most 3 dimensions and 10 metrics.");
        }

        ValidationException.ThrowIf(r.Metrics.Count == 0, "metrics", "Choose at least one metric.");
        foreach (string d in r.Dimensions)
        {
            ValidationException.ThrowIf(!Dimensions.Any(x => x.Code == d), "dimensions", $"Unknown dimension '{d}'.");
        }

        foreach (string m in r.Metrics)
        {
            ValidationException.ThrowIf(!Metrics.Any(x => x.Code == m), "metrics", $"Unknown metric '{m}'.");
        }

        ValidationException.ThrowIf(r.Dimensions.Count(d => d is "day" or "week" or "month") > 1, "dimensions", "Choose at most one time dimension.");
        r.Name = r.Name.Trim();
    }

    /// <summary>สร้างรายงาน</summary>
    public async Task<ReportDefinitionDTO> CreateReportAsync(MembershipCaller caller, ReportDefinitionDTO report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(report);
        caller.Require(AnaPermissions.ReportBuild);
        Validate(report);
        report.Id = Guid.NewGuid();
        report.Standard = false;
        report.OwnerSub = caller.User.Subject;
        report.Shares = caller.Has(AnaPermissions.ReportShare) ? report.Shares : [];
        report.UpdatedAt = _clock.GetUtcNow();
        await _docs.InsertAsync(AnaCollections.Report, report.Id.ToString(), report.OwnerSub, report, report.UpdatedAt, ct).ConfigureAwait(false);
        report.Version = 1;
        return report;
    }

    /// <summary>แก้รายงาน (เจ้าของ; รายงานมาตรฐาน = 409 standard_report)</summary>
    public async Task<ReportDefinitionDTO> UpdateReportAsync(MembershipCaller caller, Guid id, ReportDefinitionDTO report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(report);
        caller.Require(AnaPermissions.ReportBuild);
        ReportDefinitionDTO before = await OwnReportAsync(caller, id, ct).ConfigureAwait(false);
        Validate(report);
        report.Id = id;
        report.Standard = false;
        report.OwnerSub = before.OwnerSub;
        report.Shares = before.Shares;
        report.UpdatedAt = _clock.GetUtcNow();
        if (!await _docs.UpdateAsync(AnaCollections.Report, id.ToString(), report.OwnerSub, report.Version, report, report.UpdatedAt, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        report.Version++;
        return report;
    }

    private async Task<ReportDefinitionDTO> OwnReportAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        if (StandardReports.Any(r => r.Id == id))
        {
            throw MembershipException.Conflict("standard_report", "Standard reports cannot be changed. Make a copy instead.");
        }

        AnaDocument<ReportDefinitionDTO>? doc = await _docs.GetAsync<ReportDefinitionDTO>(AnaCollections.Report, id.ToString(), ct).ConfigureAwait(false);
        if (doc is null || !string.Equals(doc.Owner, caller.User.Subject, StringComparison.Ordinal))
        {
            throw new NotFoundException("Report not found.");
        }

        doc.Value.Version = doc.Version;
        return doc.Value;
    }

    /// <summary>ทำสำเนารายงาน</summary>
    public async Task<ReportDefinitionDTO> CopyReportAsync(MembershipCaller caller, Guid id, ReportCopyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(AnaPermissions.ReportBuild);
        ReportDefinitionDTO src = await GetReportAsync(caller, id, ct).ConfigureAwait(false);
        return await CreateReportAsync(caller, new ReportDefinitionDTO
        {
            Name = request.Name, Description = src.Description, Dimensions = [.. src.Dimensions], Metrics = [.. src.Metrics], Filter = src.Filter.Clone(),
            Sort = [.. src.Sort.Select(s => new ReportSortDTO { Key = s.Key, Desc = s.Desc })], Display = src.Display,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>ลบรายงาน (ลบตั้งเวลาของรายงานด้วย)</summary>
    public async Task DeleteReportAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        AnaPermissions.RequireAny(caller);
        _ = await OwnReportAsync(caller, id, ct).ConfigureAwait(false);
        await _docs.DeleteAsync(AnaCollections.Report, id.ToString(), ct).ConfigureAwait(false);
        foreach (AnaDocument<AnaScheduleDoc> s in (await _docs.ListAsync<AnaScheduleDoc>(AnaCollections.Schedule, null, ct).ConfigureAwait(false)).Where(s => s.Value.Schedule.ReportId == id))
        {
            await _docs.DeleteAsync(AnaCollections.Schedule, s.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>แชร์รายงาน (แทนที่ทั้งชุด)</summary>
    public async Task<IReadOnlyList<ReportShareDTO>> ShareReportAsync(MembershipCaller caller, Guid id, IReadOnlyList<ReportShareDTO> shares, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(shares);
        caller.Require(AnaPermissions.ReportShare);
        ReportDefinitionDTO r = await OwnReportAsync(caller, id, ct).ConfigureAwait(false);
        r.Shares = [.. shares.Where(s => !string.IsNullOrWhiteSpace(s.Subject)).Select(s => new ReportShareDTO { SubjectType = s.SubjectType, Subject = s.Subject.Trim() })
            .DistinctBy(s => (s.SubjectType, s.Subject))];
        r.UpdatedAt = _clock.GetUtcNow();
        if (!await _docs.UpdateAsync(AnaCollections.Report, id.ToString(), r.OwnerSub, r.Version, r, r.UpdatedAt, ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        await _audit.AppendAsync([MembershipEvents.Audit(caller, "ana.report.share", $"ana_report:{id}", null, r.Shares, r.UpdatedAt)], ct).ConfigureAwait(false);
        return r.Shares;
    }

    /// <summary>AN-19 เปิดรายงาน (ตัวกรองที่ส่งทับค่าในรายงาน; ตัดตามขอบเขตของผู้เปิด)</summary>
    public async Task<AnalyticsResultDTO<ReportResultDTO>> RunReportAsync(MembershipCaller caller, Guid id, AnalyticsFilterDTO? filter, int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidationException.ThrowIf(pageSize is < 1 or > 1000, "pageSize", "pageSize must be between 1 and 1000.");
        ReportDefinitionDTO r = await GetReportAsync(caller, id, ct).ConfigureAwait(false);
        return await _query.RunDefinitionAsync(caller, r, Merge(r.Filter, filter), Math.Max(page, 1), pageSize, ct).ConfigureAwait(false);
    }

    private static AnalyticsFilterDTO Merge(AnalyticsFilterDTO saved, AnalyticsFilterDTO? given)
    {
        AnalyticsFilterDTO f = saved.Clone();
        if (given is null)
        {
            return f;
        }

        f.From = given.From ?? f.From;
        f.To = given.To ?? f.To;
        f.Compare = given.Compare;
        f.Granularity = given.Granularity ?? f.Granularity;
        if (given.Branch.Count > 0) { f.Branch = [.. given.Branch]; }
        if (given.Channel.Count > 0) { f.Channel = [.. given.Channel]; }
        if (given.Brand.Count > 0) { f.Brand = [.. given.Brand]; }
        if (given.Category.Count > 0) { f.Category = [.. given.Category]; }
        if (given.Tier.Count > 0) { f.Tier = [.. given.Tier]; }
        if (given.RegisteredChannel.Count > 0) { f.RegisteredChannel = [.. given.RegisteredChannel]; }
        if (given.SourceCampaign.Count > 0) { f.SourceCampaign = [.. given.SourceCampaign]; }
        if (given.Campaign.Count > 0) { f.Campaign = [.. given.Campaign]; }
        return f;
    }
}
