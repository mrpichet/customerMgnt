using Common.Data.Infrastructure;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Data.Infrastructure;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.Ado;

/// <summary>โปรไฟล์ผู้ใช้ภายใน (subject ของ AuthServer ↔ user id ของ MessageHub) — PostgreSQL / SQL Server + Dapper</summary>
public sealed class UserProfileADO : AdoBase, IUserProfileDAO
{
    /// <summary>สร้าง ADO</summary>
    /// <param name="db">connection factory</param>
    /// <param name="logger">logger</param>
    public UserProfileADO(IDbConnectionFactory db, ILogger<UserProfileADO> logger) : base(db, logger) { }

    /// <inheritdoc />
    public Task<UserProfileDTO?> GetAsync(string subject, CancellationToken ct) =>
        RunAsync("profile.get", c => c.QuerySingleOrDefaultAsync<UserProfileDTO>(Cmd(
            "SELECT subject, display_name, message_hub_user_id FROM user_profile WHERE subject = @subject", new { subject }, ct)), ct);

    /// <inheritdoc />
    public Task UpsertAsync(UserProfileDTO profile, CancellationToken ct) =>
        RunAsync("profile.upsert", c => c.ExecuteAsync(Cmd(Dialect.IsSqlServer()
            ? """
              MERGE user_profile WITH (HOLDLOCK) AS t
              USING (SELECT @Subject AS subject) AS s ON t.subject = s.subject
              WHEN MATCHED THEN UPDATE SET display_name = @DisplayName, message_hub_user_id = @MessageHubUserId
              WHEN NOT MATCHED THEN INSERT (subject, display_name, message_hub_user_id) VALUES (@Subject, @DisplayName, @MessageHubUserId);
              """
            : """
              INSERT INTO user_profile (subject, display_name, message_hub_user_id) VALUES (@Subject, @DisplayName, @MessageHubUserId)
              ON CONFLICT (subject) DO UPDATE SET display_name = EXCLUDED.display_name, message_hub_user_id = EXCLUDED.message_hub_user_id
              """, profile, ct)), ct);
}
