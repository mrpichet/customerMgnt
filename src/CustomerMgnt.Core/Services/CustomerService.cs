using System.Globalization;
using Common.Core.Domain;
using Common.Core.Exceptions;
using Common.Core.Security;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// ลงทะเบียนและดูแลข้อมูลลูกค้า (บุคคล/นิติบุคคล) รวมถึงตัวตนในช่องทางต่างๆ และมุมมองลูกค้า 360 องศา
/// </summary>
public sealed class CustomerService
{
    private readonly ICustomerDAO _customers;
    private readonly IRelationshipDAO _relationships;
    private readonly IConversationDAO _conversations;
    private readonly IActivityDAO _activities;
    private readonly ICustomerRequestDAO _requests;
    private readonly IOpportunityDAO _opportunities;
    private readonly IExternalLinkDAO _links;
    private readonly SlaService _sla;
    private readonly PrivacyService _privacy;
    private readonly CustomerMgntOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>สร้างบริการ</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="relationships">DAO ความสัมพันธ์</param>
    /// <param name="conversations">DAO แชต</param>
    /// <param name="activities">DAO กิจกรรม</param>
    /// <param name="requests">DAO คำขอ</param>
    /// <param name="opportunities">DAO โอกาสการขาย</param>
    /// <param name="links">DAO ลิงก์ระบบอื่น</param>
    /// <param name="sla">บริการ SLA</param>
    /// <param name="privacy">บริการปกป้องข้อมูลส่วนบุคคล</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="clock">นาฬิกา</param>
    public CustomerService(ICustomerDAO customers, IRelationshipDAO relationships, IConversationDAO conversations, IActivityDAO activities,
        ICustomerRequestDAO requests, IOpportunityDAO opportunities, IExternalLinkDAO links, SlaService sla, PrivacyService privacy,
        IOptions<CustomerMgntOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _customers = customers;
        _relationships = relationships;
        _conversations = conversations;
        _activities = activities;
        _requests = requests;
        _opportunities = opportunities;
        _links = links;
        _sla = sla;
        _privacy = privacy;
        _options = options.Value;
        _clock = clock;
    }

