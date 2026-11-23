using System.Data.Common;
using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>โอกาสการขาย / pipeline (PostgreSQL + Dapper)</summary>
public sealed class OpportunityADO : AdoBase, IOpportunityDAO
{
    /// <summary>SELECT พร้อมชื่อลูกค้า</summary>
    private const string Select = """
        SELECT o.id, o.customer_id, o.title, o.stage, o.amount, o.probability, o.score, o.expected_close_date, o.source, o.owner_subject, o.lost_reason,
               o.stage_changed_at, o.closed_at, o.created_at, o.version, c.display_name AS customer_name
        FROM opportunity o JOIN customer c ON c.id = o.customer_id
        """;

    /// <summary>สร้าง ADO</summary>
    /// <param name="db">connection factory</param>
    /// <param name="logger">logger</param>
    public OpportunityADO(IDbConnectionFactory db, ILogger<OpportunityADO> logger) : base(db, logger) { }

    /// <inheritdoc />
    public Task<long> InsertAsync(OpportunityDTO opportunity, CancellationToken ct) =>
        RunAsync("opportunity.insert", c => c.ExecuteScalarAsync<long>(Cmd("""
            INSERT INTO opportunity (customer_id, title, stage, amount, probability, score, expected_close_date, source, owner_subject, lost_reason,
                stage_changed_at, closed_at, created_at, version)
            VALUES (@CustomerId, @Title, @Stage, @Amount, @Probability, @Score, @ExpectedCloseDate, @Source, @OwnerSubject, @LostReason,
                @StageChangedAt, @ClosedAt, @CreatedAt, @Version)
            RETURNING id
            """, Params(opportunity), ct)), ct);

    /// <inheritdoc />
    public Task<bool> UpdateAsync(OpportunityDTO opportunity, StageHistoryDTO? history, CancellationToken ct) =>
        RunAsync("opportunity.update", async c =>
        {
            // Step 1: optimistic concurrency + ประวัติการเปลี่ยน stage ใน transaction เดียว
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            int rows = await c.ExecuteAsync(Cmd("""
                UPDATE opportunity SET title = @Title, stage = @Stage, amount = @Amount, probability = @Probability, score = @Score,
                    expected_close_date = @ExpectedCloseDate, source = @Source, owner_subject = @OwnerSubject, lost_reason = @LostReason,
                    stage_changed_at = @StageChangedAt, closed_at = @ClosedAt, version = version + 1
                WHERE id = @Id AND version = @Version
                """, Params(opportunity), ct, tx)).ConfigureAwait(false);
            if (rows != 1)
            {
                return false;
            }

            if (history is not null)
            {
                await c.ExecuteAsync(Cmd("""
                    INSERT INTO stage_history (opportunity_id, from_stage, to_stage, changed_by, changed_at)
                    VALUES (@OpportunityId, @FromStage, @ToStage, @ChangedBy, @ChangedAt)
                    """, new { OpportunityId = opportunity.Id, FromStage = (int)history.FromStage, ToStage = (int)history.ToStage, history.ChangedBy, history.ChangedAt }, ct, tx)).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, ct);

    /// <inheritdoc />
    public Task<OpportunityDTO?> GetAsync(long id, CancellationToken ct) =>
        RunAsync("opportunity.get", c => c.QuerySingleOrDefaultAsync<OpportunityDTO>(Cmd($"{Select} WHERE o.id = @id", new { id }, ct)), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<OpportunityDTO>> ListAsync(long? customerId, bool openOnly, string? ownerSubject, CancellationToken ct) =>
        RunAsync<IReadOnlyList<OpportunityDTO>>("opportunity.list", async c =>
        {
            string sql = $"""
                {Select}
                WHERE (@customerId::bigint IS NULL OR o.customer_id = @customerId) AND (@openOnly = FALSE OR o.stage NOT IN (6, 7))
                  AND (@ownerSubject::varchar IS NULL OR o.owner_subject = @ownerSubject)
                ORDER BY o.expected_close_date, o.id
                LIMIT 50000
                """;
            return (await c.QueryAsync<OpportunityDTO>(Cmd(sql, new { customerId, openOnly, ownerSubject }, ct)).ConfigureAwait(false)).ToList();
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<OpportunityDTO>> ListClosedAsync(DateTimeOffset since, CancellationToken ct) =>
        RunAsync<IReadOnlyList<OpportunityDTO>>("opportunity.list_closed", async c =>
            (await c.QueryAsync<OpportunityDTO>(Cmd($"{Select} WHERE o.stage IN (6, 7) AND o.closed_at >= @since ORDER BY o.closed_at LIMIT 200000", new { since }, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task UpdateScoresAsync(IReadOnlyDictionary<long, double> scores, CancellationToken ct) =>
        RunAsync("opportunity.update_scores", async c =>
        {
            // Step 1: bulk update ด้วย unnest (ไม่เพิ่ม version เพราะคะแนนเป็นข้อมูลที่ระบบคำนวณ ไม่ใช่การแก้ไขของผู้ใช้)
            foreach (KeyValuePair<long, double>[] chunk in scores.Chunk(10_000))
            {
                await c.ExecuteAsync(Cmd(
                    "UPDATE opportunity AS t SET score = s.score FROM unnest(@ids, @values) AS s(id, score) WHERE t.id = s.id",
                    new { ids = chunk.Select(kv => kv.Key).ToArray(), values = chunk.Select(kv => kv.Value).ToArray() }, ct)).ConfigureAwait(false);
            }

            return true;
        }, ct);

    /// <summary>parameter object</summary>
    private static object Params(OpportunityDTO x) => new
    {
        x.Id,
        x.CustomerId,
        x.Title,
        Stage = (int)x.Stage,
        x.Amount,
        x.Probability,
        x.Score,
        x.ExpectedCloseDate,
        x.Source,
        x.OwnerSubject,
        x.LostReason,
        x.StageChangedAt,
        x.ClosedAt,
        x.CreatedAt,
        x.Version,
    };
}
