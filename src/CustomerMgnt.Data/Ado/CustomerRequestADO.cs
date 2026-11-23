using System.Data.Common;
using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>คำขอของลูกค้า / TOR พร้อมรายการข้อกำหนด (PostgreSQL + Dapper)</summary>
public sealed class CustomerRequestADO : AdoBase, ICustomerRequestDAO
{
    /// <summary>คอลัมน์ของคำขอ</summary>
    private const string Columns = """
        id, request_no, customer_id, contact_person_id, kind, title, description, status, severity_code, budget, desired_date, owner_subject,
        forward_signature, forwarded_at, created_at, updated_at, version
        """;

    /// <summary>สร้าง ADO</summary>
    /// <param name="db">connection factory</param>
    /// <param name="logger">logger</param>
    public CustomerRequestADO(IDbConnectionFactory db, ILogger<CustomerRequestADO> logger) : base(db, logger) { }

    /// <inheritdoc />
    public Task<long> InsertAsync(CustomerRequestDTO request, CancellationToken ct) =>
        RunAsync("request.insert", async c =>
        {
            // Step 1: คำขอ + รายการ TOR ใน transaction เดียว (ไม่มีคำขอที่รายการหายครึ่งทาง)
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            long id = await c.ExecuteScalarAsync<long>(Cmd("""
                INSERT INTO customer_request (request_no, customer_id, contact_person_id, kind, title, description, status, severity_code, budget, desired_date,
                    owner_subject, forward_signature, forwarded_at, created_at, updated_at, version)
                VALUES (@RequestNo, @CustomerId, @ContactPersonId, @Kind, @Title, @Description, @Status, @SeverityCode, @Budget, @DesiredDate,
                    @OwnerSubject, @ForwardSignature, @ForwardedAt, @CreatedAt, @UpdatedAt, @Version)
                RETURNING id
                """, Params(request), ct, tx)).ConfigureAwait(false);

            // Step 2: รายการ TOR (Dapper รันคำสั่งต่อรายการใน connection เดียวกัน)
            if (request.Items.Count > 0)
            {
                await c.ExecuteAsync(Cmd("""
                    INSERT INTO tor_item (request_id, item_no, text, priority, mandatory, requirement_key)
                    VALUES (@RequestId, @ItemNo, @Text, @Priority, @Mandatory, @RequirementKey)
                    """, request.Items.Select(i => new { RequestId = id, i.ItemNo, i.Text, i.Priority, i.Mandatory, i.RequirementKey }).ToList(), ct, tx)).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return id;
        }, ct);

    /// <inheritdoc />
    public Task<bool> UpdateAsync(CustomerRequestDTO request, CancellationToken ct) =>
        RunAsync("request.update", async c =>
        {
            // Step 1: optimistic concurrency บนหัวคำขอ
            await using DbTransaction tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
            int rows = await c.ExecuteAsync(Cmd("""
                UPDATE customer_request SET title = @Title, description = @Description, status = @Status, severity_code = @SeverityCode, budget = @Budget,
                    desired_date = @DesiredDate, owner_subject = @OwnerSubject, forward_signature = @ForwardSignature, forwarded_at = @ForwardedAt,
                    updated_at = @UpdatedAt, version = version + 1
                WHERE id = @Id AND version = @Version
                """, Params(request), ct, tx)).ConfigureAwait(false);
            if (rows != 1)
            {
                return false;
            }

            // Step 2: อัปเดต requirement key ของรายการ (ได้มาหลังส่งต่อไป RequirementMgnt)
            List<object> keys = request.Items.Where(i => i.Id > 0).Select(i => (object)new { i.Id, i.RequirementKey }).ToList();
            if (keys.Count > 0)
            {
                await c.ExecuteAsync(Cmd("UPDATE tor_item SET requirement_key = @RequirementKey WHERE id = @Id", keys, ct, tx)).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, ct);

    /// <inheritdoc />
    public Task<CustomerRequestDTO?> GetAsync(long id, CancellationToken ct) =>
        RunAsync("request.get", async c =>
        {
            // Step 1: หัว + รายการ ใน round-trip เดียว
            string sql = $"""
                SELECT {Columns} FROM customer_request WHERE id = @id;
                SELECT id, request_id, item_no, text, priority, mandatory, requirement_key FROM tor_item WHERE request_id = @id ORDER BY id;
                """;
            using SqlMapper.GridReader grid = await c.QueryMultipleAsync(Cmd(sql, new { id }, ct)).ConfigureAwait(false);
            CustomerRequestDTO? r = await grid.ReadSingleOrDefaultAsync<CustomerRequestDTO>().ConfigureAwait(false);
            if (r is not null)
            {
                r.Items = (await grid.ReadAsync<TorItemDTO>().ConfigureAwait(false)).ToList();
            }

            return r;
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<CustomerRequestDTO>> ListAsync(long? customerId, RequestStatus? status, int take, CancellationToken ct) =>
        RunAsync<IReadOnlyList<CustomerRequestDTO>>("request.list", async c =>
        {
            // Step 1: รายการคำขอ (ไม่โหลดรายการ TOR เพื่อความเร็ว — ดูรายละเอียดผ่าน GetAsync)
            string sql = $"""
                SELECT {Columns} FROM customer_request
                WHERE (@customerId::bigint IS NULL OR customer_id = @customerId) AND (@status::int IS NULL OR status = @status)
                ORDER BY created_at DESC LIMIT @take
                """;
            return (await c.QueryAsync<CustomerRequestDTO>(Cmd(sql, new { customerId, status = (int?)status, take = Math.Clamp(take, 1, 1_000) }, ct)).ConfigureAwait(false)).ToList();
        }, ct);

    /// <inheritdoc />
    public Task<int> NextSequenceAsync(int year, CancellationToken ct) =>
        RunAsync("request.next_sequence", c => c.ExecuteScalarAsync<int>(Cmd("""
            INSERT INTO request_sequence (year, value) VALUES (@year, 1)
            ON CONFLICT (year) DO UPDATE SET value = request_sequence.value + 1
            RETURNING value
            """, new { year }, ct)), ct);

    /// <summary>parameter object</summary>
    private static object Params(CustomerRequestDTO x) => new
    {
        x.Id,
        x.RequestNo,
        x.CustomerId,
        x.ContactPersonId,
        Kind = (int)x.Kind,
        x.Title,
        x.Description,
        Status = (int)x.Status,
        x.SeverityCode,
        x.Budget,
        x.DesiredDate,
        x.OwnerSubject,
        x.ForwardSignature,
        x.ForwardedAt,
        x.CreatedAt,
        x.UpdatedAt,
        x.Version,
    };
}
