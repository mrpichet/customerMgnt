using System.Globalization;
using System.Text;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Membership.Web.Services;

/// <summary>แปลงตัวกรองร่วม (§28.1) ↔ query string — ตัวกรองอยู่ใน URL จึงแชร์ลิงก์ได้</summary>
public static class AnaQuery
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>query string ของตัวกรอง (ไม่มี ? นำหน้า) พร้อมพารามิเตอร์เพิ่มเติม</summary>
    public static string Of(AnalyticsFilterDTO f, params (string Key, object? Value)[] extra)
    {
        ArgumentNullException.ThrowIfNull(f);
        var sb = new StringBuilder();
        void Add(string k, object? v)
        {
            string? s = v switch
            {
                null => null,
                DateOnly d => d.ToString(DateFormat, CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString(),
            };
            if (string.IsNullOrEmpty(s)) return;
            sb.Append(sb.Length == 0 ? string.Empty : "&").Append(k).Append('=').Append(Uri.EscapeDataString(s));
        }

        Add("from", f.From);
        Add("to", f.To);
        if (f.Compare != CompareMode.PreviousPeriod) Add("compare", f.Compare);
        Add("granularity", f.Granularity);
        foreach (string v in f.Branch) Add("branch", v);
        foreach (SalesChannel v in f.Channel) Add("channel", v);
        foreach (string v in f.Brand) Add("brand", v);
        foreach (string v in f.Category) Add("category", v);
        foreach (string v in f.Tier) Add("tier", v);
        foreach (RegisteredChannel v in f.RegisteredChannel) Add("registeredChannel", v);
        foreach (string v in f.SourceCampaign) Add("sourceCampaign", v);
        foreach (string v in f.Campaign) Add("campaign", v);
        Add("savedFilterId", f.SavedFilterId);
        foreach ((string k, object? v) in extra)
        {
            if (v is System.Collections.IEnumerable list and not string)
            {
                foreach (object? item in list) Add(k, item);
            }
            else
            {
                Add(k, v);
            }
        }

        return sb.ToString();
    }

    /// <summary>อ่านตัวกรองจาก URL ปัจจุบัน</summary>
    public static AnalyticsFilterDTO Parse(string uri)
    {
        ILookup<string, string> p = Pairs(uri);
        IEnumerable<string> All(string k) => p[k].Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim());
        string? One(string k) => All(k).FirstOrDefault();
        List<T> Enums<T>(string k) where T : struct, Enum => [.. All(k).Select(x => Enum.TryParse(x, true, out T e) ? (T?)e : null).Where(x => x is not null).Select(x => x!.Value)];
        return new AnalyticsFilterDTO
        {
            From = DateOnly.TryParseExact(One("from"), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly from) ? from : null,
            To = DateOnly.TryParseExact(One("to"), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly to) ? to : null,
            Compare = Enum.TryParse(One("compare"), true, out CompareMode c) ? c : CompareMode.PreviousPeriod,
            Granularity = Enum.TryParse(One("granularity"), true, out Granularity g) ? g : null,
            Branch = [.. All("branch")],
            Channel = Enums<SalesChannel>("channel"),
            Brand = [.. All("brand")],
            Category = [.. All("category")],
            Tier = [.. All("tier")],
            RegisteredChannel = Enums<RegisteredChannel>("registeredChannel"),
            SourceCampaign = [.. All("sourceCampaign")],
            Campaign = [.. All("campaign")],
            SavedFilterId = Guid.TryParse(One("savedFilterId"), out Guid id) ? id : null,
        };
    }

    /// <summary>ค่าพารามิเตอร์อื่นใน URL (ไม่ใช่ตัวกรอง)</summary>
    public static string? Param(string uri, string key) => Pairs(uri)[key].FirstOrDefault();

    /// <summary>ค่าทั้งหมดของพารามิเตอร์ที่ซ้ำได้</summary>
    public static IReadOnlyList<string> Params(string uri, string key) => [.. Pairs(uri)[key].Where(v => v.Length > 0)];

    /// <summary>คู่ key=value ของ query string (key ไม่สนตัวพิมพ์)</summary>
    private static ILookup<string, string> Pairs(string uri)
    {
        int q = uri.IndexOf('?', StringComparison.Ordinal);
        if (q < 0) return Array.Empty<KeyValuePair<string, string>>().ToLookup(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        string query = uri[(q + 1)..];
        int hash = query.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0) query = query[..hash];
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            string k = eq < 0 ? part : part[..eq];
            string v = eq < 0 ? string.Empty : part[(eq + 1)..];
            return new KeyValuePair<string, string>(Uri.UnescapeDataString(k.Replace('+', ' ')), Uri.UnescapeDataString(v.Replace('+', ' ')));
        }).ToLookup(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
    }
}
