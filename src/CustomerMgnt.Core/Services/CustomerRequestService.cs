using System.Globalization;
using System.Text;
using System.Text.Json;
using Common.Core.Domain;
using Common.Core.Exceptions;
using Common.Core.Security;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Integration;
using CustomerMgnt.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// บริหาร Customer Request / TOR / Change request / Incident และส่งต่อไปยัง
/// ProjectMgnt (สร้างโครงการ), RequirementMgnt (สร้าง requirement ทีละข้อ TOR) และ IssueTracking (incident พร้อม severity/SLA)
/// เนื้อหาที่ส่งต่อถูกลงนามด้วย ML-DSA-65 + ES256 (hybrid PQC) เพื่อพิสูจน์ภายหลังว่าไม่ถูกแก้ไข
/// </summary>
public sealed class CustomerRequestService
{
    // การเปลี่ยนสถานะที่อนุญาต (Forwarded ทำผ่าน ForwardAsync เท่านั้น)
    private static readonly Dictionary<RequestStatus, RequestStatus[]> Transitions = new()
    {
        [RequestStatus.New] = [RequestStatus.UnderReview, RequestStatus.Approved, RequestStatus.Rejected, RequestStatus.Closed],
        [RequestStatus.UnderReview] = [RequestStatus.Approved, RequestStatus.Rejected, RequestStatus.Closed],
        [RequestStatus.Approved] = [RequestStatus.UnderReview, RequestStatus.Closed],
        [RequestStatus.Forwarded] = [RequestStatus.Closed],
        [RequestStatus.Rejected] = [RequestStatus.UnderReview],
        [RequestStatus.Closed] = [],
    };

    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly ICustomerRequestDAO _requests;
    private readonly ICustomerDAO _customers;
    private readonly IExternalLinkDAO _links;
    private readonly SlaService _sla;
    private readonly IProjectMgntGateway _projects;
    private readonly IRequirementMgntGateway _requirements;
    private readonly IIssueTrackingGateway _issues;
    private readonly PqcSignatureService _signer;
    private readonly CustomerMgntOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CustomerRequestService> _logger;

