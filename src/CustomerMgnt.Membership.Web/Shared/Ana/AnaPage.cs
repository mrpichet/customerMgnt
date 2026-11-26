using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Membership.Web.Services;

namespace CustomerMgnt.Membership.Web.Shared.Ana;

/// <summary>
/// ฐานของหน้าวิเคราะห์ที่มีตัวกรองร่วม: ตัวกรองอ่านจาก URL ทุกครั้งที่ query เปลี่ยน แล้วโหลดใหม่ — กด "ใช้ตัวกรอง" = เปลี่ยน URL (แชร์ลิงก์ได้)
/// </summary>
public abstract class AnaPage : MembershipPage
{
    private string? _lastUri;

    /// <summary>ตัวกรองปัจจุบัน (จาก URL)</summary>
    protected AnalyticsFilterDTO Filter { get; private set; } = new();

    /// <summary>รหัสหน้าจอ (ใช้กับตัวกรองที่บันทึก)</summary>
    protected abstract string PageCode { get; }

    /// <summary>โหลดข้อมูลตามตัวกรอง</summary>
    protected abstract Task LoadAsync();

    /// <summary>ค่าพารามิเตอร์อื่นใน URL</summary>
    protected string? Param(string key) => AnaQuery.Param(Nav.Uri, key);

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (_lastUri == Nav.Uri)
        {
            return;
        }

        _lastUri = Nav.Uri;
        Filter = AnaQuery.Parse(Nav.Uri);
        Busy = true;
        try
        {
            await LoadAsync();
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>path ของหน้านี้ (ไม่มี query)</summary>
    protected string Path
    {
        get
        {
            string rel = Nav.ToBaseRelativePath(Nav.Uri);
            int q = rel.IndexOf('?', StringComparison.Ordinal);
            return q < 0 ? rel : rel[..q];
        }
    }

    /// <summary>ใช้ตัวกรองใหม่ (เปลี่ยน URL)</summary>
    protected void Apply(AnalyticsFilterDTO filter, params (string Key, object? Value)[] extra)
    {
        string q = AnaQuery.Of(filter, extra);
        Nav.NavigateTo(q.Length == 0 ? Path : $"{Path}?{q}");
    }

    /// <summary>query string ของตัวกรองปัจจุบัน + พารามิเตอร์เพิ่ม</summary>
    protected string Q(params (string Key, object? Value)[] extra) => AnaQuery.Of(Filter, extra);

    /// <summary>ไปหน้า drill-down ของ metric ด้วยตัวกรองปัจจุบัน</summary>
    protected void Drill(string? metric)
    {
        if (!string.IsNullOrEmpty(metric))
        {
            Nav.NavigateTo($"analytics/drilldown?{Q(("metric", metric), ("back", Path))}");
        }
    }

    /// <summary>ข้อมูลกำกับผลลัพธ์</summary>
    protected static AnaMeta Meta<T>(AnalyticsResultDTO<T>? r) => r is null ? new AnaMeta(false, null, false, false, null) : new AnaMeta(r.Stale, r.DataAsOf, r.ScopeEmpty, r.ScopeClamped, r.AppliedFilter);
}

/// <summary>ข้อมูลกำกับผลลัพธ์ของ Analytics API</summary>
public sealed record AnaMeta(bool Stale, DateTimeOffset? DataAsOf, bool ScopeEmpty, bool ScopeClamped, AppliedFilterDTO? Applied);
