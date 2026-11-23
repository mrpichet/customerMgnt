using Microsoft.AspNetCore.Components;

using CustomerMgnt.Web.Services;

namespace CustomerMgnt.Web.Shared;

/// <summary>ฐานของหน้า: มี ApiClient, สถานะ loading/error และตัวช่วยรันงานพร้อมจับข้อผิดพลาด</summary>
public abstract class PageBase : ComponentBase
{
    /// <summary>API client</summary>
    [Inject] protected ApiClient Api { get; set; } = default!;

    /// <summary>สถานะผู้ใช้</summary>
    [Inject] protected AuthState Auth { get; set; } = default!;

    /// <summary>ข้อความผิดพลาดล่าสุด</summary>
    protected string? Error { get; set; }

    /// <summary>กำลังโหลด</summary>
    protected bool Busy { get; set; }

    /// <summary>รันงาน แสดงสถานะ busy และเก็บข้อความผิดพลาด (ไม่ให้หน้า crash)</summary>
    /// <param name="work">งาน</param>
    /// <returns>true = สำเร็จ</returns>
    protected async Task<bool> RunAsync(Func<Task> work)
    {
        Busy = true;
        Error = null;
        try
        {
            await work();
            return true;
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
            return false;
        }
        catch (HttpRequestException)
        {
            Error = "เชื่อมต่อเซิร์ฟเวอร์ไม่ได้";
            return false;
        }
        finally
        {
            Busy = false;
            StateHasChanged();
        }
    }
}
