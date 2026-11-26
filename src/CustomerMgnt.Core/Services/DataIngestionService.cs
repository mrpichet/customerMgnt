using Common.Core.Domain;
using Common.Core.Exceptions;
using Common.Core.Security;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// นำเข้าข้อมูลที่ใช้ฝึก ML: พฤติกรรมลูกค้า (เปิดอีเมล, คลิก, เข้าเว็บ, ซื้อ, ...), สัญญา MA และแคตตาล็อกสินค้า/เนื้อหา
/// ออกแบบเป็น batch (สูงสุด 10,000 เหตุการณ์ต่อครั้ง) เพื่อ throughput สูง
/// </summary>
public sealed class DataIngestionService
{
    /// <summary>จำนวนเหตุการณ์สูงสุดต่อครั้ง</summary>
    public const int MaxBatch = 10_000;

    private readonly ICustomerDAO _customers;
    private readonly IEngagementDAO _engagement;
    private readonly ISlaDAO _sla;
    private readonly TimeProvider _clock;

    /// <summary>สร้างบริการ</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="engagement">DAO พฤติกรรม/สัญญา/แคตตาล็อก</param>
    /// <param name="sla">DAO SLA (ตรวจนโยบายที่ผูกกับสัญญา)</param>
    /// <param name="clock">นาฬิกา</param>
    public DataIngestionService(ICustomerDAO customers, IEngagementDAO engagement, ISlaDAO sla, TimeProvider clock)
    {
        _customers = customers;
        _engagement = engagement;
        _sla = sla;
        _clock = clock;
    }

    /// <summary>นำเข้าเหตุการณ์พฤติกรรมเป็นชุด</summary>
    /// <param name="user">ผู้เรียก (data.ingest)</param>
    /// <param name="events">เหตุการณ์ (1..10,000)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>จำนวนที่บันทึก</returns>
    /// <exception cref="ValidationException">ข้อมูลไม่ถูกต้อง หรืออ้างถึงลูกค้าที่ไม่มีอยู่</exception>
    public async Task<int> IngestEventsAsync(UserContextDTO user, IReadOnlyList<EngagementEventRequest> events, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ขนาด batch
        AccessGuard.Require(user, Permissions.DataIngest);
        ArgumentNullException.ThrowIfNull(events);
        ValidationException.ThrowIf(events.Count == 0 || events.Count > MaxBatch, "events", $"Provide between 1 and {MaxBatch} events.");

        // Step 2: ตรวจว่าลูกค้ามีอยู่จริงทั้งหมด (query เดียวต่อ 1,000 ราย)
        long[] ids = events.Select(e => e.CustomerId).Distinct().ToArray();
        var found = new HashSet<long>();
        foreach (long[] chunk in ids.Chunk(1_000))
        {
            found.UnionWith((await _customers.GetManyAsync(chunk, ct).ConfigureAwait(false)).Select(c => c.Id));
        }

        long[] missing = ids.Where(id => !found.Contains(id)).Take(10).ToArray();
        ValidationException.ThrowIf(missing.Length > 0, "customerId", $"Unknown customer id(s): {string.Join(", ", missing)}");

        // Step 3: ตรวจค่าแต่ละรายการ แล้วแปลงเป็น DTO
        DateTimeOffset now = _clock.GetUtcNow();
        var list = new List<EngagementEventDTO>(events.Count);
        foreach (EngagementEventRequest e in events)
        {
            ValidationException.ThrowIf(!Enum.IsDefined(e.Kind), "kind", "Unknown engagement kind.");
            ValidationException.ThrowIf(e.OccurredAt > now.AddMinutes(5), "occurredAt", "Events cannot be in the future.");
            ValidationException.ThrowIf(e.ItemCode is not null && !InputGuard.IsSafeIdentifier(e.ItemCode, 60), "itemCode", "Item code contains invalid characters.");
            list.Add(new EngagementEventDTO
            {
                CustomerId = e.CustomerId,
                Kind = e.Kind,
                Channel = e.Channel,
                ItemCode = e.ItemCode,
                Value = e.Value,
                OccurredAt = e.OccurredAt ?? now,
            });
        }

        // Step 4: bulk insert
        return await _engagement.InsertManyAsync(list, ct).ConfigureAwait(false);
    }

