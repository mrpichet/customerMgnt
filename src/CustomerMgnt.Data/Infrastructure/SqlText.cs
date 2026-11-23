using System.Text.Json;
using Common.Data.Infrastructure;

namespace CustomerMgnt.Data.Infrastructure;

/// <summary>
/// ส่วนของ SQL ที่ต่างกันระหว่าง PostgreSQL และ SQL Server (2022+)
/// ADO เขียน SQL แบบกลางให้มากที่สุด (CAST, IS DISTINCT FROM, CONCAT ใช้ได้ทั้งคู่) และเรียก helper นี้เฉพาะจุดที่ต่างจริง
/// </summary>
internal static class SqlText
{
    /// <summary>เป็น SQL Server หรือไม่</summary>
    /// <param name="d">dialect</param>
    /// <returns>true = SQL Server</returns>
    public static bool IsSqlServer(this ISqlDialect d) => d is SqlServerDialect;

    /// <summary>INSERT ที่คืน id ที่สร้าง (PostgreSQL: RETURNING, SQL Server: OUTPUT INSERTED)</summary>
    /// <param name="d">dialect</param>
    /// <param name="table">ชื่อตาราง</param>
    /// <param name="columns">คอลัมน์ (คั่นด้วย ,)</param>
    /// <param name="values">ค่า/parameter (คั่นด้วย ,)</param>
    /// <returns>SQL</returns>
    public static string InsertReturningId(this ISqlDialect d, string table, string columns, string values) => d.IsSqlServer()
        ? $"INSERT INTO {table} ({columns}) OUTPUT INSERTED.id VALUES ({values})"
        : $"INSERT INTO {table} ({columns}) VALUES ({values}) RETURNING id";

    /// <summary>จำกัดจำนวนแถว (วางท้าย ORDER BY เสมอ)</summary>
    /// <param name="d">dialect</param>
    /// <param name="take">parameter หรือค่าคงที่ (เช่น "@take", "1000")</param>
    /// <returns>SQL</returns>
    public static string Take(this ISqlDialect d, string take) => d.IsSqlServer() ? $"OFFSET 0 ROWS FETCH NEXT {take} ROWS ONLY" : $"LIMIT {take}";

    /// <summary>แบ่งหน้า (วางท้าย ORDER BY เสมอ)</summary>
    /// <param name="d">dialect</param>
    /// <param name="offset">parameter offset</param>
    /// <param name="take">parameter จำนวน</param>
    /// <returns>SQL</returns>
    public static string Page(this ISqlDialect d, string offset, string take) => d.IsSqlServer()
        ? $"OFFSET {offset} ROWS FETCH NEXT {take} ROWS ONLY"
        : $"OFFSET {offset} LIMIT {take}";

    /// <summary>ค่าคงที่ boolean</summary>
    /// <param name="d">dialect</param>
    /// <param name="value">ค่า</param>
    /// <returns>TRUE/FALSE หรือ 1/0</returns>
    public static string Bool(this ISqlDialect d, bool value) => d.IsSqlServer() ? (value ? "1" : "0") : (value ? "TRUE" : "FALSE");

    /// <summary>ให้ค่า NULL ขึ้นก่อนเมื่อเรียงจากน้อยไปมาก (SQL Server ทำแบบนี้อยู่แล้ว)</summary>
    /// <param name="d">dialect</param>
    /// <returns>SQL</returns>
    public static string NullsFirst(this ISqlDialect d) => d.IsSqlServer() ? string.Empty : " NULLS FIRST";

    /// <summary>
    /// เงื่อนไข "column อยู่ในชุด id" ที่ใช้ parameter เดียวเสมอ (ไม่ชนเพดาน 2,100 parameter ของ SQL Server)
    /// PostgreSQL: <c>= ANY(@p)</c> กับ array, SQL Server: <c>IN (SELECT value FROM OPENJSON(@p))</c> กับ JSON array
    /// </summary>
    /// <param name="d">dialect</param>
    /// <param name="column">คอลัมน์</param>
    /// <param name="parameter">ชื่อ parameter (ไม่มี @)</param>
    /// <param name="sqlType">ชนิดของค่า (bigint/int)</param>
    /// <returns>SQL</returns>
    public static string InSet(this ISqlDialect d, string column, string parameter, string sqlType = "bigint") => d.IsSqlServer()
        ? $"{column} IN (SELECT CAST([value] AS {sqlType}) FROM OPENJSON(@{parameter}))"
        : $"{column} = ANY(@{parameter})";

    /// <summary>ค่า parameter สำหรับ <see cref="InSet"/></summary>
    /// <typeparam name="T">ชนิด id</typeparam>
    /// <param name="d">dialect</param>
    /// <param name="values">ค่า</param>
    /// <returns>array (PostgreSQL) หรือ JSON (SQL Server)</returns>
    public static object SetParam<T>(this ISqlDialect d, IEnumerable<T> values)
    {
        T[] arr = values as T[] ?? values.ToArray();
        return d.IsSqlServer() ? JsonSerializer.Serialize(arr) : arr;
    }

    /// <summary>JSON ของแถวสำหรับ bulk operation บน SQL Server (OPENJSON ... WITH)</summary>
    /// <typeparam name="T">ชนิดแถว</typeparam>
    /// <param name="rows">แถว</param>
    /// <returns>JSON (ชื่อ property แบบ camelCase)</returns>
    public static string Rows<T>(IEnumerable<T> rows) => JsonSerializer.Serialize(rows, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