    /// <summary>ลงทะเบียนลูกค้าใหม่ (บุคคล หรือ นิติบุคคล)</summary>
    /// <param name="user">ผู้เรียก (customer.write)</param>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้าที่บันทึกแล้ว</returns>
    /// <exception cref="ValidationException">ข้อมูลไม่ครบ/ผิดรูปแบบ</exception>
    /// <exception cref="ConflictException">รหัสลูกค้าหรือเลขผู้เสียภาษีซ้ำ</exception>
    public async Task<CustomerDTO> RegisterAsync(UserContextDTO user, RegisterCustomerRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์
        AccessGuard.Require(user, Permissions.CustomerWrite);
        ArgumentNullException.ThrowIfNull(request);

        // Step 2: ตรวจข้อมูลตามประเภทลูกค้า
        ValidationException.ThrowIf(!Enum.IsDefined(request.Kind), "kind", "Kind must be Person or Juristic.");
        string displayName;
        if (request.Kind == CustomerKind.Person)
        {
            string first = InputGuard.Required(request.FirstName, "firstName", 100);
            string last = InputGuard.Required(request.LastName, "lastName", 100);
            displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? $"{first} {last}" : InputGuard.Required(request.DisplayName, "displayName", 300);
        }
        else
        {
            string legal = InputGuard.Required(request.LegalName, "legalName", 300);
            displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? legal : InputGuard.Required(request.DisplayName, "displayName", 300);
        }

        ValidateWebsite(request.Website);
        _ = BusinessCalendar.ResolveZone(request.TimeZoneId);

        // Step 3: เลขผู้เสียภาษี → normalize, blind index ตรวจซ้ำ, เข้ารหัส PQC
        string? taxEnvelope = null, taxHash = null, taxMasked = null;
        if (!string.IsNullOrWhiteSpace(request.TaxId))
        {
            string normalized = PrivacyService.NormalizeTaxId(request.TaxId);
            taxHash = _privacy.BlindIndex(normalized);
            if (await _customers.FindByTaxIdHashAsync(taxHash, ct).ConfigureAwait(false) is { } existing)
            {
                throw new ConflictException($"A customer with this tax id already exists ({existing.Code}).");
            }

            taxEnvelope = _privacy.Encrypt(normalized);
            taxMasked = PrivacyService.Mask(normalized);
        }

        // Step 4: รหัสลูกค้า (ผู้ใช้กำหนด หรือสร้างหลัง insert จาก Id)
        string? code = string.IsNullOrWhiteSpace(request.Code) ? null : InputGuard.RequireIdentifier(request.Code.Trim().ToUpperInvariant(), "code", 30);
        DateTimeOffset now = _clock.GetUtcNow();
        var customer = new CustomerDTO
        {
            Code = code ?? $"TMP-{Guid.NewGuid():N}"[..30],
            Kind = request.Kind,
            DisplayName = displayName,
            LegalName = request.Kind == CustomerKind.Juristic ? request.LegalName!.Trim() : null,
            FirstName = request.Kind == CustomerKind.Person ? request.FirstName!.Trim() : null,
            LastName = request.Kind == CustomerKind.Person ? request.LastName!.Trim() : null,
            TaxIdEnvelope = taxEnvelope,
            TaxIdHash = taxHash,
            TaxIdMasked = taxMasked,
            Email = request.Email?.Trim().ToLowerInvariant(),
            Phone = InputGuard.StripControlCharacters(request.Phone)?.Trim(),
            Industry = InputGuard.StripControlCharacters(request.Industry),
            EmployeeCount = request.EmployeeCount,
            AnnualRevenue = request.AnnualRevenue,
            Website = request.Website,
            Address = InputGuard.StripControlCharacters(request.Address),
            Province = InputGuard.StripControlCharacters(request.Province),
            Country = (request.Country ?? "TH").ToUpperInvariant(),
            TimeZoneId = request.TimeZoneId,
            Status = request.Status,
            OwnerSubject = string.IsNullOrWhiteSpace(request.OwnerSubject) ? user.Subject : request.OwnerSubject,
            Tags = InputGuard.StripControlCharacters(request.Tags),
            MarketingConsent = request.MarketingConsent,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };

        // Step 5: บันทึก (unique ของ code/tax hash ถูกตรวจซ้ำที่ DB → ConflictException จาก AdoBase)
        customer.Id = await _customers.InsertAsync(customer, ct).ConfigureAwait(false);

        // Step 6: ถ้าระบบเป็นผู้สร้างรหัส → ตั้งเป็น CUST-000123 จาก Id
        if (code is null)
        {
            customer.Code = _options.CustomerCodePrefix + customer.Id.ToString("D6", CultureInfo.InvariantCulture);
            if (!await _customers.UpdateAsync(customer, ct).ConfigureAwait(false))
            {
                throw new ConflictException("The customer was changed concurrently.");
            }

            customer.Version++;
        }

        // Step 7: ผูกอีเมลเป็นตัวตนในช่องทาง Email อัตโนมัติ (จับคู่อีเมลขาเข้ากับลูกค้าได้ทันที)
        if (customer.Email is not null && await _relationships.FindIdentityAsync(ChannelKind.Email, customer.Email, ct).ConfigureAwait(false) is null)
        {
            await _relationships.InsertIdentityAsync(new ChannelIdentityDTO { CustomerId = customer.Id, Channel = ChannelKind.Email, ExternalId = customer.Email, DisplayName = customer.DisplayName, Verified = true }, ct).ConfigureAwait(false);
        }

        return customer;
    }

