using System.Reflection;
using Common.Data.Infrastructure;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Data.Infrastructure;

/// <summary>
/// รัน migration ของ CustomerMgnt ตอนเริ่มระบบ (รูปแบบเดียวกับ Common.Data DatabaseMigrator แต่สแกน assembly นี้)
/// <list type="number">
/// <item>สร้างตาราง <c>customermgnt_schema_version</c> ถ้ายังไม่มี</item>
/// <item>หา embedded resource <c>Sql/postgres/NNN_*.sql</c> เรียงตามชื่อ ข้ามไฟล์ที่รันแล้ว</item>
/// <item>รันแต่ละไฟล์ใน transaction เดียว (ล้มเหลว = rollback ทั้งไฟล์) แล้วบันทึกเวอร์ชัน</item>
/// </list>
/// ใช้ advisory lock ป้องกันหลาย instance รัน migration พร้อมกัน
/// </summary>
public sealed class CustomerMgntMigrator : IHostedService
{
    /// <summary>key ของ pg_advisory_lock (ค่าคงที่ของระบบนี้)</summary>
    private const long AdvisoryLockKey = 0x43_4D_47_4D_54; // "CMGMT"

    private readonly IDbConnectionFactory _db;
    private readonly DatabaseOptions _options;
    private readonly ILogger<CustomerMgntMigrator> _logger;

    /// <summary>สร้าง migrator</summary>
    /// <param name="db">connection factory</param>
    /// <param name="options">ค่าตั้งฐานข้อมูล</param>
    /// <param name="logger">logger</param>
    public CustomerMgntMigrator(IDbConnectionFactory db, IOptions<DatabaseOptions> options, ILogger<CustomerMgntMigrator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>รัน migration</summary>
    /// <param name="cancellationToken">ยกเลิก</param>
    /// <returns>Task</returns>
    /// <exception cref="NotSupportedException">provider ไม่ใช่ PostgreSQL</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Step 1: ปิดได้ด้วย Database:RunMigrations=false (เช่น ให้ DBA รันเอง)
        if (!_options.RunMigrations)
        {
            return;
        }

        if (_db.Provider != DatabaseProvider.Postgres)
        {
            throw new NotSupportedException("CustomerMgnt supports PostgreSQL only (Database:Provider=Postgres).");
        }

        // Step 2: lock ระดับฐานข้อมูล + ตารางเวอร์ชัน
        await using var conn = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@key)", new { key = AdvisoryLockKey }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "CREATE TABLE IF NOT EXISTS customermgnt_schema_version (version VARCHAR(100) PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            var applied = (await conn.QueryAsync<string>(new CommandDefinition("SELECT version FROM customermgnt_schema_version", cancellationToken: cancellationToken)).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            // Step 3: ไฟล์ที่ยังไม่รัน เรียงตามชื่อ
            Assembly asm = typeof(CustomerMgntMigrator).Assembly;
            string prefix = $"{asm.GetName().Name}.Sql.{_db.Dialect.Name}.";
            foreach (string resource in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                string version = resource[prefix.Length..];
                if (applied.Contains(version))
                {
                    continue;
                }

                // Step 4: รันทั้งไฟล์ใน transaction แล้วบันทึกเวอร์ชัน
                using var reader = new StreamReader(asm.GetManifestResourceStream(resource)!);
                string sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                foreach (string batch in sql.Split(["\nGO\n", "\r\nGO\r\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    await conn.ExecuteAsync(new CommandDefinition(batch, transaction: tx, commandTimeout: 300, cancellationToken: cancellationToken)).ConfigureAwait(false);
                }

                await conn.ExecuteAsync(new CommandDefinition("INSERT INTO customermgnt_schema_version (version) VALUES (@version)", new { version }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Applied CustomerMgnt migration {Version}", version);
            }
        }
        finally
        {
            // Step 5: ปล่อย lock เสมอ
            await conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey }, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <summary>ไม่มีงานตอนหยุด</summary>
    /// <param name="cancellationToken">ยกเลิก</param>
    /// <returns>Task ที่เสร็จแล้ว</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
