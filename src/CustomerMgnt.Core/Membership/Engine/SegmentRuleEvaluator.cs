using System.Globalization;
using System.Text.Json;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Core.Membership.Engine;

/// <summary>
/// ประเมินเงื่อนไข segment (ต้นไม้ And/Or) กับข้อเท็จจริงของสมาชิก (<see cref="MemberFactsDTO"/>)
/// field และ operator เป็น whitelist เท่านั้น — ข้อเท็จจริงคำนวณด้วย SQL ที่เขียนตายตัว (ไม่ประกอบ SQL จากข้อความผู้ใช้)
/// </summary>
public static class SegmentRuleEvaluator
{
    /// <summary>field ที่ใช้ได้ → ชนิด (number | text | bool)</summary>
    public static IReadOnlyDictionary<string, string> Fields { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["tierCode"] = "text", ["status"] = "text", ["ageYears"] = "number", ["gender"] = "text", ["province"] = "text",
        ["pointsAvailable"] = "number", ["spend12m"] = "number", ["frequency12m"] = "number", ["recencyDays"] = "number",
        ["rfmR"] = "number", ["rfmF"] = "number", ["rfmM"] = "number", ["marketingConsent"] = "bool",
    };

    /// <summary>operator ที่ใช้ได้</summary>
    public static IReadOnlySet<string> Operators { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Eq", "Ne", "In", "NotIn", "Gt", "Gte", "Lt", "Lte", "Between" };

    /// <summary>ตรวจรูปแบบเงื่อนไข (ลึกไม่เกิน 5 ชั้น, ไม่เกิน 50 เงื่อนไข)</summary>
    /// <param name="rule">เงื่อนไข</param>
    /// <exception cref="ValidationException">ไม่ถูกต้อง</exception>
    public static void Validate(SegmentRuleDTO rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        int count = 0;
        Walk(rule, 0, ref count);
        ValidationException.ThrowIf(count > 50, "rule", "A segment may have at most 50 conditions.");
    }

    private static void Walk(SegmentRuleDTO node, int depth, ref int count)
    {
        ValidationException.ThrowIf(depth > 5, "rule", "Segment rules may be nested at most 5 levels.");
        if (node.Items is { Count: > 0 })
        {
            ValidationException.ThrowIf(!string.Equals(node.Op, "And", StringComparison.OrdinalIgnoreCase) && !string.Equals(node.Op, "Or", StringComparison.OrdinalIgnoreCase), "rule.op", "Group operator must be And or Or.");
            foreach (SegmentRuleDTO child in node.Items)
            {
                Walk(child, depth + 1, ref count);
            }

            return;
        }

        count++;
        ValidationException.ThrowIf(node.Field is null || !Fields.ContainsKey(node.Field), "rule.field", $"Unknown field '{node.Field}'.");
        ValidationException.ThrowIf(node.Operator is null || !Operators.Contains(node.Operator), "rule.operator", $"Unknown operator '{node.Operator}'.");
        ValidationException.ThrowIf(node.Value is null, "rule.value", "Value is required.");
        bool listOp = node.Operator is "In" or "NotIn" or "Between";
        ValidationException.ThrowIf(listOp && node.Value!.Value.ValueKind != JsonValueKind.Array, "rule.value", $"{node.Operator} needs an array value.");
        ValidationException.ThrowIf(node.Operator == "Between" && node.Value!.Value.GetArrayLength() != 2, "rule.value", "Between needs [from, to].");
    }

    /// <summary>สมาชิกเข้าเงื่อนไขหรือไม่</summary>
    /// <param name="rule">เงื่อนไข</param>
    /// <param name="f">ข้อเท็จจริง</param>
    /// <returns>true ถ้าเข้า</returns>
    public static bool Matches(SegmentRuleDTO rule, MemberFactsDTO f)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(f);
        if (rule.Items is { Count: > 0 })
        {
            return string.Equals(rule.Op, "Or", StringComparison.OrdinalIgnoreCase)
                ? rule.Items.Any(i => Matches(i, f))
                : rule.Items.All(i => Matches(i, f));
        }

        if (rule.Field is null || rule.Operator is null || rule.Value is null)
        {
            return true; // node ว่าง = ทุกคน
        }

        object? actual = rule.Field.ToLowerInvariant() switch
        {
            "tiercode" => f.TierCode,
            "status" => f.Status.ToString(),
            "ageyears" => f.AgeYears,
            "gender" => f.Gender.ToString(),
            "province" => f.Province,
            "pointsavailable" => f.PointsAvailable,
            "spend12m" => f.Spend12m,
            "frequency12m" => f.Frequency12m,
            "recencydays" => f.RecencyDays,
            "rfmr" => f.RfmR,
            "rfmf" => f.RfmF,
            "rfmm" => f.RfmM,
            "marketingconsent" => f.MarketingConsent,
            _ => null,
        };

        JsonElement v = rule.Value.Value;
        return Fields[rule.Field] switch
        {
            "number" => CompareNumber(actual is null ? null : Convert.ToDecimal(actual, CultureInfo.InvariantCulture), rule.Operator, v),
            "bool" => actual is bool b && CompareText(b ? "true" : "false", rule.Operator, v),
            _ => CompareText(actual as string, rule.Operator, v),
        };
    }

    private static decimal? Num(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) => d,
        _ => null,
    };

    private static string? Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => e.GetRawText(),
        _ => null,
    };

    private static bool CompareNumber(decimal? a, string op, JsonElement v)
    {
        if (a is null)
        {
            return op is "Ne" or "NotIn";
        }

        return op switch
        {
            "Eq" => Num(v) == a,
            "Ne" => Num(v) != a,
            "Gt" => a > Num(v),
            "Gte" => a >= Num(v),
            "Lt" => a < Num(v),
            "Lte" => a <= Num(v),
            "In" => v.EnumerateArray().Any(x => Num(x) == a),
            "NotIn" => v.EnumerateArray().All(x => Num(x) != a),
            "Between" => a >= Num(v[0]) && a <= Num(v[1]),
            _ => false,
        };
    }

    private static bool CompareText(string? a, string op, JsonElement v)
    {
        static bool Eq(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        return op switch
        {
            "Eq" => Eq(a, Text(v)),
            "Ne" => !Eq(a, Text(v)),
            "In" => v.ValueKind == JsonValueKind.Array && v.EnumerateArray().Any(x => Eq(a, Text(x))),
            "NotIn" => v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => !Eq(a, Text(x))),
            _ => false,
        };
    }

    /// <summary>คะแนน quintile 1-5 ของค่าในประชากร (ค่าสูง = 5; ใช้ invert สำหรับ recency)</summary>
    /// <param name="values">ค่าของทุกคน</param>
    /// <param name="invert">true = ค่าน้อยได้คะแนนสูง</param>
    /// <returns>ฟังก์ชันแปลงค่า → คะแนน</returns>
    public static Func<decimal, int> Quintile(IReadOnlyList<decimal> values, bool invert = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return _ => 1;
        }

        decimal[] sorted = [.. values.Order()];
        decimal[] cuts = [.. Enumerable.Range(1, 4).Select(k => sorted[Math.Min(sorted.Length - 1, sorted.Length * k / 5)])];
        return x =>
        {
            int q = 1 + cuts.Count(c => x >= c);
            q = Math.Clamp(q, 1, 5);
            return invert ? 6 - q : q;
        };
    }
}