    /// <summary>แก้ไขข้อมูลลูกค้า (เฉพาะ field ที่ส่งมา, ตรวจ version)</summary>
    /// <param name="user">ผู้เรียก (customer.write)</param>
    /// <param name="id">รหัสลูกค้า</param>
    /// <param name="request">ข้อมูลใหม่</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้าหลังแก้ไข</returns>
    /// <exception cref="ConflictException">version ไม่ตรง</exception>
    public async Task<CustomerDTO> UpdateAsync(UserContextDTO user, long id, UpdateCustomerRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + อ่านข้อมูลเดิม
        AccessGuard.Require(user, Permissions.CustomerWrite);
        ArgumentNullException.ThrowIfNull(request);
        CustomerDTO c = await _customers.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
        if (c.Version != request.Version)
        {
            throw new ConflictException("The customer was changed by someone else. Reload and try again.");
        }

        // Step 2: ใช้เฉพาะค่าที่ส่งมา
        if (request.DisplayName is not null) c.DisplayName = InputGuard.Required(request.DisplayName, "displayName", 300);
        if (request.Email is not null) c.Email = request.Email.Trim().ToLowerInvariant();
        if (request.Phone is not null) c.Phone = InputGuard.StripControlCharacters(request.Phone).Trim();
        if (request.Industry is not null) c.Industry = InputGuard.StripControlCharacters(request.Industry);
        if (request.EmployeeCount is not null) c.EmployeeCount = request.EmployeeCount;
        if (request.AnnualRevenue is not null) c.AnnualRevenue = request.AnnualRevenue;
        if (request.Address is not null) c.Address = InputGuard.StripControlCharacters(request.Address);
        if (request.Status is not null) c.Status = request.Status.Value;
        if (request.OwnerSubject is not null) c.OwnerSubject = request.OwnerSubject;
        if (request.Tags is not null) c.Tags = InputGuard.StripControlCharacters(request.Tags);
        if (request.MarketingConsent is not null) c.MarketingConsent = request.MarketingConsent.Value;
        c.UpdatedAt = _clock.GetUtcNow();

        // Step 3: บันทึกแบบ optimistic concurrency
        if (!await _customers.UpdateAsync(c, ct).ConfigureAwait(false))
        {
            throw new ConflictException("The customer was changed by someone else. Reload and try again.");
        }

        c.Version++;
        return c;
    }

