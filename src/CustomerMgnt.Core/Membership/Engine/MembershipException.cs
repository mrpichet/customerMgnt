using Common.Core.Exceptions;

namespace CustomerMgnt.Core.Membership.Engine;

/// <summary>
/// ข้อผิดพลาดทางธุรกิจของระบบสมาชิกที่มีรหัสเฉพาะ (Claude.MD §25) — Common แปลงเป็น ProblemDetails พร้อม <c>code</c> ให้อัตโนมัติ
/// ข้อความต้องปลอดภัยต่อการแสดงผู้ใช้
/// </summary>
public sealed class MembershipException : CommonException
{
    private readonly int _status;

    /// <summary>สร้าง exception</summary>
    /// <param name="status">HTTP status</param>
    /// <param name="code">รหัส เช่น insufficient_points</param>
    /// <param name="message">ข้อความ</param>
    public MembershipException(int status, string code, string message) : base(message, code)
    {
        _status = status;
    }

    /// <summary>ข้อมูลที่แนบกลับให้ผู้เรียก (เช่น quote ใหม่เมื่อ quote_changed)</summary>
    public object? Payload { get; init; }

    /// <inheritdoc />
    public override int StatusCode => _status;

    /// <summary>409 conflict พร้อมรหัส</summary>
    public static MembershipException Conflict(string code, string message) => new(409, code, message);

    /// <summary>422 พร้อมรหัส</summary>
    public static MembershipException Unprocessable(string code, string message) => new(422, code, message);

    /// <summary>410 gone พร้อมรหัส</summary>
    public static MembershipException Gone(string code, string message) => new(410, code, message);

    /// <summary>มีคนแก้ข้อมูลก่อน</summary>
    public static MembershipException VersionConflict() => new(409, "version_conflict", "The record was changed by someone else. Reload and try again.");

    /// <summary>เปลี่ยนสถานะไม่ได้</summary>
    public static MembershipException InvalidTransition(object from, object to) => new(409, "invalid_transition", $"Cannot change status from {from} to {to}.");
}

/// <summary>การบันทึกชนกัน (optimistic lock / unique key) — service ลองใหม่หรือแปลงเป็น 409</summary>
public sealed class MembershipConcurrencyException(string message) : Exception(message);

/// <summary>ตัวนับแคมเปญเกินจำกัดตอนบันทึก (Code = limit_reached / budget_exhausted)</summary>
public sealed class MembershipLimitException(string code) : Exception($"Campaign counter limit: {code}")
{
    /// <summary>รหัส error</summary>
    public string Code { get; } = code;
}
