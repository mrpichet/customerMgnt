using System.Globalization;
using System.Text.Json;
using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Membership.Web.Services;

/// <summary>โมเดลแก้ไขเงื่อนไข Segment บนหน้าจอ (แปลงไป/กลับ <see cref="SegmentRuleDTO"/>)</summary>
public sealed class SegmentRuleModel
{
    /// <summary>field ที่ใช้ได้ (ตรงกับ SegmentRuleEvaluator) และชนิด</summary>
    public static readonly IReadOnlyList<(string Field, string Label, string Kind)> Fields =
    [
        ("tierCode", "Tier", "text"), ("status", "สถานะสมาชิก", "text"), ("ageYears", "อายุ (ปี)", "number"), ("gender", "เพศ", "text"),
        ("province", "จังหวัด", "text"), ("pointsAvailable", "คะแนนคงเหลือ", "number"), ("spend12m", "ยอดซื้อ 12 เดือน", "number"),
        ("frequency12m", "จำนวนครั้งที่ซื้อ 12 เดือน", "number"), ("recencyDays", "วันนับจากซื้อล่าสุด", "number"),
        ("rfmR", "RFM: R (1-5)", "number"), ("rfmF", "RFM: F (1-5)", "number"), ("rfmM", "RFM: M (1-5)", "number"), ("marketingConsent", "ยินยอมรับข่าวสาร", "bool"),
    ];

    /// <summary>ตัวดำเนินการ</summary>
    public static readonly IReadOnlyList<(string Op, string Label)> Operators =
    [
        ("Eq", "เท่ากับ"), ("Ne", "ไม่เท่ากับ"), ("In", "อยู่ใน (คั่นด้วย ,)"), ("NotIn", "ไม่อยู่ใน (คั่นด้วย ,)"),
        ("Gt", "มากกว่า"), ("Gte", "มากกว่าหรือเท่ากับ"), ("Lt", "น้อยกว่า"), ("Lte", "น้อยกว่าหรือเท่ากับ"), ("Between", "ระหว่าง (จาก,ถึง)"),
    ];

    /// <summary>เป็นกลุ่ม</summary>
    public bool IsGroup { get; set; }

    /// <summary>And / Or</summary>
    public string Op { get; set; } = "And";

    /// <summary>ลูกของกลุ่ม</summary>
    public List<SegmentRuleModel> Items { get; set; } = [];

    /// <summary>field</summary>
    public string Field { get; set; } = "tierCode";

    /// <summary>ตัวดำเนินการ</summary>
    public string Operator { get; set; } = "Eq";

    /// <summary>ค่าที่พิมพ์</summary>
    public string? ValueText { get; set; }

    /// <summary>กลุ่มเปล่า</summary>
    /// <returns>กลุ่ม And</returns>
    public static SegmentRuleModel NewGroup() => new() { IsGroup = true, Items = [new SegmentRuleModel()] };

    /// <summary>แปลงจาก DTO</summary>
    /// <param name="r">DTO</param>
    /// <returns>โมเดล (ราก = กลุ่มเสมอ)</returns>
    public static SegmentRuleModel From(SegmentRuleDTO? r)
    {
        if (r is null || (r.Items is not { Count: > 0 } && r.Field is null))
        {
            return NewGroup();
        }

        SegmentRuleModel m = FromNode(r);
        return m.IsGroup ? m : new SegmentRuleModel { IsGroup = true, Items = [m] };
    }

    private static SegmentRuleModel FromNode(SegmentRuleDTO r) => r.Items is { Count: > 0 }
        ? new SegmentRuleModel { IsGroup = true, Op = r.Op ?? "And", Items = [.. r.Items.Select(FromNode)] }
        : new SegmentRuleModel { Field = r.Field ?? "tierCode", Operator = r.Operator ?? "Eq", ValueText = ValueToText(r.Value) };

    private static string? ValueToText(JsonElement? v) => v?.ValueKind switch
    {
        JsonValueKind.Array => string.Join(", ", v.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
        JsonValueKind.String => v.Value.GetString(),
        null or JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => v.Value.GetRawText(),
    };

    /// <summary>แปลงเป็น DTO</summary>
    /// <returns>DTO</returns>
    public SegmentRuleDTO ToDto()
    {
        if (IsGroup)
        {
            return new SegmentRuleDTO { Op = Op, Items = [.. Items.Select(i => i.ToDto())] };
        }

        string kind = Fields.FirstOrDefault(f => f.Field == Field).Kind ?? "text";
        bool list = Operator is "In" or "NotIn" or "Between";
        IEnumerable<string> parts = list ? (ValueText ?? string.Empty).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0) : [(ValueText ?? string.Empty).Trim()];
        object?[] values = [.. parts.Select(p => Convert(p, kind))];
        object? value = list ? values : values.FirstOrDefault();
        return new SegmentRuleDTO { Field = Field, Operator = Operator, Value = JsonSerializer.SerializeToElement(value) };
    }

    private static object? Convert(string s, string kind) => kind switch
    {
        "number" => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : s,
        "bool" => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s is "1" or "ใช่",
        _ => s,
    };
}
