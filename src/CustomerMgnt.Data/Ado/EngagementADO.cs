using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>พฤติกรรมลูกค้า สัญญา และแคตตาล็อก (input ของ ML) — PostgreSQL + Dapper</summary>
public sealed class EngagementADO : AdoBase, IEngagementDAO
{
    /// <summary>คอลัมน์สัญญา</summary>
    private const string ContractColumns = "id, customer_id, contract_no, title, start_date, end_date, annual_value, status, renewal_count, sla_policy_id";

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
            const string sql = """
                SELECT id, customer_id, kind, channel, item_code, value, occurred_at FROM engagement_event
                WHERE customer_id = ANY(@ids) AND occurred_at >= @since AND (@kinds::int[] IS NULL OR kind = ANY(@kinds))
                """;
            int[]? kindArray = kinds is null || kinds.Count == 0 ? null : kinds.Select(k => (int)k).ToArray();
            return (await c.QueryAsync<EngagementEventDTO>(Cmd(sql, new { ids = customerIds.ToArray(), since, kinds = kindArray }, ct)).ConfigureAwait(false)).ToList();
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<DateOnly, decimal>> MonthlyPurchasesAsync(long? customerId, DateTimeOffset since, CancellationToken ct) =>
        RunAsync<IReadOnlyDictionary<DateOnly, decimal>>("engagement.monthly", async c =>
        {
            // Step 1: รวมยอดซื้อรายเดือน (เขตเวลาไทย) ในฐานข้อมูล
            const string sql = """
                SELECT date_trunc('month', occurred_at AT TIME ZONE 'Asia/Bangkok')::date AS month, SUM(value) AS total
                FROM engagement_event
                WHERE kind = 5 AND occurred_at >= @since AND (@customerId::bigint IS NULL OR customer_id = @customerId)
                GROUP BY 1
                """;
            IEnumerable<MonthRow> rows = await c.QueryAsync<MonthRow>(Cmd(sql, new { customerId, since }, ct)).ConfigureAwait(false);
            return rows.ToDictionary(r => r.Month, r => r.Total);
        }, ct);

    /// <inheritdoc />
    public Task<long> InsertContractAsync(ContractDTO contract, CancellationToken ct) =>
        RunAsync("contract.insert", c => c.ExecuteScalarAsync<long>(Cmd("""
            INSERT INTO contract (customer_id, contract_no, title, start_date, end_date, annual_value, status, renewal_count, sla_policy_id)
            VALUES (@CustomerId, @ContractNo, @Title, @StartDate, @EndDate, @AnnualValue, @Status, @RenewalCount, @SlaPolicyId)
            RETURNING id
            """, new { contract.CustomerId, contract.ContractNo, contract.Title, contract.StartDate, contract.EndDate, contract.AnnualValue, Status = (int)contract.Status, contract.RenewalCount, contract.SlaPolicyId }, ct)), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContractDTO>> ListContractsEndingAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ContractDTO>>("contract.ending", async c =>
            (await c.QueryAsync<ContractDTO>(Cmd($"SELECT {ContractColumns} FROM contract WHERE end_date BETWEEN @from AND @to ORDER BY end_date", new { from, to }, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContractDTO>> ListClosedContractsAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<ContractDTO>>("contract.closed", async c =>
            (await c.QueryAsync<ContractDTO>(Cmd($"SELECT {ContractColumns} FROM contract WHERE status IN (2, 3, 4) ORDER BY end_date DESC LIMIT 200000", null, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContractDTO>> ListContractsAsync(long customerId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ContractDTO>>("contract.list", async c =>
            (await c.QueryAsync<ContractDTO>(Cmd($"SELECT {ContractColumns} FROM contract WHERE customer_id = @customerId ORDER BY end_date DESC", new { customerId }, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<CatalogItemDTO>> ListCatalogAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<CatalogItemDTO>>("catalog.list", async c =>
            (await c.QueryAsync<CatalogItemDTO>(Cmd("SELECT code, name, kind, category, price, is_active FROM catalog_item ORDER BY code LIMIT 20000", null, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task UpsertCatalogAsync(CatalogItemDTO item, CancellationToken ct) =>
        RunAsync("catalog.upsert", c => c.ExecuteAsync(Cmd("""
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
