using CustomerMgnt.Membership.Web.Services;

namespace CustomerMgnt.Membership.Web.Shared;

/// <summary>ฐานของหน้า Member Portal: ถ้าบัญชียังไม่ได้สมัครสมาชิก (404 not_registered) พาไปหน้าสมัคร</summary>
public abstract class PortalPage : MembershipPage
{
    /// <summary>โหลดข้อมูล; ยังไม่สมัคร = ไปหน้าสมัคร</summary>
    /// <param name="load">งานโหลด</param>
    /// <returns>true เมื่อสำเร็จ</returns>
    protected async Task<bool> PortalLoadAsync(Func<Task> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        LoadError = null;
        try
        {
            await load();
            return true;
        }
        catch (ApiException ex) when (ex.Code == "not_registered")
        {
            Nav.NavigateTo("portal/register", replace: true);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoadError = Text.Error(ex);
            return false;
        }
    }
}
