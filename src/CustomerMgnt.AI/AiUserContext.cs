using Common.Core.Domain;
using CustomerMgnt.Core.Security;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.AI;

/// <summary>
/// ระบุตัวผู้ใช้ของการเรียก tool แต่ละครั้ง — ทุก tool รันด้วยสิทธิ์ของผู้ใช้นี้ (service layer ตรวจสิทธิ์ซ้ำเสมอ)
/// <list type="bullet">
/// <item>MCP over HTTP / IChatClient ใน Web API: ผู้ใช้จาก JWT ของ AuthServer (HttpContext)</item>
/// <item>MCP stdio: ตัวตนที่ตั้งค่าไว้ (<see cref="McpIdentityOptions"/>) — ค่าปริยายเป็นสิทธิ์อ่านอย่างเดียว</item>
/// </list>
/// </summary>
public interface IAiUserContextAccessor
{
    /// <summary>ผู้ใช้ปัจจุบัน</summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>ผู้ใช้</returns>
    ValueTask<UserContextDTO> GetAsync(CancellationToken ct);
}

/// <summary>ตัวตนของ MCP stdio (section <c>Mcp:Identity</c>)</summary>
public sealed class McpIdentityOptions
{
    /// <summary>ชื่อ section</summary>
    public const string SectionName = "Mcp:Identity";

    /// <summary>subject (บันทึกใน audit/เจ้าของงาน)</summary>
    public string Subject { get; set; } = "mcp-local";

    /// <summary>ชื่อแสดง</summary>
    public string UserName { get; set; } = "MCP (local)";

    /// <summary>
    /// สิทธิ์ที่ให้ AI (ว่าง = <see cref="DefaultPermissions"/> แบบอ่านอย่างเดียว; ใส่ *.write เองถ้าต้องการให้ AI แก้ข้อมูลได้)
    /// ค่าปริยายไม่ใส่ไว้ใน list โดยตรง เพราะ configuration binder จะ "ต่อท้าย" list เดิม ทำให้ลดสิทธิ์ผ่าน config ไม่ได้
    /// </summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>สิทธิ์ปริยาย (อ่านอย่างเดียว)</summary>
    public static IReadOnlyList<string> DefaultPermissions { get; } =
    [
        CustomerMgnt.Core.Security.Permissions.CustomerRead, CustomerMgnt.Core.Security.Permissions.ChatRead, CustomerMgnt.Core.Security.Permissions.RequestRead,
        CustomerMgnt.Core.Security.Permissions.SalesRead, CustomerMgnt.Core.Security.Permissions.SlaRead, CustomerMgnt.Core.Security.Permissions.AnalyticsRead,
    ];

    /// <summary>สิทธิ์ที่ใช้จริง</summary>
    /// <returns>สิทธิ์จาก config หรือค่าปริยาย</returns>
    public IReadOnlyList<string> EffectivePermissions() => Permissions.Count > 0 ? Permissions : DefaultPermissions;
}

/// <summary>ตัวตนคงที่จาก configuration (ใช้กับ MCP stdio)</summary>
public sealed class ConfiguredAiUserContextAccessor : IAiUserContextAccessor
{
    private readonly UserContextDTO _user;

    /// <summary>สร้าง accessor</summary>
    /// <param name="options">ค่าตั้ง</param>
    public ConfiguredAiUserContextAccessor(IOptions<McpIdentityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        McpIdentityOptions o = options.Value;

        // Step 1: ไม่ยอมให้ตั้งสิทธิ์ที่ไม่รู้จัก (กันพิมพ์ผิดแล้วได้สิทธิ์เกิน/ขาดโดยไม่รู้ตัว) และไม่ให้ role admin ผ่าน config
        IReadOnlyList<string> granted = o.EffectivePermissions();
        string[] unknown = granted.Except(Permissions.All, StringComparer.OrdinalIgnoreCase).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException($"Unknown permission(s) in {McpIdentityOptions.SectionName}: {string.Join(", ", unknown)}");
        }

        _user = new UserContextDTO(o.Subject, o.UserName, [], granted.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), null);
    }

    /// <inheritdoc />
    public ValueTask<UserContextDTO> GetAsync(CancellationToken ct) => ValueTask.FromResult(_user);
}
