using System.Globalization;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Integration;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Membership.Services;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

/// <summary>เอกสารตั้งเวลา: ค่าที่ผู้ใช้ตั้ง + สิทธิ์ของเจ้าของ ณ เวลาบันทึก (ใช้รันรายงานตามขอบเขตของเจ้าของ)</summary>
public sealed class AnaScheduleDoc
{
    public ReportScheduleDTO Schedule { get; set; } = new();
    public AnaExportMeta Owner { get; set; } = new();
}

// ตั้งเวลาส่งรายงาน (AN-21) และส่งออกไฟล์ (AN-22)
public sealed partial class AnaReportService
{
    private const int MaxScheduleFailures = 3;

    // ================================================================== ตั้งเวลา

    /// <summary>ตั้งเวลาของตัวเอง</summary>
    public async Task<IReadOnlyList<ReportScheduleDTO>> ListSchedulesAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.ReportBuild);
        return [.. (await _docs.ListAsync<AnaScheduleDoc>(AnaCollections.Schedule, caller.User.Subject, ct).ConfigureAwait(false)).Select(WithVersion)];
    }

    private static ReportScheduleDTO WithVersion(AnaDocument<AnaScheduleDoc> d)
    {
        d.Value.Schedule.Version = d.Version;
        return d.Value.Schedule;
    }

    private async Task ValidateScheduleAsync(MembershipCaller caller, ReportScheduleDTO s, CancellationToken ct)
    {
        _ = await GetReportAsync(caller, s.ReportId, ct).ConfigureAwait(false);
        ValidationException.ThrowIf(!CronSchedule.TryParse(s.Cron, out _), "cron", "Invalid cron expression (5 fields).");
        ValidationException.ThrowIf(!TryZone(s.Timezone, out _), "timezone", "Unknown time zone.");
        ValidationException.ThrowIf(s.Recipients.Count is 0 or > 50, "recipients", "Add 1 to 50 recipients.");
        AnalyticsSettingsDTO settings = await _settings.GetAsync(ct).ConfigureAwait(false);
        foreach (string r in s.Recipients.Concat(s.FailureRecipients))
        {
            ValidationException.ThrowIf(!System.Net.Mail.MailAddress.TryCreate(r, out System.Net.Mail.MailAddress? addr), "recipients", $"Invalid e-mail '{r}'.");
            if (settings.AllowRecipientDomains.Count > 0 && !settings.AllowRecipientDomains.Contains(addr!.Host, StringComparer.OrdinalIgnoreCase))
            {
                throw AnaErrors.Bad("recipient_not_allowed", $"The domain '{addr.Host}' is not in the allowed list.");
            }
        }
    }

    private static bool TryZone(string id, out TimeZoneInfo zone)
    {
        if (string.Equals(id, "Asia/Bangkok", StringComparison.OrdinalIgnoreCase))
        {
            zone = AnaTime.Zone;
            return true;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = AnaTime.Zone;
            return false;
        }
    }

    private DateTimeOffset? NextRun(ReportScheduleDTO s) =>
        s.Enabled && CronSchedule.TryParse(s.Cron, out CronSchedule? cron) && TryZone(s.Timezone, out TimeZoneInfo zone) ? cron!.Next(_clock.GetUtcNow(), zone) : null;

    private static AnaExportMeta OwnerOf(MembershipCaller caller) => new()
    {
        Sub = caller.User.Subject, UserName = caller.User.UserName, Roles = [.. caller.User.Roles], Permissions = [.. caller.User.Permissions],
    };

    /// <summary>สร้างตั้งเวลา</summary>
    public async Task<ReportScheduleDTO> CreateScheduleAsync(MembershipCaller caller, ReportScheduleDTO schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(schedule);
        caller.Require(AnaPermissions.ReportBuild);
        await ValidateScheduleAsync(caller, schedule, ct).ConfigureAwait(false);
        schedule.Id = Guid.NewGuid();
        schedule.OwnerSub = caller.User.Subject;
        schedule.ConsecutiveFailures = 0;
        schedule.LastRun = null;
        schedule.NextRunAt = NextRun(schedule);
        await _docs.InsertAsync(AnaCollections.Schedule, schedule.Id.ToString(), schedule.OwnerSub, new AnaScheduleDoc { Schedule = schedule, Owner = OwnerOf(caller) }, _clock.GetUtcNow(), ct)
            .ConfigureAwait(false);
        schedule.Version = 1;
        return schedule;
    }

    /// <summary>แก้ตั้งเวลา (เปิดใหม่ = ล้างจำนวนครั้งที่ล้มเหลว)</summary>
    public async Task<ReportScheduleDTO> UpdateScheduleAsync(MembershipCaller caller, Guid id, ReportScheduleDTO schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(schedule);
        caller.Require(AnaPermissions.ReportBuild);
        AnaDocument<AnaScheduleDoc> doc = await OwnScheduleAsync(caller, id, ct).ConfigureAwait(false);
        await ValidateScheduleAsync(caller, schedule, ct).ConfigureAwait(false);
        schedule.Id = id;
        schedule.OwnerSub = doc.Value.Schedule.OwnerSub;
        schedule.LastRun = doc.Value.Schedule.LastRun;
        schedule.ConsecutiveFailures = schedule.Enabled && !doc.Value.Schedule.Enabled ? 0 : doc.Value.Schedule.ConsecutiveFailures;
        schedule.NextRunAt = NextRun(schedule);
        if (!await _docs.UpdateAsync(AnaCollections.Schedule, id.ToString(), schedule.OwnerSub, schedule.Version, new AnaScheduleDoc { Schedule = schedule, Owner = OwnerOf(caller) },
                _clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        schedule.Version++;
        return schedule;
    }

    /// <summary>ลบตั้งเวลา</summary>
    public async Task DeleteScheduleAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        _ = await OwnScheduleAsync(caller, id, ct).ConfigureAwait(false);
        await _docs.DeleteAsync(AnaCollections.Schedule, id.ToString(), ct).ConfigureAwait(false);
    }

    private async Task<AnaDocument<AnaScheduleDoc>> OwnScheduleAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        AnaPermissions.RequireAny(caller);
        AnaDocument<AnaScheduleDoc>? doc = await _docs.GetAsync<AnaScheduleDoc>(AnaCollections.Schedule, id.ToString(), ct).ConfigureAwait(false);
        return doc is not null && string.Equals(doc.Owner, caller.User.Subject, StringComparison.Ordinal) ? doc : throw new NotFoundException("Schedule not found.");
    }

    /// <summary>ส่งทันที (ไม่เปลี่ยนรอบถัดไป)</summary>
    public async Task<AnaExportJobDTO> RunScheduleNowAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        AnaDocument<AnaScheduleDoc> doc = await OwnScheduleAsync(caller, id, ct).ConfigureAwait(false);
        return await RunScheduleAsync(doc, advance: false, ct).ConfigureAwait(false);
    }

    /// <summary>งานเบื้องหลัง: ส่งรายงานที่ถึงเวลา</summary>
    public async Task<int> RunDueSchedulesAsync(CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        int n = 0;
        foreach (AnaDocument<AnaScheduleDoc> doc in (await _docs.ListAsync<AnaScheduleDoc>(AnaCollections.Schedule, null, ct).ConfigureAwait(false))
            .Where(d => d.Value.Schedule.Enabled && d.Value.Schedule.NextRunAt <= now))
        {
            await RunScheduleAsync(doc, advance: true, ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    /// <summary>ช่วงวันของรายงานตามเวลา</summary>
    public static (DateOnly From, DateOnly To) RangeOf(ScheduleRange range, DateOnly today)
    {
        DateOnly yesterday = today.AddDays(-1);
        DateOnly monday = AnaTime.Bucket(today, Granularity.Week);
        DateOnly month = new(today.Year, today.Month, 1);
        return range switch
        {
            ScheduleRange.PreviousWeek => (monday.AddDays(-7), monday.AddDays(-1)),
            ScheduleRange.PreviousMonth => (month.AddMonths(-1), month.AddDays(-1)),
            ScheduleRange.MonthToDate => today.Day == 1 ? (month.AddMonths(-1), month.AddDays(-1)) : (month, yesterday),
            ScheduleRange.Last7Days => (yesterday.AddDays(-6), yesterday),
            _ => (yesterday.AddDays(-29), yesterday),
        };
    }

    private async Task<AnaExportJobDTO> RunScheduleAsync(AnaDocument<AnaScheduleDoc> doc, bool advance, CancellationToken ct)
    {
        ReportScheduleDTO s = doc.Value.Schedule;
        (DateOnly from, DateOnly to) = RangeOf(s.Range, AnaTime.Today(_clock));
        AnaExportMeta meta = doc.Value.Owner;
        meta.ScheduleId = s.Id;
        meta.Request = new AnaExportRequest { Source = ExportSource.Report, ReportId = s.ReportId, Format = s.Format, Filter = new AnalyticsFilterDTO { From = from, To = to, Compare = CompareMode.None } };
        ExportJobDTO job = await QueueAsync(meta, ct).ConfigureAwait(false);
        await ProcessAsync(job, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        bool ok = job.Status == JobStatus.Done;
        string? error = job.Error;
        if (ok)
        {
            ExportJobDTO? full = await _jobs.GetExportAsync(job.Id, withContent: true, ct).ConfigureAwait(false);
            string file = FileName(job);
            foreach (string to2 in s.Recipients)
            {
                ok &= await SafeSendAsync(to2, $"รายงานตามเวลา {from:yyyy-MM-dd} – {to:yyyy-MM-dd}",
                    $"รายงานตามเวลาที่ตั้งไว้ ช่วง {from:yyyy-MM-dd} ถึง {to:yyyy-MM-dd} ({job.RowCount} แถว)",
                    full?.Content is byte[] content ? [new EmailAttachmentDTO(file, ContentType(job.Format), content)] : [], ct).ConfigureAwait(false);
            }

            error = ok ? null : "Sending e-mail failed.";
        }

        s.LastRun = new ScheduleRunDTO { At = now, Status = ok ? JobStatus.Done : JobStatus.Failed, Error = error };
        s.ConsecutiveFailures = ok ? 0 : s.ConsecutiveFailures + 1;
        if (s.ConsecutiveFailures >= MaxScheduleFailures)
        {
            s.Enabled = false;
            foreach (string to3 in s.FailureRecipients)
            {
                await SafeSendAsync(to3, "ปิดการส่งรายงานตามเวลา", $"การส่งรายงานตามเวลาล้มเหลว {MaxScheduleFailures} ครั้งติดกัน ระบบปิดการส่งแล้ว กรุณาตรวจสอบและเปิดใหม่", [], ct)
                    .ConfigureAwait(false);
            }
        }

        if (advance || !s.Enabled)
        {
            s.NextRunAt = NextRun(s);
        }

        await _docs.UpsertAsync(AnaCollections.Schedule, s.Id.ToString(), s.OwnerSub, doc.Value, now, ct).ConfigureAwait(false);
        return ToDto(job);
    }

    private async Task<bool> SafeSendAsync(string to, string subject, string body, IReadOnlyList<EmailAttachmentDTO> attachments, CancellationToken ct)
    {
        try
        {
            return await _hub.SendEmailAsync(to, subject, body, attachments, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            LogSendFailed(_log, ex);
            return false;
        }
    }

    [LoggerMessage(EventId = 4310, Level = LogLevel.Warning, Message = "Analytics e-mail send failed")]
    private static partial void LogSendFailed(ILogger logger, Exception ex);

    // ================================================================== ส่งออก

    /// <summary>ขอส่งออกไฟล์</summary>
    public async Task<AnaExportJobDTO> RequestExportAsync(MembershipCaller caller, AnaExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(AnaPermissions.Export);
        if (request.Unmasked)
        {
            // drill-down และ read model เก็บเฉพาะค่าที่ mask แล้ว
            throw AnaErrors.Bad("unmasked_not_supported", "Unmasked export is not available. Exports always contain masked personal data.");
        }

        switch (request.Source)
        {
            case ExportSource.Report:
                ValidationException.ThrowIf(request.ReportId is null, "reportId", "reportId is required.");
                _ = await GetReportAsync(caller, request.ReportId!.Value, ct).ConfigureAwait(false);
                break;
            case ExportSource.Dashboard:
                ValidationException.ThrowIf(request.Page is not ("AN-01" or "AN-02" or "AN-03"), "page", "page must be AN-01, AN-02 or AN-03.");
                break;
            case ExportSource.Drilldown:
                ValidationException.ThrowIf(string.IsNullOrWhiteSpace(request.Metric), "metric", "metric is required.");
                if ((request.Level ?? DrillLevel.Member) == DrillLevel.Member)
                {
                    caller.Require(AnaPermissions.MemberDetail);
                }

                break;
            default:
                throw AnaErrors.Bad("invalid_source", "Unknown export source.");
        }

        // ตรวจตัวกรอง (ช่วงวันเกิน = 400) ก่อนเข้าคิว
        _ = await _filters.ResolveAsync(caller, request.Filter, ct).ConfigureAwait(false);
        AnaExportMeta meta = OwnerOf(caller);
        meta.Request = request;
        ExportJobDTO job = await QueueAsync(meta, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "ana.export.request", $"export:{job.Id}", null, new { request.Source, request.ReportId, request.Page, request.Metric }, _clock.GetUtcNow())], ct)
            .ConfigureAwait(false);
        return ToDto(job);
    }

    private async Task<ExportJobDTO> QueueAsync(AnaExportMeta meta, CancellationToken ct)
    {
        var job = new ExportJobDTO
        {
            Report = ExportPrefix + meta.Request.Source.ToString().ToLowerInvariant(), Format = meta.Request.Format, FiltersJson = MembershipJson.Write(meta), Status = JobStatus.Queued,
            RequestedBy = $"user:{meta.Sub}", RequestedAt = _clock.GetUtcNow(),
        };
        job.Id = await _jobs.InsertExportAsync(job, ct).ConfigureAwait(false);
        return job;
    }

    /// <summary>งานส่งออก (ของตน; audit.read + mine=false เห็นทุกคน)</summary>
    public async Task<IReadOnlyList<AnaExportJobDTO>> ListExportsAsync(MembershipCaller caller, bool mine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.Export);
        string? by = !mine && caller.Has(MembershipPermissions.AuditRead) ? null : caller.Actor;
        return [.. (await _jobs.ListExportsAsync(by, null, ct).ConfigureAwait(false)).Where(ReportService.IsAnalyticsExport).OrderByDescending(j => j.RequestedAt).Select(ToDto)];
    }

    /// <summary>ดาวน์โหลดไฟล์ (เจ้าของงาน)</summary>
    public async Task<(string FileName, string ContentType, byte[] Content)> DownloadAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.Export);
        ExportJobDTO? job = await _jobs.GetExportAsync(id, withContent: true, ct).ConfigureAwait(false);
        if (job is null || !ReportService.IsAnalyticsExport(job) || !string.Equals(job.RequestedBy, caller.Actor, StringComparison.Ordinal))
        {
            throw new NotFoundException("Export not found.");
        }

        if (job.Status != JobStatus.Done || job.Content is null || job.ExpiresAt <= _clock.GetUtcNow())
        {
            throw MembershipException.Gone("export_expired", "The file is not available.");
        }

        await _audit.AppendAsync([MembershipEvents.Audit(caller, "ana.export.download", $"export:{job.Id}", null, new { job.Report, job.RowCount }, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        return (FileName(job), ContentType(job.Format), job.Content);
    }

    private static string FileName(ExportJobDTO job) =>
        $"analytics-{job.Report[ExportPrefix.Length..]}-{job.RequestedAt.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}.{(job.Format == "Xlsx" ? "xlsx" : "csv")}";

    private static string ContentType(string format) => format == "Xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv; charset=utf-8";

    private static AnaExportJobDTO ToDto(ExportJobDTO j)
    {
        AnaExportMeta meta = MembershipJson.Read<AnaExportMeta>(j.FiltersJson);
        return new AnaExportJobDTO
        {
            Id = j.Id, Source = meta.Request.Source, Format = j.Format, Request = meta.Request, Status = j.Status, RowCount = j.RowCount, Masked = true, RequestedBy = j.RequestedBy,
            RequestedAt = j.RequestedAt, ExpiresAt = j.ExpiresAt, ScheduleId = meta.ScheduleId, Error = j.Error,
        };
    }

    /// <summary>งานเบื้องหลัง: สร้างไฟล์ที่รอคิว</summary>
    public async Task<int> RunExportsAsync(CancellationToken ct)
    {
        int n = 0;
        foreach (ExportJobDTO job in (await _jobs.ListExportsAsync(null, JobStatus.Queued, ct).ConfigureAwait(false)).Where(ReportService.IsAnalyticsExport))
        {
            await ProcessAsync(job, ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    private async Task ProcessAsync(ExportJobDTO job, CancellationToken ct)
    {
        job.Status = JobStatus.Running;
        await _jobs.UpdateExportAsync(job, ct).ConfigureAwait(false);
        AnalyticsSettingsDTO s = await _settings.GetAsync(ct).ConfigureAwait(false);
        try
        {
            AnaExportMeta meta = MembershipJson.Read<AnaExportMeta>(job.FiltersJson);
            var caller = new MembershipCaller(new UserContextDTO(meta.Sub, meta.UserName, meta.Roles, meta.Permissions, null), null, null);
            ReportDTO table = await BuildTableAsync(caller, meta.Request, s.ExportMaxRows, ct).ConfigureAwait(false);
            if (table.Rows.Count > s.ExportMaxRows)
            {
                throw AnaErrors.Bad("too_many_rows", $"The export exceeds {s.ExportMaxRows} rows. Narrow the filter.");
            }

            job.Content = job.Format == "Xlsx" ? ReportService.Xlsx(table) : ReportService.Csv(table);
            job.RowCount = table.Rows.Count;
            job.Status = JobStatus.Done;
            job.ExpiresAt = _clock.GetUtcNow().AddHours(s.ExportRetentionHours);
        }
        catch (Exception ex) when (ex is CommonException or MembershipException or InvalidOperationException or FormatException)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
        }

        await _jobs.UpdateExportAsync(job, ct).ConfigureAwait(false);
    }

    private async Task<ReportDTO> BuildTableAsync(MembershipCaller caller, AnaExportRequest r, int maxRows, CancellationToken ct)
    {
        static string Cell(object? v) => v switch
        {
            null => string.Empty,
            DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? string.Empty,
        };
        switch (r.Source)
        {
            case ExportSource.Report:
            {
                ReportDefinitionDTO def = await GetReportAsync(caller, r.ReportId!.Value, ct).ConfigureAwait(false);
                ReportResultDTO res = (await _query.RunDefinitionAsync(caller, def, Merge(def.Filter, r.Filter), 1, maxRows + 1, ct).ConfigureAwait(false)).Data;
                return new ReportDTO
                {
                    Title = def.Name, Columns = [.. res.Columns.Select(c => c.Label)], Rows = [.. res.Rows.Select(row => res.Columns.Select(c => Cell(row.GetValueOrDefault(c.Key))).ToList())],
                    Totals = res.Totals.Where(t => t.Value is not null).ToDictionary(t => t.Key, t => t.Value!.Value),
                };
            }

            case ExportSource.Dashboard:
                return await _query.DashboardTableAsync(caller, r.Page!, r.Filter, ct).ConfigureAwait(false);
            default:
            {
                (_, List<ColumnDTO> cols, List<Dictionary<string, object?>> rows, _) =
                    await _query.DrillRowsAsync(caller, r.Filter, r.Metric!, r.Level ?? DrillLevel.Member, null, maxRows + 1, ct).ConfigureAwait(false);
                return new ReportDTO { Title = r.Metric!, Columns = [.. cols.Select(c => c.Label)], Rows = [.. rows.Select(row => cols.Select(c => Cell(row.GetValueOrDefault(c.Key))).ToList())] };
            }
        }
    }
}
