using System.Data.Common;
using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>นโยบาย SLA และระดับ severity ต่อลูกค้า (PostgreSQL + Dapper)</summary>
public sealed class SlaADO : AdoBase, ISlaDAO
{
    /// <summary>คอลัมน์นโยบาย</summary>
    private const string PolicyColumns = """
        id, customer_id, name, description, valid_from, valid_to, time_zone_id, business_start, business_end, working_days_mask, holidays,
        at_risk_threshold, is_active, version
        """;

    /// <summary>คอลัมน์ระดับ</summary>
    private const string LevelColumns = "id, policy_id, code, name, description, rank, response_minutes, resolution_minutes, business_hours_only, issue_priority";

    /// <summary>สร้าง ADO</summary>
    /// <param name="db">connection factory</param>
    /// <param name="logger">logger</param>
    public SlaADO(IDbConnectionFactory db, ILogger<SlaADO> logger) : base(db, logger) { }

    /// <inheritdoc />
    public Task<long> InsertAsync(SlaPolicyDTO policy, CancellationToken ct) =>
        RunAsync("sla.insert", async c =>
        {
            // Step 1: นโยบาย + ระดับ ใน transaction เดียว
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            long id = await c.ExecuteScalarAsync<long>(Cmd("""
                INSERT INTO sla_policy (customer_id, name, description, valid_from, valid_to, time_zone_id, business_start, business_end, working_days_mask,
                    holidays, at_risk_threshold, is_active, version)
                VALUES (@CustomerId, @Name, @Description, @ValidFrom, @ValidTo, @TimeZoneId, @BusinessStart, @BusinessEnd, @WorkingDaysMask,
                    @Holidays, @AtRiskThreshold, @IsActive, 1)
                RETURNING id
                """, policy, ct, tx)).ConfigureAwait(false);
            await InsertLevelsAsync(c, tx, id, policy.Levels, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return id;
        }, ct);

    /// <inheritdoc />
    public Task<bool> UpdateAsync(SlaPolicyDTO policy, CancellationToken ct) =>
        RunAsync("sla.update", async c =>
        {
            // Step 1: optimistic concurrency บนนโยบาย
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            int rows = await c.ExecuteAsync(Cmd("""
                UPDATE sla_policy SET name = @Name, description = @Description, valid_from = @ValidFrom, valid_to = @ValidTo, time_zone_id = @TimeZoneId,
                    business_start = @BusinessStart, business_end = @BusinessEnd, working_days_mask = @WorkingDaysMask, holidays = @Holidays,
                    at_risk_threshold = @AtRiskThreshold, is_active = @IsActive, version = version + 1
                WHERE id = @Id AND version = @Version
                """, policy, ct, tx)).ConfigureAwait(false);
            if (rows != 1)
            {
                return false;
            }

            // Step 2: แทนที่ชุดระดับทั้งหมด (ระดับเป็นส่วนหนึ่งของนโยบาย — issue เก่าเก็บ due date ไว้แล้วจึงไม่กระทบ)
            await c.ExecuteAsync(Cmd("DELETE FROM severity_level WHERE policy_id = @Id", new { policy.Id }, ct, tx)).ConfigureAwait(false);
            await InsertLevelsAsync(c, tx, policy.Id, policy.Levels, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, ct);

    /// <inheritdoc />
    public Task<SlaPolicyDTO?> GetAsync(long id, CancellationToken ct) =>
        RunAsync("sla.get", async c =>
        {
            string sql = $"""
                SELECT {PolicyColumns} FROM sla_policy WHERE id = @id;
                SELECT {LevelColumns} FROM severity_level WHERE policy_id = @id ORDER BY rank, code;
                """;
            using SqlMapper.GridReader grid = await c.QueryMultipleAsync(Cmd(sql, new { id }, ct)).ConfigureAwait(false);
            SlaPolicyDTO? p = await grid.ReadSingleOrDefaultAsync<SlaPolicyDTO>().ConfigureAwait(false);
            if (p is not null)
            {
                p.Levels = (await grid.ReadAsync<SeverityLevelDTO>().ConfigureAwait(false)).ToList();
            }

            return p;
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<SlaPolicyDTO>> ListByCustomerAsync(long? customerId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<SlaPolicyDTO>>("sla.list", async c =>
        {
            // Step 1: นโยบายของลูกค้า (null = นโยบายมาตรฐาน) + ระดับทั้งหมดของนโยบายเหล่านั้น
            string sql = $"""
                SELECT {PolicyColumns} FROM sla_policy WHERE customer_id IS NOT DISTINCT FROM @customerId::bigint ORDER BY valid_from DESC, id DESC;
                SELECT {LevelColumns} FROM severity_level WHERE policy_id IN (SELECT id FROM sla_policy WHERE customer_id IS NOT DISTINCT FROM @customerId::bigint) ORDER BY rank, code;
                """;
            using SqlMapper.GridReader grid = await c.QueryMultipleAsync(Cmd(sql, new { customerId }, ct)).ConfigureAwait(false);
            List<SlaPolicyDTO> policies = (await grid.ReadAsync<SlaPolicyDTO>().ConfigureAwait(false)).ToList();
            ILookup<long, SeverityLevelDTO> levels = (await grid.ReadAsync<SeverityLevelDTO>().ConfigureAwait(false)).ToLookup(l => l.PolicyId);
            foreach (SlaPolicyDTO p in policies)
            {
                p.Levels = levels[p.Id].ToList();
            }

            return policies;
        }, ct);

    /// <summary>เพิ่มระดับของนโยบาย</summary>
    private Task InsertLevelsAsync(DbConnection c, DbTransaction tx, long policyId, IReadOnlyList<SeverityLevelDTO> levels, CancellationToken ct) =>
        levels.Count == 0
            ? Task.CompletedTask
            : c.ExecuteAsync(Cmd("""
                INSERT INTO severity_level (policy_id, code, name, description, rank, response_minutes, resolution_minutes, business_hours_only, issue_priority)
                VALUES (@PolicyId, @Code, @Name, @Description, @Rank, @ResponseMinutes, @ResolutionMinutes, @BusinessHoursOnly, @IssuePriority)
                """, levels.Select(l => new { PolicyId = policyId, l.Code, l.Name, l.Description, l.Rank, l.ResponseMinutes, l.ResolutionMinutes, l.BusinessHoursOnly, l.IssuePriority }).ToList(), ct, tx));
}