    /// <summary>อ่านลูกค้า</summary>
    /// <param name="user">ผู้เรียก (customer.read)</param>
    /// <param name="id">รหัส</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้า</returns>
    /// <exception cref="NotFoundException">ไม่พบ</exception>
    public async Task<CustomerDTO> GetAsync(UserContextDTO user, long id, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.CustomerRead);
        return await _customers.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
    }

    /// <summary>อ่านลูกค้าจากรหัสลูกค้า</summary>
    /// <param name="user">ผู้เรียก (customer.read)</param>
    /// <param name="code">รหัสลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้า</returns>
    public async Task<CustomerDTO> GetByCodeAsync(UserContextDTO user, string code, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.CustomerRead);
        return await _customers.GetByCodeAsync(InputGuard.Required(code, "code", 30).ToUpperInvariant(), ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Customer '{code}' not found.");
    }

    /// <summary>ค้นหาลูกค้า</summary>
    /// <param name="user">ผู้เรียก (customer.read)</param>
    /// <param name="criteria">เงื่อนไข</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผลลัพธ์แบ่งหน้า</returns>
    public Task<PagedResultDTO<CustomerDTO>> SearchAsync(UserContextDTO user, CustomerSearchCriteria criteria, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + จำกัดขนาดหน้า (กันดึงข้อมูลจำนวนมากเกินไป)
        AccessGuard.Require(user, Permissions.CustomerRead);
        ArgumentNullException.ThrowIfNull(criteria);
        (criteria.Page, criteria.PageSize) = InputGuard.ClampPaging(criteria.Page, criteria.PageSize, 500);
        criteria.Text = string.IsNullOrWhiteSpace(criteria.Text) ? null : InputGuard.StripControlCharacters(criteria.Text).Trim();
        return _customers.SearchAsync(criteria, ct);
    }

    /// <summary>ผูกตัวตนในช่องทาง (LINE userId, Facebook PSID ...) กับลูกค้า</summary>
    /// <param name="user">ผู้เรียก (customer.write)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="request">ตัวตน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ตัวตนที่บันทึก</returns>
    /// <exception cref="ConflictException">ตัวตนนี้ผูกกับลูกค้ารายอื่นแล้ว</exception>
    public async Task<ChannelIdentityDTO> AddIdentityAsync(UserContextDTO user, long customerId, ChannelIdentityRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้ามีจริง
        AccessGuard.Require(user, Permissions.CustomerWrite);
        ArgumentNullException.ThrowIfNull(request);
        _ = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");

        // Step 2: normalize แล้วตรวจว่าไม่ได้เป็นของคนอื่น
        string externalId = NormalizeExternalId(request.Channel, request.ExternalId);
        if (await _relationships.FindIdentityAsync(request.Channel, externalId, ct).ConfigureAwait(false) is { } existing)
        {
            if (existing.CustomerId == customerId)
            {
                return existing;
            }

            throw new ConflictException("This channel identity is already linked to another customer.");
        }

        // Step 3: บันทึก
        var identity = new ChannelIdentityDTO { CustomerId = customerId, Channel = request.Channel, ExternalId = externalId, DisplayName = InputGuard.StripControlCharacters(request.DisplayName), Verified = true };
        identity.Id = await _relationships.InsertIdentityAsync(identity, ct).ConfigureAwait(false);
        return identity;
    }

    /// <summary>มุมมองลูกค้า 360 องศา: ข้อมูลหลัก ความสัมพันธ์ ตัวตน แชตเปิด กิจกรรมค้าง คำขอ โอกาสการขาย ลิงก์ และ SLA</summary>
    /// <param name="user">ผู้เรียก (customer.read)</param>
    /// <param name="id">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป 360</returns>
    public async Task<Customer360DTO> Get360Async(UserContextDTO user, long id, CancellationToken ct = default)
    {
        // Step 1: ข้อมูลหลัก
        CustomerDTO customer = await GetAsync(user, id, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // Step 2: ดึงข้อมูลประกอบพร้อมกัน (แต่ละ DAO เปิด connection ของตัวเองจาก pool → ขนานได้)
        Task<IReadOnlyList<PartyRelationshipDTO>> rel = _relationships.ListByCustomerAsync(id, ct);
        Task<IReadOnlyList<ChannelIdentityDTO>> ids = _relationships.ListIdentitiesAsync(id, ct);
        Task<IReadOnlyList<ConversationDTO>> conv = _conversations.ListAsync(null, id, false, 200, ct);
        Task<IReadOnlyList<ActivityDTO>> act = _activities.ListByCustomerAsync(id, true, 500, ct);
        Task<IReadOnlyList<CustomerRequestDTO>> req = _requests.ListAsync(id, null, 500, ct);
        Task<IReadOnlyList<OpportunityDTO>> opp = _opportunities.ListAsync(id, true, null, ct);
        Task<IReadOnlyList<ExternalLinkDTO>> links = _links.ListAsync(id, null, ct);
        Task<(CustomerDTO Customer, SlaPolicyDTO? Policy)> sla = _sla.ResolvePolicyAsync(customer.Code, now, ct);
        await Task.WhenAll(rel, ids, conv, act, req, opp, links, sla).ConfigureAwait(false);

        // Step 3: สรุป
        return new Customer360DTO(
            customer,
            rel.Result.Where(r => r.IsActiveOn(today)).ToList(),
            ids.Result,
            conv.Result.Count(c => c.Status != ConversationStatus.Resolved),
            act.Result.Count(a => a.IsOverdue(now)),
            req.Result.Count(r => r.Status is not (RequestStatus.Closed or RequestStatus.Rejected)),
            opp.Result,
            links.Result,
            sla.Result.Policy?.Name);
    }

    /// <summary>normalize รหัสในช่องทาง (อีเมลเป็นตัวพิมพ์เล็ก, ตัด control character)</summary>
    /// <param name="channel">ช่องทาง</param>
    /// <param name="externalId">รหัส</param>
    /// <returns>รหัสที่ normalize แล้ว</returns>
    internal static string NormalizeExternalId(ChannelKind channel, string externalId)
    {
        string value = InputGuard.Required(externalId, "externalId", 200);
        return channel == ChannelKind.Email ? value.ToLowerInvariant() : value;
    }

    /// <summary>เว็บไซต์ต้องเป็น https (กัน javascript:/data: URL ที่ UI อาจแสดงเป็นลิงก์)</summary>
    /// <param name="website">URL</param>
    private static void ValidateWebsite(string? website)
    {
        if (string.IsNullOrWhiteSpace(website))
        {
            return;
        }

        bool ok = Uri.TryCreate(website, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;
        ValidationException.ThrowIf(!ok, "website", "Website must be an absolute https URL.");
    }
}
