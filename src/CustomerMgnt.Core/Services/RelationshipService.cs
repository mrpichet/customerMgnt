using Common.Core.Domain;
using Common.Core.Exceptions;
using Common.Core.Security;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// ความสัมพันธ์ระหว่างบุคคลกับนิติบุคคล (ตำแหน่ง + ช่วงเวลา)
/// กฎ: ฝั่งหนึ่งต้องเป็นบุคคล อีกฝั่งเป็นนิติบุคคล, ช่วงเวลาตำแหน่งเดียวกันในนิติบุคคลเดียวกันห้ามทับกัน,
/// บุคคลหนึ่งคนมีหลายนิติบุคคลได้
/// </summary>
public sealed class RelationshipService
{
    private readonly IRelationshipDAO _relationships;
    private readonly ICustomerDAO _customers;
    private readonly TimeProvider _clock;

    /// <summary>สร้างบริการ</summary>
    /// <param name="relationships">DAO ความสัมพันธ์</param>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="clock">นาฬิกา</param>
    public RelationshipService(IRelationshipDAO relationships, ICustomerDAO customers, TimeProvider clock)
    {
        _relationships = relationships;
        _customers = customers;
        _clock = clock;
    }

    /// <summary>ผูกบุคคลเข้ากับนิติบุคคลในตำแหน่งและช่วงเวลาที่กำหนด</summary>
    /// <param name="user">ผู้เรียก (customer.write)</param>
    /// <param name="request">ข้อมูลความสัมพันธ์</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ความสัมพันธ์ที่บันทึก</returns>
    /// <exception cref="ValidationException">ประเภทลูกค้าไม่ถูกต้อง หรือช่วงเวลาผิด</exception>
    /// <exception cref="ConflictException">ช่วงเวลาทับกับตำแหน่งเดิม</exception>
    public async Task<PartyRelationshipDTO> LinkAsync(UserContextDTO user, CreateRelationshipRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์
        AccessGuard.Require(user, Permissions.CustomerWrite);
        ArgumentNullException.ThrowIfNull(request);

        // Step 2: ทั้งสองฝ่ายต้องมีอยู่และประเภทถูกต้อง
        CustomerDTO person = await _customers.GetAsync(request.PersonId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Person not found.");
        CustomerDTO org = await _customers.GetAsync(request.OrganizationId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Organization not found.");
        ValidationException.ThrowIf(person.Kind != CustomerKind.Person, "personId", "PersonId must refer to a person.");
        ValidationException.ThrowIf(org.Kind != CustomerKind.Juristic, "organizationId", "OrganizationId must refer to a juristic person.");

        // Step 3: ช่วงเวลา
        DateOnly from = request.ValidFrom == default ? DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime) : request.ValidFrom;
        ValidationException.ThrowIf(request.ValidTo is { } to && to < from, "validTo", "ValidTo must be on or after ValidFrom.");
        string position = InputGuard.Required(request.Position, "position", 150);

        // Step 4: ตำแหน่งเดียวกันในนิติบุคคลเดียวกันต้องไม่ทับช่วงเวลา (ตำแหน่งต่างกันทับได้ เช่น CEO + กรรมการ)
        IReadOnlyList<PartyRelationshipDTO> existing = await _relationships.ListByCustomerAsync(person.Id, ct).ConfigureAwait(false);
        bool overlap = existing.Any(r => r.OrganizationId == org.Id && r.PersonId == person.Id
            && string.Equals(r.Position, position, StringComparison.OrdinalIgnoreCase)
            && Overlaps(r.ValidFrom, r.ValidTo, from, request.ValidTo));
        if (overlap)
        {
            throw new ConflictException("This person already holds this position in the organization during the given period.");
        }

        // Step 5: บันทึก
        var rel = new PartyRelationshipDTO
        {
            PersonId = person.Id,
            OrganizationId = org.Id,
            Position = position,
            Department = InputGuard.StripControlCharacters(request.Department),
            IsPrimaryContact = request.IsPrimaryContact,
            IsDecisionMaker = request.IsDecisionMaker,
            ValidFrom = from,
            ValidTo = request.ValidTo,
            PersonName = person.DisplayName,
            OrganizationName = org.DisplayName,
        };
        rel.Id = await _relationships.InsertAsync(rel, ct).ConfigureAwait(false);
        return rel;
    }

    /// <summary>สิ้นสุดความสัมพันธ์ (ลาออก/ย้ายตำแหน่ง)</summary>
    /// <param name="user">ผู้เรียก (customer.write)</param>
    /// <param name="relationshipId">รหัสความสัมพันธ์</param>
    /// <param name="validTo">วันสุดท้าย</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ความสัมพันธ์หลังแก้ไข</returns>
    public async Task<PartyRelationshipDTO> EndAsync(UserContextDTO user, long relationshipId, DateOnly validTo, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + มีอยู่จริง
        AccessGuard.Require(user, Permissions.CustomerWrite);
        PartyRelationshipDTO rel = await _relationships.GetAsync(relationshipId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Relationship not found.");

        // Step 2: วันสิ้นสุดต้องไม่ก่อนวันเริ่ม
        ValidationException.ThrowIf(validTo < rel.ValidFrom, "validTo", "ValidTo must be on or after ValidFrom.");
        await _relationships.EndAsync(relationshipId, validTo, ct).ConfigureAwait(false);
        rel.ValidTo = validTo;
        return rel;
    }

    /// <summary>ความสัมพันธ์ของลูกค้า (บุคคล → นิติบุคคลที่สังกัด, นิติบุคคล → บุคคลในองค์กร)</summary>
    /// <param name="user">ผู้เรียก (customer.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="activeOn">เฉพาะที่มีผล ณ วันที่ (null = ทั้งหมดรวมประวัติ)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    public async Task<IReadOnlyList<PartyRelationshipDTO>> ListAsync(UserContextDTO user, long customerId, DateOnly? activeOn, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.CustomerRead);
        IReadOnlyList<PartyRelationshipDTO> all = await _relationships.ListByCustomerAsync(customerId, ct).ConfigureAwait(false);
        return activeOn is { } d ? all.Where(r => r.IsActiveOn(d)).ToList() : all;
    }

    /// <summary>ช่วงวันที่สองช่วงทับกันหรือไม่ (null = ไม่มีวันสิ้นสุด)</summary>
    /// <param name="aFrom">ช่วง A เริ่ม</param>
    /// <param name="aTo">ช่วง A จบ</param>
    /// <param name="bFrom">ช่วง B เริ่ม</param>
    /// <param name="bTo">ช่วง B จบ</param>
    /// <returns>true ถ้าทับ</returns>
    internal static bool Overlaps(DateOnly aFrom, DateOnly? aTo, DateOnly bFrom, DateOnly? bTo) =>
        aFrom <= (bTo ?? DateOnly.MaxValue) && bFrom <= (aTo ?? DateOnly.MaxValue);
}
