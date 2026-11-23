namespace CustomerMgnt.Core.Security;

/// <summary>
/// รหัสสิทธิ์ของ CustomerMgnt (มาจาก claim <c>perm</c>/<c>scope</c> ของ AuthServer; role <c>admin</c> มีทุกสิทธิ์)
/// ตรวจใน Service layer เสมอ ทำให้ Web API และ MCP ใช้กฎเดียวกัน
/// </summary>
public static class Permissions
{
    /// <summary>ดูข้อมูลลูกค้า</summary>
    public const string CustomerRead = "customer.read";
    /// <summary>ลงทะเบียน/แก้ไขลูกค้าและความสัมพันธ์</summary>
    public const string CustomerWrite = "customer.write";
    /// <summary>ดู inbox แชต</summary>
    public const string ChatRead = "chat.read";
    /// <summary>ตอบแชต</summary>
    public const string ChatReply = "chat.reply";
    /// <summary>บันทึกกิจกรรม/นัดหมาย</summary>
    public const string ActivityWrite = "activity.write";
    /// <summary>ดูคำขอ/TOR</summary>
    public const string RequestRead = "request.read";
    /// <summary>สร้าง/ตัดสินคำขอ</summary>
    public const string RequestWrite = "request.write";
    /// <summary>ส่งต่อคำขอไปยังระบบอื่น</summary>
    public const string RequestForward = "request.forward";
    /// <summary>ดู pipeline</summary>
    public const string SalesRead = "sales.read";
    /// <summary>แก้ไข pipeline</summary>
    public const string SalesWrite = "sales.write";
    /// <summary>ดู SLA</summary>
    public const string SlaRead = "sla.read";
    /// <summary>กำหนด SLA/Severity ของลูกค้า</summary>
    public const string SlaManage = "sla.manage";
    /// <summary>ดูผลวิเคราะห์ (ML) และสรุป issue/project</summary>
    public const string AnalyticsRead = "analytics.read";
    /// <summary>บันทึกข้อมูลพฤติกรรม/สัญญา/แคตตาล็อก (ระบบภายในหรือ ETL)</summary>
    public const string DataIngest = "data.ingest";

    /// <summary>ทุกสิทธิ์ (ใช้สร้าง client ใน AuthServer และเอกสาร)</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        CustomerRead, CustomerWrite, ChatRead, ChatReply, ActivityWrite, RequestRead, RequestWrite, RequestForward,
        SalesRead, SalesWrite, SlaRead, SlaManage, AnalyticsRead, DataIngest,
    ];
}
