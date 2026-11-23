using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>ลิงก์ลูกค้า ↔ ระบบอื่น (PostgreSQL + Dapper)</summary>
public sealed class ExternalLinkADO : AdoBase, IExternalLinkDAO
{
    /// <summary>คอลัมน์ทั้งหมด</summary>
    private const string Columns = "id, customer_id, system, entity_type, external_id, title, customer_request_id, created_at";

    /// <summary>สร้าง ADO</summary>
    /// <param name="db">connection factory</param>
    /// <param name="logger">logger</param>
    public ExternalLinkADO(IDbConnectionFactory db, ILogger<ExternalLinkADO> logger) : base(db, logger) { }

    /// <inheritdoc />
    public Task<long> UpsertAsync(ExternalLinkDTO link, CancellationToken ct) =>
        RunAsync("link.upsert", c => c.ExecuteScalarAsync<long>(Cmd("""
            INSERT INTO external_link (customer_id, system, entity_type, external_id, title, customer_request_id, created_at)
            VALUES (@CustomerId, @System, @EntityType, @ExternalId, @Title, @CustomerRequestId, @CreatedAt)
            ON CONFLICT (customer_id, system, entity_type, external_id)
            DO UPDATE SET title = COALESCE(EXCLUDED.title, external_link.title),
                          customer_request_id = COALESCE(external_link.customer_request_id, EXCLUDED.customer_request_id)
            RETURNING id
            """, new { link.CustomerId, System = (int)link.System, link.EntityType, link.ExternalId, link.Title, link.CustomerRequestId, link.CreatedAt }, ct)), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ExternalLinkDTO>> ListAsync(long customerId, ExternalSystem? system, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ExternalLinkDTO>>("link.list", async c =>
            (await c.QueryAsync<ExternalLinkDTO>(Cmd(
                $"SELECT {Columns} FROM external_link WHERE customer_id = @customerId AND (@system::int IS NULL OR system = @system) ORDER BY created_at DESC",
                new { customerId, system = (int?)system }, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ExternalLinkDTO>> ListByRequestAsync(long requestId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ExternalLinkDTO>>("link.list_request", async c =>
            (await c.QueryAsync<ExternalLinkDTO>(Cmd($"SELECT {Columns} FROM external_link WHERE customer_request_id = @requestId ORDER BY id", new { requestId }, ct)).ConfigureAwait(false)).ToList(), ct);
}
