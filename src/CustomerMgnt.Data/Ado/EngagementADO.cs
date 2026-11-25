using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Data.Infrastructure;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>พฤติกรรมลูกค้า สัญญา และแคตตาล็อก (input ของ ML) — PostgreSQL / SQL Server + Dapper</summary>
public sealed class EngagementADO : AdoBase, IEngagementDAO
{
    /// <summary>คอลัมน์สัญญา</summary>
    private const string ContractColumns = "id, customer_id, contract_no, title, start_date, end_date, annual_value, status, renewal_count, sla_policy_id, product_code";

    /// <summary>สร้าง ADO</summary>
    /// <param name="db">connection factory</param>
    /// <param name="logger">logger</param>
    public EngagementADO(IDbConnectionFactory db, ILogger<EngagementADO> logger) : base(db, logger) { }

    /// <inheritdoc />
    public Task<int> InsertManyAsync(IReadOnlyList<EngagementEventDTO> events, CancellationToken ct) =>
        RunAsync("engagement.insert_many", async c =>
        {
            // Step 1: bulk insert ด้วย unnest ของ array (1 คำสั่งต่อ 5,000 แถว — throughput สูงกว่า insert ทีละแถวหลายเท่า)
            int total = 0;
            foreach (EngagementEventDTO[] chunk in events.Chunk(5_000))
            {
                // SQL Server: ส่งทั้งชุดเป็น JSON แล้วแตกด้วย OPENJSON (1 คำสั่ง/ชุด เช่นเดียวกับ unnest)
                if (Dialect.IsSqlServer())
                {
                    const string sqlServer = """
                        INSERT INTO engagement_event (customer_id, kind, channel, item_code, value, occurred_at)
                        SELECT customerId, kind, channel, itemCode, value, occurredAt
                        FROM OPENJSON(@rows) WITH (customerId bigint, kind int, channel int, itemCode nvarchar(64), value decimal(18, 2), occurredAt datetimeoffset)
                        """;
                    var rows = chunk.Select(e => new { e.CustomerId, Kind = (int)e.Kind, Channel = (int?)e.Channel, e.ItemCode, e.Value, OccurredAt = e.OccurredAt.ToUniversalTime() });
                    total += await c.ExecuteAsync(Cmd(sqlServer, new { rows = SqlText.Rows(rows) }, ct)).ConfigureAwait(false);
                    continue;
                }

                const string sql = """
                    INSERT INTO engagement_event (customer_id, kind, channel, item_code, value, occurred_at)
                    SELECT * FROM unnest(@customerIds, @kinds, @channels, @items, @values, @occurred)
                    """;
                total += await c.ExecuteAsync(Cmd(sql, new
                {
                    customerIds = chunk.Select(e => e.CustomerId).ToArray(),
                    kinds = chunk.Select(e => (int)e.Kind).ToArray(),
                    channels = chunk.Select(e => (int?)e.Channel).ToArray(),
                    items = chunk.Select(e => e.ItemCode).ToArray(),
                    values = chunk.Select(e => e.Value).ToArray(),
                    occurred = chunk.Select(e => e.OccurredAt.ToUniversalTime()).ToArray(),
                }, ct)).ConfigureAwait(false);
            }

            return total;
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<EngagementEventDTO>> ListAsync(IReadOnlyCollection<long> customerIds, DateTimeOffset since, IReadOnlyCollection<EngagementKind>? kinds, CancellationToken ct) =>
        RunAsync<IReadOnlyList<EngagementEventDTO>>("engagement.list", async c =>
        {
            // Step 1: กรองลูกค้า/ช่วงเวลา/ชนิด (ใช้ index (customer_id, occurred_at))
            if (customerIds.Count == 0) return [];
            bool filterKinds = kinds is { Count: > 0 };
            string sql = $"""
                SELECT id, customer_id, kind, channel, item_code, value, occurred_at FROM engagement_event
                WHERE {Dialect.InSet("customer_id", "ids")} AND occurred_at >= @since{(filterKinds ? " AND " + Dialect.InSet("kind", "kinds", "int") : "")}
                """;
            object? kindParam = filterKinds ? Dialect.SetParam(kinds!.Select(k => (int)k)) : null;
            return (await c.QueryAsync<EngagementEventDTO>(Cmd(sql, new { ids = Dialect.SetParam(customerIds), since, kinds = kindParam }, ct)).ConfigureAwait(false)).ToList();
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<DateOnly, decimal>> MonthlyPurchasesAsync(long? customerId, DateTimeOffset since, CancellationToken ct) =>
        RunAsync<IReadOnlyDictionary<DateOnly, decimal>>("engagement.monthly", async c =>
        {
            // Step 1: รวมยอดซื้อรายเดือน (เขตเวลาไทย) ในฐานข้อมูล
            string month = Dialect.IsSqlServer()
                ? "DATEFROMPARTS(YEAR(occurred_at AT TIME ZONE 'SE Asia Standard Time'), MONTH(occurred_at AT TIME ZONE 'SE Asia Standard Time'), 1)"
                : "CAST(date_trunc('month', occurred_at AT TIME ZONE 'Asia/Bangkok') AS date)";
            string sql = $"""
                SELECT {month} AS month, SUM(value) AS total
                FROM engagement_event
                WHERE kind = 5 AND occurred_at >= @since AND (CAST(@customerId AS bigint) IS NULL OR customer_id = @customerId)
                GROUP BY {month}
                """;
            IEnumerable<MonthRow> rows = await c.QueryAsync<MonthRow>(Cmd(sql, new { customerId, since }, ct)).ConfigureAwait(false);
            return rows.ToDictionary(r => r.Month, r => r.Total);
        }, ct);

    /// <inheritdoc />
    public Task<long> InsertContractAsync(ContractDTO contract, CancellationToken ct) =>
        RunAsync("contract.insert", c => c.ExecuteScalarAsync<long>(Cmd(Dialect.InsertReturningId("contract", """
customer_id, contract_no, title, start_date, end_date, annual_value, status, renewal_count, sla_policy_id, product_code
""", """
@CustomerId, @ContractNo, @Title, @StartDate, @EndDate, @AnnualValue, @Status, @RenewalCount, @SlaPolicyId, @ProductCode
"""), new { contract.CustomerId, contract.ContractNo, contract.Title, contract.StartDate, contract.EndDate, contract.AnnualValue, Status = (int)contract.Status, contract.RenewalCount, contract.SlaPolicyId, contract.ProductCode }, ct)), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContractDTO>> ListContractsEndingAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ContractDTO>>("contract.ending", async c =>
            (await c.QueryAsync<ContractDTO>(Cmd($"SELECT {ContractColumns} FROM contract WHERE end_date BETWEEN @from AND @to ORDER BY end_date", new { from, to }, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContractDTO>> ListClosedContractsAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<ContractDTO>>("contract.closed", async c =>
            (await c.QueryAsync<ContractDTO>(Cmd($"SELECT {ContractColumns} FROM contract WHERE status IN (2, 3, 4) ORDER BY end_date DESC {Dialect.Take("200000")}", null, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContractDTO>> ListContractsAsync(long customerId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ContractDTO>>("contract.list", async c =>
            (await c.QueryAsync<ContractDTO>(Cmd($"SELECT {ContractColumns} FROM contract WHERE customer_id = @customerId ORDER BY end_date DESC", new { customerId }, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ProductCustomerDTO>> ListActiveCustomersByProductAsync(string productCode, DateOnly today, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ProductCustomerDTO>>("contract.by-product", async c =>
        {
            // Step 1: สัญญา Active (status 1) ที่ยังไม่สิ้นสุด + ชื่อลูกค้า (ใช้ index ix_contract_product)
            const string sql = """
                SELECT cu.code AS CustomerCode, cu.display_name AS CustomerName, ct.contract_no AS ContractNo, ct.end_date AS ContractEnd
                FROM contract ct
                JOIN customer cu ON cu.id = ct.customer_id
                WHERE ct.product_code = @productCode AND ct.status = 1 AND ct.end_date >= @today
                ORDER BY cu.code, ct.end_date
                """;
            return (await c.QueryAsync<ProductCustomerRow>(Cmd(sql, new { productCode, today }, ct)).ConfigureAwait(false))
                .Select(r => new ProductCustomerDTO(r.CustomerCode, r.CustomerName, r.ContractNo, r.ContractEnd)).ToList();
        }, ct);

    /// <summary>แถวผลลัพธ์ของ <see cref="ListActiveCustomersByProductAsync"/> (Dapper map ตามชื่อคอลัมน์)</summary>
    private sealed class ProductCustomerRow
    {
        public string CustomerCode { get; set; } = string.Empty;

        public string CustomerName { get; set; } = string.Empty;

        public string? ContractNo { get; set; }

        public DateOnly? ContractEnd { get; set; }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CatalogItemDTO>> ListCatalogAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<CatalogItemDTO>>("catalog.list", async c =>
            (await c.QueryAsync<CatalogItemDTO>(Cmd($"SELECT code, name, kind, category, price, is_active FROM catalog_item ORDER BY code {Dialect.Take("20000")}", null, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task UpsertCatalogAsync(CatalogItemDTO item, CancellationToken ct) =>
        RunAsync("catalog.upsert", c => c.ExecuteAsync(Cmd(Dialect.IsSqlServer()
            ? """
              MERGE catalog_item WITH (HOLDLOCK) AS t
              USING (SELECT @Code AS code) AS s ON t.code = s.code
              WHEN MATCHED THEN UPDATE SET name = @Name, kind = @Kind, category = @Category, price = @Price, is_active = @IsActive
              WHEN NOT MATCHED THEN INSERT (code, name, kind, category, price, is_active) VALUES (@Code, @Name, @Kind, @Category, @Price, @IsActive);
              """
            : """
              INSERT INTO catalog_item (code, name, kind, category, price, is_active) VALUES (@Code, @Name, @Kind, @Category, @Price, @IsActive)
              ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, kind = EXCLUDED.kind, category = EXCLUDED.category, price = EXCLUDED.price, is_active = EXCLUDED.is_active
              """, new { item.Code, item.Name, Kind = (int)item.Kind, item.Category, item.Price, item.IsActive }, ct)), ct);

    /// <summary>แถวยอดรายเดือน</summary>
    private sealed class MonthRow
    {
        /// <summary>วันแรกของเดือน</summary>
        public DateOnly Month { get; set; }

        /// <summary>ยอดรวม</summary>
        public decimal Total { get; set; }
    }
}
