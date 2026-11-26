using CustomerMgnt.Membership.Web.Services;
using Microsoft.AspNetCore.Components;
using WebCommon.UI.Auth;
using WebCommon.UI.Services;

namespace CustomerMgnt.Membership.Web.Shared;

/// <summary>ฐานของหน้าในระบบสมาชิก: service ที่ใช้บ่อย + ตัวช่วยโหลด/บันทึกที่แสดงข้อความผิดพลาดแบบเดียวกัน</summary>
public abstract class MembershipPage : ComponentBase
{
    /// <summary>API client</summary>
    [Inject] protected MembershipApiClient Api { get; set; } = default!;

    /// <summary>นำทาง</summary>
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    /// <summary>ข้อความแจ้งเตือน</summary>
    [Inject] protected ToastService Toasts { get; set; } = default!;

    /// <summary>ตรวจสิทธิ์</summary>
    [Inject] protected PermissionService Perms { get; set; } = default!;

    /// <summary>ข้อความผิดพลาดตอนโหลด (null = ไม่มี)</summary>
    protected string? LoadError { get; set; }

    /// <summary>กำลังทำงาน</summary>
    protected bool Busy { get; set; }

    /// <summary>โหลดข้อมูล เก็บข้อความผิดพลาดใน <see cref="LoadError"/></summary>
    /// <param name="load">งานโหลด</param>
    /// <returns>true เมื่อสำเร็จ</returns>
    protected async Task<bool> LoadSafeAsync(Func<Task> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        LoadError = null;
        try
        {
            await load();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoadError = Text.Error(ex);
            return false;
        }
    }

    /// <summary>ทำงานเขียนข้อมูล แสดง toast สำเร็จ/ผิดพลาด แล้วนำทางต่อ (ถ้าระบุ)</summary>
    /// <param name="action">งาน</param>
    /// <param name="success">ข้อความสำเร็จ</param>
    /// <param name="navigateTo">หน้าถัดไป</param>
    /// <returns>true เมื่อสำเร็จ</returns>
    protected async Task<bool> SaveSafeAsync(Func<Task> action, string success, string? navigateTo = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        Busy = true;
        try
        {
            await action();
            Toasts.Success(success);
            if (navigateTo is not null)
            {
                Nav.NavigateTo(navigateTo);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Toasts.Error(Text.Error(ex));
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>โหลดรายการแบบไม่บังคับ (ไม่มีสิทธิ์/ผิดพลาด = รายการว่าง)</summary>
    /// <typeparam name="T">ชนิดข้อมูล</typeparam>
    /// <param name="load">งานโหลด</param>
    /// <returns>ผลหรือรายการว่าง</returns>
    protected static async Task<IReadOnlyList<T>> OptionalAsync<T>(Func<Task<List<T>>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        try { return await load(); }
        catch (ApiException) { return []; }
    }

    /// <summary>ตัดช่องว่าง ค่าว่าง = null</summary>
    /// <param name="s">ข้อความ</param>
    /// <returns>ค่าที่ทำความสะอาดแล้ว</returns>
    protected static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