    /// <summary>บันทึกสัญญา (MA/บริการ)</summary>
    /// <param name="user">ผู้เรียก (customer.write)</param>
    /// <param name="request">ข้อมูลสัญญา</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สัญญาที่บันทึก</returns>
    public async Task<ContractDTO> CreateContractAsync(UserContextDTO user, ContractRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้า + ช่วงวันที่
        AccessGuard.Require(user, Permissions.CustomerWrite);
        ArgumentNullException.ThrowIfNull(request);
        _ = await _customers.GetAsync(request.CustomerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
        ValidationException.ThrowIf(request.EndDate < request.StartDate, "endDate", "End date must be on or after start date.");
        if (request.SlaPolicyId is { } policyId)
        {
            SlaPolicyDTO? policy = await _sla.GetAsync(policyId, ct).ConfigureAwait(false);
            ValidationException.ThrowIf(policy is null || (policy.CustomerId is not null && policy.CustomerId != request.CustomerId), "slaPolicyId", "SLA policy not found for this customer.");
        }

        // Step 2: บันทึก
        var contract = new ContractDTO
        {
            CustomerId = request.CustomerId,
            ContractNo = InputGuard.Required(request.ContractNo, "contractNo", 50),
            Title = InputGuard.Required(request.Title, "title", 300),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            AnnualValue = request.AnnualValue,
            Status = ContractStatus.Active,
            SlaPolicyId = request.SlaPolicyId,
            ProductCode = NormalizeProductCode(request.ProductCode, required: false),
        };
        contract.Id = await _engagement.InsertContractAsync(contract, ct).ConfigureAwait(false);
        return contract;
    }

    /// <summary>ลูกค้าที่มีสัญญา Active ของผลิตภัณฑ์ (ProductMgnt เรียกตอนตรวจผลกระทบก่อนเปลี่ยนสถานะเป็น Sunset/Retired)</summary>
    /// <param name="user">ผู้เรียก (customer.read — token ของบริการ ProductMgnt หรือผู้ใช้)</param>
    /// <param name="productCode">รหัสผลิตภัณฑ์ใน ProductMgnt</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ลูกค้า + สัญญา (เฉพาะรหัส ชื่อ เลขที่สัญญา วันสิ้นสุด)</returns>
    public Task<IReadOnlyList<ProductCustomerDTO>> ListCustomersByProductAsync(UserContextDTO user, string productCode, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + รหัสผลิตภัณฑ์
        AccessGuard.Require(user, Permissions.CustomerRead);
        string code = NormalizeProductCode(productCode, required: true)!;

        // Step 2: "วันนี้" ตามเวลาไทย — สัญญาที่สิ้นสุดวันนี้ยังนับว่ามีผล
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), BusinessCalendar.ResolveZone("Asia/Bangkok")).DateTime);
        return _engagement.ListActiveCustomersByProductAsync(code, today, ct);
    }

    /// <summary>ตรวจและทำรหัสผลิตภัณฑ์ให้เป็นตัวพิมพ์ใหญ่ (เทียบได้ทั้ง SQL Server และ PostgreSQL)</summary>
    /// <param name="value">รหัส</param>
    /// <param name="required">บังคับกรอก</param>
    /// <returns>รหัสตัวพิมพ์ใหญ่ หรือ null เมื่อไม่บังคับและว่าง</returns>
    private static string? NormalizeProductCode(string? value, bool required)
    {
        // Step 1: ว่าง
        if (string.IsNullOrWhiteSpace(value))
        {
            ValidationException.ThrowIf(required, "productCode", "Product code is required.");
            return null;
        }

        // Step 2: อักขระที่ปลอดภัยเท่านั้น
        return InputGuard.RequireIdentifier(value.Trim().ToUpperInvariant(), "productCode", 50);
    }

    /// <summary>สัญญาของลูกค้า</summary>
    /// <param name="user">ผู้เรียก (customer.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สัญญา</returns>
    public Task<IReadOnlyList<ContractDTO>> ListContractsAsync(UserContextDTO user, long customerId, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.CustomerRead);
        return _engagement.ListContractsAsync(customerId, ct);
    }

    /// <summary>เพิ่ม/แก้ไขรายการในแคตตาล็อก</summary>
    /// <param name="user">ผู้เรียก (data.ingest)</param>
    /// <param name="item">รายการ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการที่บันทึก</returns>
    public async Task<CatalogItemDTO> UpsertCatalogItemAsync(UserContextDTO user, CatalogItemDTO item, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ตรวจค่า
        AccessGuard.Require(user, Permissions.DataIngest);
        ArgumentNullException.ThrowIfNull(item);
        item.Code = InputGuard.RequireIdentifier(item.Code, "code", 60);
        item.Name = InputGuard.Required(item.Name, "name", 200);
        item.Category = item.Category is null ? null : InputGuard.StripControlCharacters(item.Category);
        ValidationException.ThrowIf(!Enum.IsDefined(item.Kind), "kind", "Unknown catalog item kind.");
        ValidationException.ThrowIf(item.Price < 0, "price", "Price cannot be negative.");

        // Step 2: บันทึก
        await _engagement.UpsertCatalogAsync(item, ct).ConfigureAwait(false);
        return item;
    }

    /// <summary>แคตตาล็อกทั้งหมด</summary>
    /// <param name="user">ผู้เรียก (customer.read)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    public Task<IReadOnlyList<CatalogItemDTO>> ListCatalogAsync(UserContextDTO user, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.CustomerRead);
        return _engagement.ListCatalogAsync(ct);
    }
}
