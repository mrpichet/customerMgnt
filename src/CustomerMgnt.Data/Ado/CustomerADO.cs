using Common.Core.Domain;
using Common.Core.Security;
using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Data.Infrastructure;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>ลูกค้า (PostgreSQL / SQL Server + Dapper)</summary>
public sealed class CustomerADO : AdoBase, ICustomerDAO
{
    /// <summary>คอลัมน์ทั้งหมดของ customer</summary>
    internal const string Columns = """
        id, code, kind, display_name, legal_name, first_name, last_name, tax_id_masked, tax_id_envelope, tax_id_hash, email, phone,
        industry, employee_count, annual_revenue, website, address, province, country, time_zone_id, status, owner_subject, tags,
        segment, marketing_consent, created_at, updated_at, version
        """;

    /// <summary>สร้าง ADO</summary>
    /// <param name="db">connection factory</param>
    /// <param name="logger">logger</param>
    public CustomerADO(IDbConnectionFactory db, ILogger<CustomerADO> logger) : base(db, logger) { }

    /// <inheritdoc />
    public Task<long> InsertAsync(CustomerDTO customer, CancellationToken ct) =>
        RunAsync("customer.insert", async c =>
        {
            // Step 1: insert แล้วคืน id (version เริ่มที่ค่าใน DTO)
            string sql = Dialect.InsertReturningId("customer", """
code, kind, display_name, legal_name, first_name, last_name, tax_id_masked, tax_id_envelope, tax_id_hash, email, phone,
                    industry, employee_count, annual_revenue, website, address, province, country, time_zone_id, status, owner_subject, tags, segment,
                    marketing_consent, created_at, updated_at, version
""", """
@Code, @Kind, @DisplayName, @LegalName, @FirstName, @LastName, @TaxIdMasked, @TaxIdEnvelope, @TaxIdHash, @Email, @Phone,
                    @Industry, @EmployeeCount, @AnnualRevenue, @Website, @Address, @Province, @Country, @TimeZoneId, @Status, @OwnerSubject, @Tags, @Segment,
                    @MarketingConsent, @CreatedAt, @UpdatedAt, @Version
""");
            return await c.ExecuteScalarAsync<long>(Cmd(sql, Params(customer), ct)).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    public Task<bool> UpdateAsync(CustomerDTO customer, CancellationToken ct) =>
        RunAsync("customer.update", async c =>
        {
            // Step 1: optimistic concurrency — อัปเดตเมื่อ version ตรงเท่านั้น แล้วเพิ่ม version
            const string sql = """
                UPDATE customer SET code = @Code, display_name = @DisplayName, legal_name = @LegalName, first_name = @FirstName, last_name = @LastName,
                    tax_id_masked = @TaxIdMasked, tax_id_envelope = @TaxIdEnvelope, tax_id_hash = @TaxIdHash, email = @Email, phone = @Phone,
                    industry = @Industry, employee_count = @EmployeeCount, annual_revenue = @AnnualRevenue, website = @Website, address = @Address,
                    province = @Province, country = @Country, time_zone_id = @TimeZoneId, status = @Status, owner_subject = @OwnerSubject, tags = @Tags,
                    segment = @Segment, marketing_consent = @MarketingConsent, updated_at = @UpdatedAt, version = version + 1
                WHERE id = @Id AND version = @Version
                """;
            return await c.ExecuteAsync(Cmd(sql, Params(customer), ct)).ConfigureAwait(false) == 1;
        }, ct);

    /// <inheritdoc />
    public Task<CustomerDTO?> GetAsync(long id, CancellationToken ct) =>
        RunAsync("customer.get", c => c.QuerySingleOrDefaultAsync<CustomerDTO>(Cmd($"SELECT {Columns} FROM customer WHERE id = @id", new { id }, ct)), ct);

    /// <inheritdoc />
    public Task<CustomerDTO?> GetByCodeAsync(string code, CancellationToken ct) =>
        RunAsync("customer.get_by_code", c => c.QuerySingleOrDefaultAsync<CustomerDTO>(Cmd($"SELECT {Columns} FROM customer WHERE code = @code", new { code }, ct)), ct);

    /// <inheritdoc />
    public Task<CustomerDTO?> FindByTaxIdHashAsync(string taxIdHash, CancellationToken ct) =>
        RunAsync("customer.find_tax", c => c.QuerySingleOrDefaultAsync<CustomerDTO>(Cmd($"SELECT {Columns} FROM customer WHERE tax_id_hash = @taxIdHash", new { taxIdHash }, ct)), ct);

    /// <inheritdoc />
    public Task<PagedResultDTO<CustomerDTO>> SearchAsync(CustomerSearchCriteria criteria, CancellationToken ct) =>
        RunAsync("customer.search", async c =>
        {
            // Step 1: เงื่อนไขทั้งหมดเป็น parameter (ค่า null = ไม่กรอง) + escape wildcard ของคำค้น
            ArgumentNullException.ThrowIfNull(criteria);
            (int page, int pageSize) = InputGuard.ClampPaging(criteria.Page, criteria.PageSize);
            string? text = string.IsNullOrWhiteSpace(criteria.Text) ? null : $"%{InputGuard.EscapeLike(criteria.Text.Trim())}%";
            var p = new
            {
                text,
                kind = (int?)criteria.Kind,
                status = (int?)criteria.Status,
                owner = criteria.OwnerSubject,
                segment = criteria.Segment,
                offset = (page - 1) * pageSize,
                take = pageSize,
            };
            // ILIKE (PostgreSQL) / LIKE (SQL Server — collation ปกติไม่สนตัวพิมพ์อยู่แล้ว)
            string like = Dialect.LikeOperator;
            string where = $"""
                WHERE (CAST(@text AS varchar(300)) IS NULL OR display_name {like} @text ESCAPE '\' OR code {like} @text ESCAPE '\' OR email {like} @text ESCAPE '\' OR legal_name {like} @text ESCAPE '\')
                  AND (CAST(@kind AS int) IS NULL OR kind = @kind) AND (CAST(@status AS int) IS NULL OR status = @status)
                  AND (CAST(@owner AS varchar(200)) IS NULL OR owner_subject = @owner) AND (CAST(@segment AS varchar(200)) IS NULL OR segment = @segment)
                """;

            // Step 2: นับทั้งหมด + ดึงหน้าเดียวใน round-trip เดียว
            string sql = $"SELECT COUNT(*) FROM customer {where};\nSELECT {Columns} FROM customer {where} ORDER BY display_name, id {Dialect.Page("@offset", "@take")};";
            using SqlMapper.GridReader grid = await c.QueryMultipleAsync(Cmd(sql, p, ct)).ConfigureAwait(false);
            long total = await grid.ReadSingleAsync<long>().ConfigureAwait(false);
            List<CustomerDTO> items = (await grid.ReadAsync<CustomerDTO>().ConfigureAwait(false)).ToList();
            return new PagedResultDTO<CustomerDTO>(items, total, page, pageSize);
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<CustomerDTO>> GetManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CustomerDTO>>("customer.get_many", async c =>
        {
            if (ids.Count == 0) return [];
            return (await c.QueryAsync<CustomerDTO>(Cmd($"SELECT {Columns} FROM customer WHERE {Dialect.InSet("id", "ids")}", new { ids = Dialect.SetParam(ids) }, ct)).ConfigureAwait(false)).ToList();
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<CustomerDTO>> ListBatchAsync(long afterId, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CustomerDTO>>("customer.list_batch", async c =>
            (await c.QueryAsync<CustomerDTO>(Cmd($"SELECT {Columns} FROM customer WHERE id > @afterId ORDER BY id {Dialect.Take("@take")}", new { afterId, take = Math.Clamp(take, 1, 10_000) }, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task UpdateSegmentsAsync(IReadOnlyDictionary<long, string> segments, CancellationToken ct) =>
        RunAsync("customer.update_segments", async c =>
        {
            // Step 1: bulk update ด้วย unnest ของ array คู่ (เร็วกว่าอัปเดตทีละแถวมาก) แบ่งชุดละ 10k
            foreach (KeyValuePair<long, string>[] chunk in segments.Chunk(10_000))
            {
                // SQL Server: ส่งคู่ (id, segment) เป็น JSON แล้วแตกด้วย OPENJSON (1 parameter ต่อชุด)
                if (Dialect.IsSqlServer())
                {
                    const string sqlServer = """
                        UPDATE t SET segment = s.segment
                        FROM customer AS t JOIN OPENJSON(@rows) WITH (id bigint '$.id', segment nvarchar(100) '$.segment') AS s ON t.id = s.id
                        WHERE t.segment IS DISTINCT FROM s.segment
                        """;
                    await c.ExecuteAsync(Cmd(sqlServer, new { rows = SqlText.Rows(chunk.Select(kv => new { id = kv.Key, segment = kv.Value })) }, ct)).ConfigureAwait(false);
                    continue;
                }

                const string sql = """
                    UPDATE customer AS t SET segment = s.segment
                    FROM unnest(@ids, @names) AS s(id, segment)
                    WHERE t.id = s.id AND t.segment IS DISTINCT FROM s.segment
                    """;
                await c.ExecuteAsync(Cmd(sql, new { ids = chunk.Select(kv => kv.Key).ToArray(), names = chunk.Select(kv => kv.Value).ToArray() }, ct)).ConfigureAwait(false);
            }

            return true;
        }, ct);

    /// <summary>parameter object (enum → int)</summary>
    private static object Params(CustomerDTO x) => new
    {
        x.Id,
        x.Code,
        Kind = (int)x.Kind,
        x.DisplayName,
        x.LegalName,
        x.FirstName,
        x.LastName,
        x.TaxIdMasked,
        x.TaxIdEnvelope,
        x.TaxIdHash,
        x.Email,
        x.Phone,
        x.Industry,
        x.EmployeeCount,
        x.AnnualRevenue,
        x.Website,
        x.Address,
        x.Province,
        x.Country,
        x.TimeZoneId,
        Status = (int)x.Status,
        x.OwnerSubject,
        x.Tags,
        x.Segment,
        x.MarketingConsent,
        x.CreatedAt,
        x.UpdatedAt,
        x.Version,
    };
}
