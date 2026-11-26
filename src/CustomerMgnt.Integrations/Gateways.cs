using System.Globalization;
using System.Text.Json;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Integration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Integrations;

/// <summary>
/// IssueTracking (mrpichet/issuetracking): ค้นหา issue ของลูกค้า และสร้าง issue จากคำขอประเภท Incident
/// ใช้ field <c>customer</c>/<c>severity</c> ที่เพิ่มใน issuetracking (PR SLA) — ถ้าปลายทางยังเป็นเวอร์ชันเก่า จะค้นจาก project ที่ผูกไว้แทน
/// </summary>
public sealed class IssueTrackingGateway : GatewayBase, IIssueTrackingGateway
{
    /// <summary>ชื่อ HttpClient</summary>
    internal const string HttpClientName = "customermgnt.issuetracking";

    /// <summary>ขนาดหน้าสูงสุดของ issuetracking</summary>
    private const int PageSize = 500;

    private readonly int _maxIssues;

    /// <summary>สร้าง gateway</summary>
    /// <param name="factory">HttpClient factory</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="logger">logger</param>
    public IssueTrackingGateway(IHttpClientFactory factory, IOptions<IntegrationsOptions> options, ILogger<IssueTrackingGateway> logger)
        : base(factory, "IssueTracking", HttpClientName, options?.Value.IssueTracking ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _maxIssues = Math.Clamp(options.Value.MaxIssues, PageSize, 200_000);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExternalIssueDTO>> SearchIssuesAsync(string? customerCode, IReadOnlyCollection<long> projectIds, bool openOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        var result = new Dictionary<long, ExternalIssueDTO>();

        // Step 1: ค้นด้วยรหัสลูกค้า (field "customer" ของ issuetracking ที่รองรับ SLA)
        if (!string.IsNullOrWhiteSpace(customerCode))
        {
            try
            {
                await SearchAsync(new Condition("customer", "Equals", [customerCode]), openOnly, result, ct).ConfigureAwait(false);
            }
            catch (ValidationException ex)
            {
                // issuetracking เวอร์ชันเก่ายังไม่มี field customer → ใช้ project แทน
                Logger.LogWarning(ex, "IssueTracking does not support the 'customer' filter yet; falling back to linked projects");
            }
        }

        // Step 2: ค้นด้วย project ที่ผูกกับลูกค้า (ครั้งละ 100 ค่า ตามข้อจำกัดของ issuetracking)
        foreach (long[] chunk in projectIds.Distinct().Chunk(100))
        {
            await SearchAsync(new Condition("project", "Equals", chunk.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray()), openOnly, result, ct).ConfigureAwait(false);
        }

        return result.Values.OrderBy(i => i.CreatedAt).ToList();
    }

    /// <inheritdoc />
    public async Task<long> CreateIssueAsync(long projectId, int issueTypeId, string subject, string? description, string priority, string customerCode, string? severityCode, CancellationToken ct)
    {
        // Step 1: issuetracking คำนวณ due date จาก CustomerMgnt /api/sla/calculate เองเมื่อได้รับ customerCode + severityCode
        var body = new
        {
            projectId,
            issueTypeId,
            subject,
            description,
            priority,
            customerCode,
            severityCode,
            allowDuplicate = false,
        };
        IssueRow created = await SendAsync<IssueRow>(HttpMethod.Post, "api/issues", body, nullOnNotFound: false, ct).ConfigureAwait(false)
            ?? throw new DependencyUnavailableException("IssueTracking returned an empty response.");
        return created.Id;
    }

    /// <summary>ค้นหาทีละหน้าแล้วรวมผล (จำกัด MaxIssues)</summary>
    private async Task SearchAsync(Condition condition, bool openOnly, Dictionary<long, ExternalIssueDTO> into, CancellationToken ct)
    {
        List<Condition> conditions = [condition];
        if (openOnly)
        {
            conditions.Add(new Condition("status", "Open", []));
        }

        for (int page = 1; into.Count < _maxIssues; page++)
        {
            var criteria = new { conditions, sortBy = "created", sortDescending = false, page, pageSize = PageSize };
            PagedIssues? data = await SendAsync<PagedIssues>(HttpMethod.Post, "api/issues/search", criteria, nullOnNotFound: false, ct).ConfigureAwait(false);
            if (data?.Items is not { Count: > 0 } items)
            {
                break;
            }

            foreach (IssueRow i in items)
            {
                into[i.Id] = i.ToDto();
            }

            if (items.Count < PageSize)
            {
                break;
            }
        }
    }

    /// <summary>เงื่อนไขค้นหาของ issuetracking</summary>
    private sealed record Condition(string Field, string Operator, string[] Values);

    /// <summary>ผลค้นหาแบบแบ่งหน้า</summary>
    private sealed record PagedIssues(List<IssueRow> Items, long Total);

    /// <summary>IssueViewDTO ของ issuetracking (เฉพาะ field ที่ใช้)</summary>
    private sealed class IssueRow
    {
        public long Id { get; set; }

        public long ProjectId { get; set; }

        public string? ProjectKey { get; set; }

        public string Subject { get; set; } = string.Empty;

        public string? StatusName { get; set; }

        public bool IsClosed { get; set; }

        public string? Priority { get; set; }

        public string? AssigneeName { get; set; }

        public string? CustomerCode { get; set; }

        public string? SeverityCode { get; set; }

        public int? SeverityRank { get; set; }

        public DateTimeOffset? ResponseDueAt { get; set; }

        public DateTimeOffset? ResolutionDueAt { get; set; }

        public DateTimeOffset? FirstRespondedAt { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset? ClosedAt { get; set; }

        public ExternalIssueDTO ToDto() => new()
        {
            Id = Id,
            ProjectId = ProjectId,
            ProjectKey = ProjectKey,
            Subject = Subject,
            StatusName = StatusName,
            IsClosed = IsClosed,
            Priority = Priority,
            AssigneeName = AssigneeName,
            CustomerCode = CustomerCode,
            SeverityCode = SeverityCode,
            SeverityRank = SeverityRank,
            ResponseDueAt = ResponseDueAt,
            ResolutionDueAt = ResolutionDueAt,
            FirstRespondedAt = FirstRespondedAt,
            CreatedAt = CreatedAt,

            // issuetracking เวอร์ชันเก่าไม่มี closedAt → ใช้เวลาแก้ไขล่าสุดของ issue ที่ปิดแล้วแทน
            ClosedAt = ClosedAt ?? (IsClosed ? UpdatedAt : null),
        };
    }
}

/// <summary>ProjectMgnt (mrpichet/ProjectMgnt): อ่านโครงการ + KPI และสร้างโครงการจากคำขอ/TOR</summary>
public sealed class ProjectMgntGateway : GatewayBase, IProjectMgntGateway
{
    /// <summary>ชื่อ HttpClient</summary>
    internal const string HttpClientName = "customermgnt.projectmgnt";

    /// <summary>จำนวนคำขอพร้อมกันสูงสุด (ไม่ถล่มปลายทาง)</summary>
    private const int MaxConcurrency = 8;

    /// <summary>สร้าง gateway</summary>
    /// <param name="factory">HttpClient factory</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="logger">logger</param>
    public ProjectMgntGateway(IHttpClientFactory factory, IOptions<IntegrationsOptions> options, ILogger<ProjectMgntGateway> logger)
        : base(factory, "ProjectMgnt", HttpClientName, options?.Value.ProjectMgnt ?? throw new ArgumentNullException(nameof(options)), logger) { }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExternalProjectDTO>> GetProjectsAsync(IReadOnlyCollection<long> projectIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        long[] ids = projectIds.Distinct().ToArray();
        var results = new ExternalProjectDTO?[ids.Length];

        // Step 1: อ่านโครงการ + KPI แบบขนาน (จำกัดพร้อมกัน 8)
        await Parallel.ForEachAsync(Enumerable.Range(0, ids.Length), new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct }, async (i, token) =>
        {
            ProjectRow? p = await SendAsync<ProjectRow>(HttpMethod.Get, $"api/projects/{ids[i].ToString(CultureInfo.InvariantCulture)}", null, nullOnNotFound: true, token).ConfigureAwait(false);
            if (p is null) return; // ถูกลบหรือไม่มีสิทธิ์เห็น → ข้าม

            // KPI เป็นข้อมูลเสริม — ล้มเหลวได้โดยไม่ทำให้ทั้งรายการล้ม
            KpiRow? k = null;
            try
            {
                k = await SendAsync<KpiRow>(HttpMethod.Get, $"api/projects/{p.Id.ToString(CultureInfo.InvariantCulture)}/kpis", null, nullOnNotFound: true, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DependencyUnavailableException or ForbiddenException or ValidationException)
            {
                Logger.LogWarning(ex, "KPIs for project {ProjectId} unavailable", p.Id);
            }

            results[i] = new ExternalProjectDTO
            {
                Id = p.Id,
                Code = p.Code,
                Name = p.Name,
                Phase = p.Phase,
                ActualProgress = k?.ActualPercent,
                PlannedProgress = k?.PlannedPercent,
                Health = k?.Health,
                StartDate = p.StartDate,
                ForecastEndDate = k?.ForecastGoLive ?? p.ForecastGoLive ?? p.BaselineGoLive,
            };
        }).ConfigureAwait(false);

        return results.Where(r => r is not null).Select(r => r!).ToList();
    }

    /// <inheritdoc />
    public async Task<long> CreateProjectAsync(string code, string name, string? description, string methodology, long projectManagerId, DateOnly startDate, decimal budget, CancellationToken ct)
    {
        var body = new { code, name, description, methodology, projectManagerId, startDate, budget, currency = "THB" };
        ProjectRow created = await SendAsync<ProjectRow>(HttpMethod.Post, "api/projects", body, nullOnNotFound: false, ct).ConfigureAwait(false)
            ?? throw new DependencyUnavailableException("ProjectMgnt returned an empty response.");
        return created.Id;
    }

    /// <summary>ProjectDTO ของ ProjectMgnt</summary>
    private sealed record ProjectRow(long Id, string? Code, string Name, string? Phase, DateOnly? StartDate, DateOnly? BaselineGoLive, DateOnly? ForecastGoLive);

    /// <summary>ProjectKpiDTO ของ ProjectMgnt</summary>
    private sealed record KpiRow(double PlannedPercent, double ActualPercent, DateOnly? ForecastGoLive, string? Health);
}

/// <summary>RequirementMgnt (mrpichet/requirementMgnt): สร้างโครงการ requirement และ artifact จากรายการ TOR</summary>
public sealed class RequirementMgntGateway : GatewayBase, IRequirementMgntGateway
{
    /// <summary>ชื่อ HttpClient</summary>
    internal const string HttpClientName = "customermgnt.requirementmgnt";

    /// <summary>สร้าง gateway</summary>
    /// <param name="factory">HttpClient factory</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="logger">logger</param>
    public RequirementMgntGateway(IHttpClientFactory factory, IOptions<IntegrationsOptions> options, ILogger<RequirementMgntGateway> logger)
        : base(factory, "RequirementMgnt", HttpClientName, options?.Value.RequirementMgnt ?? throw new ArgumentNullException(nameof(options)), logger) { }

    /// <inheritdoc />
    public async Task<long> CreateProjectAsync(string key, string name, string? description, string standard, DateOnly startDate, DateOnly targetEndDate, decimal budget, CancellationToken ct)
    {
        // Step 1: ApplyTemplate = true → RequirementMgnt สร้าง module/workflow ตามมาตรฐานให้
        var body = new { key, name, description, standard, processStandard = standard, startDate, targetEndDate, budget, currency = "THB", applyTemplate = true };
        IdKey created = await SendAsync<IdKey>(HttpMethod.Post, "api/projects", body, nullOnNotFound: false, ct).ConfigureAwait(false)
            ?? throw new DependencyUnavailableException("RequirementMgnt returned an empty response.");
        return created.Id;
    }

    /// <inheritdoc />
    public async Task<long?> GetFirstModuleIdAsync(long projectId, CancellationToken ct)
    {
        List<ModuleRow>? modules = await SendAsync<List<ModuleRow>>(HttpMethod.Get, $"api/projects/{projectId.ToString(CultureInfo.InvariantCulture)}/modules", null, nullOnNotFound: true, ct).ConfigureAwait(false);
        return modules?.OrderBy(m => m.ParentModuleId is null ? 0 : 1).ThenBy(m => m.SortOrder).ThenBy(m => m.Id).Select(m => (long?)m.Id).FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<(long Id, string Key)> CreateRequirementAsync(long moduleId, string title, string content, string priority, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        // Step 1: รายการ TOR = Stakeholder Requirement (Markdown)
        var body = new
        {
            moduleId,
            streamId = (long?)null,
            type = "StakeholderRequirement",
            title,
            content,
            format = "Markdown",
            priority = NormalizePriority(priority),
            attributes,
        };
        IdKey created = await SendAsync<IdKey>(HttpMethod.Post, "api/artifacts", body, nullOnNotFound: false, ct).ConfigureAwait(false)
            ?? throw new DependencyUnavailableException("RequirementMgnt returned an empty response.");
        return (created.Id, created.Key ?? created.Id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>map ความสำคัญเป็น enum Priority ของ RequirementMgnt (Low/Medium/High/Critical)</summary>
    /// <param name="priority">ค่าจาก TOR</param>
    /// <returns>ชื่อ enum</returns>
    internal static string NormalizePriority(string? priority) => priority?.Trim().ToUpperInvariant() switch
    {
        "LOW" or "COULD" or "WONT" => "Low",
        "HIGH" or "SHOULD" => "High",
        "CRITICAL" or "MUST" or "URGENT" => "Critical",
        _ => "Medium",
    };

    /// <summary>ผลสร้าง (id + key)</summary>
    private sealed record IdKey(long Id, string? Key);

    /// <summary>ModuleDTO</summary>
    private sealed record ModuleRow(long Id, long? ParentModuleId, string Name, int SortOrder);
}

/// <summary>MessageHub (mrpichet/messagehub): ประชุม/นัดหมาย, ส่งข้อความหาลูกค้าในช่องทางต่าง ๆ และแจ้งเตือนพนักงาน</summary>
public sealed class MessageHubGateway : GatewayBase, IMessageHubGateway
{
    /// <summary>ชื่อ HttpClient</summary>
    internal const string HttpClientName = "customermgnt.messagehub";

    /// <summary>สร้าง gateway</summary>
    /// <param name="factory">HttpClient factory</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="logger">logger</param>
    public MessageHubGateway(IHttpClientFactory factory, IOptions<IntegrationsOptions> options, ILogger<MessageHubGateway> logger)
        : base(factory, "MessageHub", HttpClientName, options?.Value.MessageHub ?? throw new ArgumentNullException(nameof(options)), logger) { }

    /// <inheritdoc />
    public async Task<Guid> CreateMeetingAsync(string title, string? description, Guid organizerId, DateTimeOffset startUtc, DateTimeOffset endUtc, IReadOnlyList<Guid> attendees, IReadOnlyList<string> agenda, int? reminderMinutes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attendees);
        ArgumentNullException.ThrowIfNull(agenda);

        // Step 1: สร้างการประชุม (ผู้จัดเป็นผู้เข้าร่วมเสมอ)
        var body = new
        {
            title,
            description,
            organizerId,
            startUtc = startUtc.ToUniversalTime(),
            endUtc = endUtc.ToUniversalTime(),
            attendees = attendees.Append(organizerId).Distinct().Select(id => new { userId = id, isRequired = true }).ToList(),
            agenda = agenda.Select(a => new { title = a, presentMinutes = 10 }).ToList(),
        };
        MeetingRow meeting = await SendAsync<MeetingRow>(HttpMethod.Post, "api/meetings", body, nullOnNotFound: false, ct).ConfigureAwait(false)
            ?? throw new DependencyUnavailableException("MessageHub returned an empty response.");

        // Step 2: ส่งคำเชิญ (ลงนามดิจิทัล) และตั้งเตือน — ล้มเหลวไม่ทำให้การประชุมหาย
        string id = meeting.Id.ToString("D");
        await TryAsync("invitations", () => SendAsync<object>(HttpMethod.Post, $"api/meetings/{id}/invitations", new { sign = true }, nullOnNotFound: false, ct)).ConfigureAwait(false);
        if (reminderMinutes is > 0)
        {
            await TryAsync("reminders", () => SendAsync<object>(HttpMethod.Post, $"api/meetings/{id}/reminders", new { attendeeAlertMinutes = new[] { reminderMinutes.Value } }, nullOnNotFound: false, ct)).ConfigureAwait(false);
        }

        return meeting.Id;
    }

    /// <inheritdoc />
    public async Task<ExternalMeetingDTO?> GetMeetingAsync(Guid meetingId, CancellationToken ct)
    {
        MeetingRow? m = await SendAsync<MeetingRow>(HttpMethod.Get, $"api/meetings/{meetingId:D}", null, nullOnNotFound: true, ct).ConfigureAwait(false);
        return m is null ? null : new ExternalMeetingDTO
        {
            Id = m.Id,
            Title = m.Title,
            StartUtc = m.StartUtc,
            EndUtc = m.EndUtc,
            Status = m.Status,
            Agenda = (m.Agenda ?? []).Select(a => new ExternalAgendaItemDTO { Id = a.Id, Title = a.Title, Status = a.Status, Conclusion = a.Conclusion, OwnerUserId = a.OwnerUserId }).ToList(),
        };
    }

    /// <inheritdoc />
    public async Task<bool> SendToCustomerAsync(ChannelKind channel, string recipient, string? subject, string body, CancellationToken ct)
    {
        // Step 1: ช่องทางที่ MessageHub ส่งได้
        string? type = MapChannel(channel);
        if (type is null)
        {
            return false;
        }

        // Step 2: ส่งแบบเข้าคิว (MessageHub retry/ส่งซ้ำให้เอง)
        await SendAsync<object>(HttpMethod.Post, "api/messages", new { channel = type, recipient, subject, body, queue = true }, nullOnNotFound: false, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SendToCustomerAsync(ChannelKind channel, string recipient, string? subject, string body, string? reference, CancellationToken ct)
    {
        string? type = MapChannel(channel);
        if (type is null)
        {
            return false;
        }

        await SendAsync<object>(HttpMethod.Post, "api/messages", new { channel = type, recipient, subject, body, reference, queue = true }, nullOnNotFound: false, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SendEmailAsync(string to, string subject, string body, IReadOnlyList<EmailAttachmentDTO> attachments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var files = attachments.Select(a => new { fileName = a.FileName, contentType = a.ContentType, contentBase64 = Convert.ToBase64String(a.Content) }).ToList();
        await SendAsync<object>(HttpMethod.Post, "api/messages", new { channel = "Email", recipient = to, subject, body, attachments = files, queue = true }, nullOnNotFound: false, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task NotifyUserAsync(Guid userId, string subject, string body, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, "api/messages/to-user", new { userId, subject, body }, nullOnNotFound: false, ct);

    /// <summary>ChannelKind ของ CustomerMgnt → ChannelType ของ MessageHub (null = ส่งไม่ได้ เช่น WebChat/Phone/Review)</summary>
    /// <param name="channel">ช่องทาง</param>
    /// <returns>ชื่อ ChannelType</returns>
    internal static string? MapChannel(ChannelKind channel) => channel switch
    {
        ChannelKind.Line => "Line",
        ChannelKind.Email => "Email",
        ChannelKind.Facebook => "FacebookMessenger",
        ChannelKind.Sms => "Sms",
        ChannelKind.Teams => "TeamsChat",
        ChannelKind.WhatsApp => "WhatsApp",
        ChannelKind.Telegram => "Telegram",
        _ => null,
    };

    /// <summary>ทำงานเสริม ล้มเหลวแค่ log</summary>
    private async Task TryAsync(string step, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DependencyUnavailableException or ValidationException or ForbiddenException or ConflictException)
        {
            Logger.LogWarning(ex, "MessageHub {Step} failed", step);
        }
    }

    /// <summary>MeetingDTO ของ MessageHub</summary>
    private sealed record MeetingRow(Guid Id, string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Status, List<AgendaRow>? Agenda);

    /// <summary>AgendaItemDTO ของ MessageHub</summary>
    private sealed record AgendaRow(Guid Id, string Title, string? Status, string? Conclusion, Guid? OwnerUserId);
}