    /// <summary>สร้างบริการ</summary>
    /// <param name="requests">DAO คำขอ</param>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="links">DAO ลิงก์</param>
    /// <param name="sla">บริการ SLA</param>
    /// <param name="projects">ProjectMgnt</param>
    /// <param name="requirements">RequirementMgnt</param>
    /// <param name="issues">IssueTracking</param>
    /// <param name="signer">ลายเซ็น PQC ของ Common</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="clock">นาฬิกา</param>
    /// <param name="logger">logger</param>
    public CustomerRequestService(ICustomerRequestDAO requests, ICustomerDAO customers, IExternalLinkDAO links, SlaService sla,
        IProjectMgntGateway projects, IRequirementMgntGateway requirements, IIssueTrackingGateway issues, PqcSignatureService signer,
        IOptions<CustomerMgntOptions> options, TimeProvider clock, ILogger<CustomerRequestService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _requests = requests;
        _customers = customers;
        _links = links;
        _sla = sla;
        _projects = projects;
        _requirements = requirements;
        _issues = issues;
        _signer = signer;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>บันทึกคำขอ/TOR ใหม่</summary>
    /// <param name="user">ผู้เรียก (request.write)</param>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอที่บันทึก</returns>
    /// <exception cref="ValidationException">Incident ไม่มี severity หรือ severity ไม่อยู่ใน SLA ของลูกค้า</exception>
    public async Task<CustomerRequestDTO> CreateAsync(UserContextDTO user, CreateCustomerRequestRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้า
        AccessGuard.Require(user, Permissions.RequestWrite);
        ArgumentNullException.ThrowIfNull(request);
        CustomerDTO customer = await _customers.GetAsync(request.CustomerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
        DateTimeOffset now = _clock.GetUtcNow();

        // Step 2: Incident ต้องมี severity ที่อยู่ใน SLA ของลูกค้ารายนี้ (ลูกค้าแต่ละรายมีชุด severity ของตัวเอง)
        string? severity = string.IsNullOrWhiteSpace(request.SeverityCode) ? null : request.SeverityCode.Trim().ToUpperInvariant();
        if (request.Kind == RequestKind.Incident)
        {
            ValidationException.ThrowIf(severity is null, "severityCode", "Incidents require a severity code.");
            (_, SlaPolicyDTO? policy) = await _sla.ResolvePolicyAsync(customer.Code, now, ct).ConfigureAwait(false);
            if (policy is not null)
            {
                _ = SlaService.CalculateTargets(customer.Code, policy, severity!, now); // โยน ValidationException พร้อมรายการที่ใช้ได้
            }
        }

        // Step 3: ข้อ TOR (เลขข้อไม่ซ้ำ)
        var items = new List<TorItemDTO>(request.Items.Count);
        var itemNos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TorItemRequest i in request.Items)
        {
            string no = InputGuard.Required(i.ItemNo, "items.itemNo", 30);
            ValidationException.ThrowIf(!itemNos.Add(no), "items.itemNo", $"Duplicate TOR item '{no}'.");
            string priority = i.Priority is "Low" or "Medium" or "High" or "Critical" ? i.Priority : "Medium";
            items.Add(new TorItemDTO { ItemNo = no, Text = InputGuard.Required(InputGuard.StripControlCharacters(i.Text), "items.text", 10_000), Priority = priority, Mandatory = i.Mandatory });
        }

        // Step 4: เลขที่คำขอ REQ-2026-00012
        int seq = await _requests.NextSequenceAsync(now.Year, ct).ConfigureAwait(false);
        var entity = new CustomerRequestDTO
        {
            RequestNo = string.Create(CultureInfo.InvariantCulture, $"{_options.RequestNoPrefix}-{now.Year}-{seq:D5}"),
            CustomerId = customer.Id,
            ContactPersonId = request.ContactPersonId,
            Kind = request.Kind,
            Title = InputGuard.Required(request.Title, "title", 300),
            Description = InputGuard.StripControlCharacters(request.Description),
            SeverityCode = severity,
            Budget = request.Budget,
            DesiredDate = request.DesiredDate,
            OwnerSubject = user.Subject,
            Status = RequestStatus.New,
            Items = items,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };

        // Step 5: บันทึก (คำขอ + ข้อ TOR ใน transaction เดียว)
        entity.Id = await _requests.InsertAsync(entity, ct).ConfigureAwait(false);
        return entity;
    }

    /// <summary>เปลี่ยนสถานะคำขอ (พิจารณา / อนุมัติ / ปฏิเสธ / ปิด)</summary>
    /// <param name="user">ผู้เรียก (request.write)</param>
    /// <param name="requestId">คำขอ</param>
    /// <param name="decision">สถานะใหม่ + version</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอหลังแก้ไข</returns>
    /// <exception cref="ValidationException">การเปลี่ยนสถานะไม่อนุญาต</exception>
    public async Task<CustomerRequestDTO> DecideAsync(UserContextDTO user, long requestId, RequestDecisionRequest decision, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + คำขอ + version
        AccessGuard.Require(user, Permissions.RequestWrite);
        ArgumentNullException.ThrowIfNull(decision);
        CustomerRequestDTO r = await _requests.GetAsync(requestId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Request not found.");
        if (r.Version != decision.Version)
        {
            throw new ConflictException("The request was changed by someone else. Reload and try again.");
        }

        // Step 2: ตรวจ state machine
        ValidationException.ThrowIf(!Transitions[r.Status].Contains(decision.Status), "status", $"Cannot change a request from {r.Status} to {decision.Status}.");

        // Step 3: บันทึก (หมายเหตุต่อท้ายรายละเอียดพร้อมผู้ตัดสิน เพื่อเป็นประวัติ)
        r.Status = decision.Status;
        if (!string.IsNullOrWhiteSpace(decision.Comment))
        {
            r.Description = $"{r.Description}\n\n[{decision.Status} โดย {user.UserName} {_clock.GetUtcNow():yyyy-MM-dd HH:mm}Z] {InputGuard.StripControlCharacters(decision.Comment)}".Trim();
        }

        r.UpdatedAt = _clock.GetUtcNow();
        if (!await _requests.UpdateAsync(r, ct).ConfigureAwait(false))
        {
            throw new ConflictException("The request was changed by someone else. Reload and try again.");
        }

        r.Version++;
        return r;
    }

    /// <summary>
    /// ส่งต่อคำขอที่อนุมัติแล้วไปยังระบบปลายทาง — idempotent: ระบบที่สร้างสำเร็จแล้ว (มี link) จะไม่ถูกสร้างซ้ำเมื่อเรียกอีกครั้ง
    /// </summary>
    /// <param name="user">ผู้เรียก (request.forward) — token ของผู้เรียกถูกส่งต่อไปยังระบบปลายทาง ทำให้สิทธิ์ของแต่ละระบบยังมีผล</param>
    /// <param name="requestId">คำขอ</param>
    /// <param name="options">ระบบปลายทางและค่าที่ต้องใช้</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลิงก์ที่สร้าง ลายเซ็น และคำเตือนของระบบที่ล้มเหลว</returns>
    public async Task<ForwardResultDTO> ForwardAsync(UserContextDTO user, long requestId, ForwardRequestOptions options, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + สถานะต้องอนุมัติแล้ว
        AccessGuard.Require(user, Permissions.RequestForward);
        ArgumentNullException.ThrowIfNull(options);
        CustomerRequestDTO r = await _requests.GetAsync(requestId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Request not found.");
        ValidationException.ThrowIf(r.Status is not (RequestStatus.Approved or RequestStatus.Forwarded), "status", "Only approved requests can be forwarded.");
        ValidationException.ThrowIf(!options.ToProjectMgnt && !options.ToRequirementMgnt && !options.ToIssueTracking, "targets", "Choose at least one target system.");
        CustomerDTO customer = (await _customers.GetAsync(r.CustomerId, ct).ConfigureAwait(false))!;

        // Step 2: ลงนามเนื้อหาแบบ canonical (PQC hybrid) — ระบบปลายทาง/ผู้ตรวจสอบยืนยันได้ด้วย public key ของเรา
        string signature = JsonSerializer.Serialize(_signer.Sign(CanonicalPayload(r, customer)));
        IReadOnlyList<ExternalLinkDTO> existing = await _links.ListByRequestAsync(r.Id, ct).ConfigureAwait(false);
        var created = new List<ExternalLinkDTO>();
        var warnings = new List<string>();
        DateOnly today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        string description = BuildDescription(r, customer);

        // Step 3: ProjectMgnt — สร้างโครงการ
        if (options.ToProjectMgnt && !existing.Any(l => l.System == ExternalSystem.ProjectMgnt))
        {
            await TryForwardAsync(ExternalSystem.ProjectMgnt, warnings, async () =>
            {
                string code = RequireProjectCode(options.ProjectCode);
                ValidationException.ThrowIf(options.ProjectManagerId is null, "projectManagerId", "ProjectManagerId is required for ProjectMgnt.");
                long id = await _projects.CreateProjectAsync(code, r.Title, description, options.Methodology, options.ProjectManagerId!.Value, r.DesiredDate ?? today, r.Budget ?? 0, ct).ConfigureAwait(false);
                created.Add(await LinkAsync(r, ExternalSystem.ProjectMgnt, "project", id.ToString(CultureInfo.InvariantCulture), r.Title, ct).ConfigureAwait(false));
            }).ConfigureAwait(false);
        }

        // Step 4: RequirementMgnt — สร้าง project แล้ว requirement ทีละข้อ (ข้อที่สร้างแล้วจะมี RequirementKey → ข้าม)
        if (options.ToRequirementMgnt)
        {
            await TryForwardAsync(ExternalSystem.RequirementMgnt, warnings, async () =>
            {
                long projectId;
                ExternalLinkDTO? projectLink = existing.Concat(created).FirstOrDefault(l => l.System == ExternalSystem.RequirementMgnt && l.EntityType == "project");
                if (projectLink is null)
                {
                    string key = RequireProjectCode(options.ProjectCode);
                    DateOnly start = today;
                    DateOnly end = r.DesiredDate is { } d && d > start ? d : start.AddMonths(6);
                    projectId = await _requirements.CreateProjectAsync(key, r.Title, description, options.RequirementStandard, start, end, r.Budget ?? 0, ct).ConfigureAwait(false);
                    created.Add(await LinkAsync(r, ExternalSystem.RequirementMgnt, "project", projectId.ToString(CultureInfo.InvariantCulture), r.Title, ct).ConfigureAwait(false));
                }
                else
                {
                    projectId = long.Parse(projectLink.ExternalId, CultureInfo.InvariantCulture);
                }

                long moduleId = options.RequirementModuleId ?? await _requirements.GetFirstModuleIdAsync(projectId, ct).ConfigureAwait(false)
                    ?? throw new ValidationException("The RequirementMgnt project has no module. Pass requirementModuleId.");
                IEnumerable<TorItemDTO> items = r.Items.Count > 0 ? r.Items : [new TorItemDTO { ItemNo = "1", Text = r.Description ?? r.Title, Priority = "Medium" }];
                foreach (TorItemDTO item in items.Where(i => i.RequirementKey is null))
                {
                    var attrs = new Dictionary<string, string> { ["source"] = "CustomerMgnt", ["customerCode"] = customer.Code, ["requestNo"] = r.RequestNo, ["torItem"] = item.ItemNo, ["mandatory"] = item.Mandatory ? "yes" : "no" };
                    string title = $"[{item.ItemNo}] {(item.Text.Length > 120 ? item.Text[..120] + "…" : item.Text)}";
                    (long artifactId, string artifactKey) = await _requirements.CreateRequirementAsync(moduleId, title, item.Text, item.Priority, attrs, ct).ConfigureAwait(false);
                    item.RequirementKey = artifactKey;
                    created.Add(await LinkAsync(r, ExternalSystem.RequirementMgnt, "artifact", artifactId.ToString(CultureInfo.InvariantCulture), artifactKey, ct).ConfigureAwait(false));
                }
            }).ConfigureAwait(false);
        }

        // Step 5: IssueTracking — issue พร้อมรหัสลูกค้าและ severity (IssueTracking คำนวณ SLA ผ่าน /api/sla/calculate ของเรา)
        if (options.ToIssueTracking && !existing.Any(l => l.System == ExternalSystem.IssueTracking && l.EntityType == "issue"))
        {
            await TryForwardAsync(ExternalSystem.IssueTracking, warnings, async () =>
            {
                long projectId = options.IssueTrackingProjectId
                    ?? (await _links.ListAsync(customer.Id, ExternalSystem.IssueTracking, ct).ConfigureAwait(false)).Where(l => l.EntityType == "project")
                        .Select(l => (long?)long.Parse(l.ExternalId, CultureInfo.InvariantCulture)).FirstOrDefault()
                    ?? throw new ValidationException("Link the customer to an IssueTracking project or pass issueTrackingProjectId.");
                string priority = "Normal";
                if (r.SeverityCode is not null && (await _sla.ResolvePolicyAsync(customer.Code, _clock.GetUtcNow(), ct).ConfigureAwait(false)).Policy is { } policy)
                {
                    priority = SlaService.CalculateTargets(customer.Code, policy, r.SeverityCode, _clock.GetUtcNow()).IssuePriority;
                }

                long issueId = await _issues.CreateIssueAsync(projectId, options.IssueTypeId, $"[{r.RequestNo}] {r.Title}", description, priority, customer.Code, r.SeverityCode, ct).ConfigureAwait(false);
                created.Add(await LinkAsync(r, ExternalSystem.IssueTracking, "issue", issueId.ToString(CultureInfo.InvariantCulture), r.Title, ct).ConfigureAwait(false));
            }).ConfigureAwait(false);
        }

        // Step 6: บันทึกสถานะ (สำเร็จอย่างน้อยหนึ่งระบบ → Forwarded)
        if (created.Count > 0 || existing.Count > 0)
        {
            r.Status = RequestStatus.Forwarded;
            r.ForwardSignature = signature;
            r.ForwardedAt ??= _clock.GetUtcNow();
            r.UpdatedAt = _clock.GetUtcNow();
            if (!await _requests.UpdateAsync(r, ct).ConfigureAwait(false))
            {
                warnings.Add("The request was changed concurrently; forward links were saved but the status was not updated.");
            }
        }

        return new ForwardResultDTO(r.Id, existing.Concat(created).ToList(), signature, warnings);
    }

    /// <summary>อ่านคำขอพร้อมข้อ TOR</summary>
    /// <param name="user">ผู้เรียก (request.read)</param>
    /// <param name="requestId">คำขอ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>คำขอ</returns>
    public async Task<CustomerRequestDTO> GetAsync(UserContextDTO user, long requestId, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.RequestRead);
        return await _requests.GetAsync(requestId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Request not found.");
    }

    /// <summary>รายการคำขอ</summary>
    /// <param name="user">ผู้เรียก (request.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="status">สถานะ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    public Task<IReadOnlyList<CustomerRequestDTO>> ListAsync(UserContextDTO user, long? customerId, RequestStatus? status, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.RequestRead);
        return _requests.ListAsync(customerId, status, 500, ct);
    }

    /// <summary>ตรวจลายเซ็นของคำขอที่ส่งต่อแล้ว (ยืนยันว่าเนื้อหาปัจจุบันตรงกับที่ส่งต่อ)</summary>
    /// <param name="user">ผู้เรียก (request.read)</param>
    /// <param name="requestId">คำขอ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>true ถ้าลายเซ็นถูกต้องและเนื้อหาไม่ถูกแก้</returns>
    public async Task<bool> VerifyForwardSignatureAsync(UserContextDTO user, long requestId, CancellationToken ct = default)
    {
        CustomerRequestDTO r = await GetAsync(user, requestId, ct).ConfigureAwait(false);
        if (r.ForwardSignature is null)
        {
            return false;
        }

        CustomerDTO customer = (await _customers.GetAsync(r.CustomerId, ct).ConfigureAwait(false))!;
        SignatureDTO? sig = JsonSerializer.Deserialize<SignatureDTO>(r.ForwardSignature);
        return sig is not null && _signer.Verify(CanonicalPayload(r, customer), sig);
    }

    /// <summary>เนื้อหาแบบ canonical สำหรับลงนาม (ลำดับ field คงที่, ไม่รวมสถานะ/ลายเซ็น/RequirementKey)</summary>
    /// <param name="r">คำขอ</param>
    /// <param name="customer">ลูกค้า</param>
    /// <returns>byte ของ JSON</returns>
    internal static byte[] CanonicalPayload(CustomerRequestDTO r, CustomerDTO customer) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            r.RequestNo,
            customer = customer.Code,
            kind = r.Kind.ToString(),
            r.Title,
            r.Description,
            r.SeverityCode,
            r.Budget,
            r.DesiredDate,
            items = r.Items.OrderBy(i => i.ItemNo, StringComparer.Ordinal).Select(i => new { i.ItemNo, i.Text, i.Priority, i.Mandatory }),
        }, CanonicalJson));

    /// <summary>คำอธิบายที่ส่งไประบบปลายทาง</summary>
    private static string BuildDescription(CustomerRequestDTO r, CustomerDTO customer)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"ลูกค้า: {customer.DisplayName} ({customer.Code})\nคำขอ: {r.RequestNo} ({r.Kind})\n");
        if (r.SeverityCode is not null) sb.Append(CultureInfo.InvariantCulture, $"Severity: {r.SeverityCode}\n");
        if (r.DesiredDate is not null) sb.Append(CultureInfo.InvariantCulture, $"วันที่ต้องการ: {r.DesiredDate:yyyy-MM-dd}\n");
        sb.Append('\n').Append(r.Description);
        if (r.Items.Count > 0)
        {
            sb.Append("\n\nTOR:\n");
            foreach (TorItemDTO i in r.Items)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- {i.ItemNo} [{i.Priority}{(i.Mandatory ? ", must" : string.Empty)}] {i.Text}\n");
            }
        }

        string text = sb.ToString();
        return text.Length > 4000 ? text[..3990] + "…" : text;
    }

    /// <summary>รหัสโครงการ A-Z0-9 2-10 ตัว ขึ้นต้นด้วยตัวอักษร (ตรงกับกฎของ ProjectMgnt/RequirementMgnt)</summary>
    private static string RequireProjectCode(string? code)
    {
        string c = (code ?? string.Empty).Trim().ToUpperInvariant();
        bool ok = c.Length is >= 2 and <= 10 && char.IsAsciiLetterUpper(c[0]) && c.All(ch => char.IsAsciiLetterUpper(ch) || char.IsAsciiDigit(ch));
        ValidationException.ThrowIf(!ok, "projectCode", "ProjectCode must be 2-10 characters A-Z/0-9 starting with a letter.");
        return c;
    }

    /// <summary>บันทึกลิงก์</summary>
    private async Task<ExternalLinkDTO> LinkAsync(CustomerRequestDTO r, ExternalSystem system, string entityType, string externalId, string? title, CancellationToken ct)
    {
        var link = new ExternalLinkDTO { CustomerId = r.CustomerId, System = system, EntityType = entityType, ExternalId = externalId, Title = title, CustomerRequestId = r.Id, CreatedAt = _clock.GetUtcNow() };
        link.Id = await _links.UpsertAsync(link, ct).ConfigureAwait(false);
        return link;
    }

    /// <summary>ส่งต่อไปหนึ่งระบบ; ระบบปลายทางล่ม/ปฏิเสธ → เก็บเป็นคำเตือน (ระบบอื่นยังทำต่อ) แต่ข้อมูลนำเข้าผิด → โยนต่อ</summary>
    private async Task TryForwardAsync(ExternalSystem system, List<string> warnings, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DependencyUnavailableException or ForbiddenException or ConflictException or NotFoundException)
        {
            _logger.LogWarning(ex, "Forwarding to {System} failed", system);
            warnings.Add($"{system}: {ex.Message}");
        }
    }
}
