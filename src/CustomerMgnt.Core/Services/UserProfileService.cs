using Common.Core.Domain;
using Common.Core.Exceptions;
using Common.Core.Security;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// โปรไฟล์ผู้ใช้ภายใน: ผูก subject ของ AuthServer กับ user id ของ MessageHub
/// (จำเป็นสำหรับสร้างนัดหมาย/แจ้งเตือนผ่าน MessageHub ในนามผู้ใช้)
/// </summary>
public sealed class UserProfileService
{
    private readonly IUserProfileDAO _profiles;

    /// <summary>สร้างบริการ</summary>
    /// <param name="profiles">DAO โปรไฟล์</param>
    public UserProfileService(IUserProfileDAO profiles) => _profiles = profiles;

    /// <summary>โปรไฟล์ของผู้เรียก (ยังไม่เคยตั้ง → คืนค่าตั้งต้นจาก token)</summary>
    /// <param name="user">ผู้เรียก (ต้องยืนยันตัวตน)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>โปรไฟล์</returns>
    public async Task<UserProfileDTO> GetMineAsync(UserContextDTO user, CancellationToken ct = default)
    {
        // Step 1: ต้องรู้ตัวผู้เรียก
        if (user is null || string.IsNullOrEmpty(user.Subject)) throw new UnauthenticatedException();

        // Step 2: อ่านหรือสร้างค่าตั้งต้น
        return await _profiles.GetAsync(user.Subject, ct).ConfigureAwait(false) ?? new UserProfileDTO { Subject = user.Subject, DisplayName = user.UserName };
    }

    /// <summary>บันทึกโปรไฟล์ของผู้เรียกเอง (แก้ของคนอื่นไม่ได้)</summary>
    /// <param name="user">ผู้เรียก</param>
    /// <param name="displayName">ชื่อแสดง</param>
    /// <param name="messageHubUserId">user id ใน MessageHub</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>โปรไฟล์หลังบันทึก</returns>
    public async Task<UserProfileDTO> UpdateMineAsync(UserContextDTO user, string displayName, Guid? messageHubUserId, CancellationToken ct = default)
    {
        // Step 1: ต้องรู้ตัวผู้เรียก + ตรวจค่า
        if (user is null || string.IsNullOrEmpty(user.Subject)) throw new UnauthenticatedException();
        ValidationException.ThrowIf(messageHubUserId == Guid.Empty, "messageHubUserId", "MessageHub user id cannot be empty.");

        // Step 2: บันทึก
        var profile = new UserProfileDTO { Subject = user.Subject, DisplayName = InputGuard.Required(displayName, "displayName", 200), MessageHubUserId = messageHubUserId };
        await _profiles.UpsertAsync(profile, ct).ConfigureAwait(false);
        return profile;
    }

    /// <summary>ผู้ดูแลตั้งโปรไฟล์ให้ผู้ใช้อื่น</summary>
    /// <param name="user">ผู้เรียก (admin)</param>
    /// <param name="profile">โปรไฟล์</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>โปรไฟล์หลังบันทึก</returns>
    public async Task<UserProfileDTO> UpsertAsync(UserContextDTO user, UserProfileDTO profile, CancellationToken ct = default)
    {
        // Step 1: เฉพาะ admin
        if (user is null || string.IsNullOrEmpty(user.Subject)) throw new UnauthenticatedException();
        if (!user.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase)) throw new ForbiddenException("Only administrators can change other users' profiles.");
        ArgumentNullException.ThrowIfNull(profile);

        // Step 2: ตรวจค่าแล้วบันทึก
        profile.Subject = InputGuard.Required(profile.Subject, "subject", 128);
        profile.DisplayName = InputGuard.Required(profile.DisplayName, "displayName", 200);
        await _profiles.UpsertAsync(profile, ct).ConfigureAwait(false);
        return profile;
    }
}
