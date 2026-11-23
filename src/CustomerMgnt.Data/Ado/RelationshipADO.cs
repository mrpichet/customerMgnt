using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>ความสัมพันธ์บุคคล ↔ นิติบุคคล และตัวตนในช่องทางต่าง ๆ (PostgreSQL + Dapper)</summary>
public sealed class RelationshipADO : AdoBase, IRelationshipDAO
{
    /// <summary>SELECT พร้อมชื่อบุคคล/บริษัท</summary>
    private const string Select = """
        SELECT r.id, r.person_id, r.organization_id, r.position, r.department, r.is_primary_contact, r.is_decision_maker, r.valid_from, r.valid_to,
               p.display_name AS person_name, o.display_name AS organization_name
        FROM party_relationship r
        JOIN customer p ON p.id = r.person_id
        JOIN customer o ON o.id = r.organization_id
        """;

    /// <summary>สร้าง ADO</summary>
    /// <param name="db">connection factory</param>
    /// <param name="logger">logger</param>
    public RelationshipADO(IDbConnectionFactory db, ILogger<RelationshipADO> logger) : base(db, logger) { }

    /// <inheritdoc />
    public Task<long> InsertAsync(PartyRelationshipDTO relationship, CancellationToken ct) =>
        RunAsync("relationship.insert", c => c.ExecuteScalarAsync<long>(Cmd("""
            INSERT INTO party_relationship (person_id, organization_id, position, department, is_primary_contact, is_decision_maker, valid_from, valid_to)
            VALUES (@PersonId, @OrganizationId, @Position, @Department, @IsPrimaryContact, @IsDecisionMaker, @ValidFrom, @ValidTo)
            RETURNING id
            """, relationship, ct)), ct);

    /// <inheritdoc />
    public Task<bool> EndAsync(long id, DateOnly validTo, CancellationToken ct) =>
        RunAsync("relationship.end", async c =>
            await c.ExecuteAsync(Cmd("UPDATE party_relationship SET valid_to = @validTo WHERE id = @id AND valid_from <= @validTo", new { id, validTo }, ct)).ConfigureAwait(false) == 1, ct);

    /// <inheritdoc />
    public Task<PartyRelationshipDTO?> GetAsync(long id, CancellationToken ct) =>
        RunAsync("relationship.get", c => c.QuerySingleOrDefaultAsync<PartyRelationshipDTO>(Cmd($"{Select} WHERE r.id = @id", new { id }, ct)), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<PartyRelationshipDTO>> ListByCustomerAsync(long customerId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<PartyRelationshipDTO>>("relationship.list", async c =>
            (await c.QueryAsync<PartyRelationshipDTO>(Cmd($"{Select} WHERE r.person_id = @customerId OR r.organization_id = @customerId ORDER BY r.valid_to NULLS FIRST, r.valid_from DESC", new { customerId }, ct)).ConfigureAwait(false)).ToList(), ct);

    /// <inheritdoc />
    public Task<long> InsertIdentityAsync(ChannelIdentityDTO identity, CancellationToken ct) =>
        RunAsync("identity.insert", c => c.ExecuteScalarAsync<long>(Cmd("""
            INSERT INTO channel_identity (customer_id, channel, external_id, display_name, verified)
            VALUES (@CustomerId, @Channel, @ExternalId, @DisplayName, @Verified)
            RETURNING id
            """, new { identity.CustomerId, Channel = (int)identity.Channel, identity.ExternalId, identity.DisplayName, identity.Verified }, ct)), ct);

    /// <inheritdoc />
    public Task<ChannelIdentityDTO?> FindIdentityAsync(ChannelKind channel, string externalId, CancellationToken ct) =>
        RunAsync("identity.find", c => c.QuerySingleOrDefaultAsync<ChannelIdentityDTO>(Cmd(
            "SELECT id, customer_id, channel, external_id, display_name, verified FROM channel_identity WHERE channel = @channel AND external_id = @externalId",
            new { channel = (int)channel, externalId }, ct)), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ChannelIdentityDTO>> ListIdentitiesAsync(long customerId, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ChannelIdentityDTO>>("identity.list", async c =>
            (await c.QueryAsync<ChannelIdentityDTO>(Cmd(
                "SELECT id, customer_id, channel, external_id, display_name, verified FROM channel_identity WHERE customer_id = @customerId ORDER BY channel, id",
                new { customerId }, ct)).ConfigureAwait(false)).ToList(), ct);
}
