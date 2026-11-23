using Common.Core.Domain;
using Common.Core.Exceptions;

namespace CustomerMgnt.Core.Security;

/// <summary>ตัวช่วยตรวจสิทธิ์ใน Service layer (deny by default)</summary>
public static class AccessGuard
{
    /// <summary>บังคับว่าผู้เรียกต้องมีสิทธิ์</summary>
    /// <param name="user">ผู้เรียก</param>
    /// <param name="permission">สิทธิ์ที่ต้องมี (ดู <see cref="Permissions"/>)</param>
    /// <exception cref="UnauthenticatedException">ไม่มีข้อมูลผู้เรียก</exception>
    /// <exception cref="ForbiddenException">ไม่มีสิทธิ์</exception>
    public static void Require(UserContextDTO? user, string permission)
    {
        // Step 1: ต้องรู้ตัวผู้เรียกเสมอ
        if (user is null || string.IsNullOrEmpty(user.Subject))
        {
            throw new UnauthenticatedException();
        }

        // Step 2: ตรวจสิทธิ์ (admin ผ่านทุกสิทธิ์ใน UserContextDTO.Has)
        if (!user.Has(permission))
        {
            throw new ForbiddenException($"Missing permission '{permission}'.");
        }
    }
}
