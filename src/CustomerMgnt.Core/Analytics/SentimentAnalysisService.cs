using System.Collections.Frozen;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Services;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Analytics;

/// <summary>
/// วิเคราะห์อารมณ์ของข้อความ (ไทย + อังกฤษ) จากแชต อีเมล และรีวิว แบบ lexicon + กฎ (negation, intensifier, escalation)
/// <para>
/// เหตุผลที่ใช้ lexicon: ภาษาไทยไม่มีช่องว่างระหว่างคำ และไม่มีชุดข้อมูลฝึกของลูกค้าตั้งแต่วันแรก
/// วิธีนี้ทำงานได้ทันที อธิบายผลได้ (คืนคำที่พบ) เร็วมาก (O(n·k) ไม่ allocate ต่อคำ) และไม่ต้องพึ่ง service ภายนอก
/// </para>
/// </summary>
public sealed class SentimentAnalysisService
{
    // น้ำหนักคำ: บวก = พอใจ, ลบ = ไม่พอใจ (ค่าประมาณ -3..+3)
    private static readonly FrozenDictionary<string, double> Lexicon = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        // ไทย — บวก
        ["ขอบคุณ"] = 1.5, ["ดีมาก"] = 2.5, ["ดีเยี่ยม"] = 3, ["ประทับใจ"] = 2.5, ["พอใจ"] = 2, ["ชอบ"] = 1.5, ["รวดเร็ว"] = 1.5,
        ["เยี่ยม"] = 2.5, ["สุดยอด"] = 3, ["ถูกใจ"] = 2, ["แนะนำ"] = 1, ["ช่วยได้"] = 1.5, ["ใช้ง่าย"] = 1.5, ["คุ้ม"] = 1.5,
        ["ดี"] = 1, ["เรียบร้อย"] = 1, ["ปลื้ม"] = 2, ["ไว้ใจ"] = 1.5,
        // ไทย — ลบ
        ["แย่"] = -2.5, ["แย่มาก"] = -3, ["ห่วย"] = -3, ["ช้า"] = -1.5, ["ผิดหวัง"] = -2.5, ["ไม่พอใจ"] = -2.5, ["โกรธ"] = -3,
        ["รำคาญ"] = -2, ["เสีย"] = -1.5, ["พัง"] = -2, ["ล่ม"] = -2.5, ["ใช้ไม่ได้"] = -2.5, ["ไม่ตอบ"] = -2, ["รอนาน"] = -2,
        ["ร้องเรียน"] = -2.5, ["ยกเลิก"] = -2, ["คืนเงิน"] = -2, ["แพง"] = -1, ["หงุดหงิด"] = -2, ["ปัญหา"] = -1, ["บั๊ก"] = -1.5,
        ["ผิดพลาด"] = -1.5, ["ไม่ไหว"] = -2, ["เซ็ง"] = -2, ["ทนไม่ได้"] = -3, ["ไม่ประทับใจ"] = -2.5,
        // อังกฤษ — บวก
        ["thank"] = 1.5, ["thanks"] = 1.5, ["great"] = 2, ["excellent"] = 3, ["awesome"] = 2.5, ["love"] = 2.5, ["happy"] = 2,
        ["good"] = 1, ["fast"] = 1, ["helpful"] = 2, ["recommend"] = 1.5, ["perfect"] = 3, ["satisfied"] = 2, ["amazing"] = 3,
        // อังกฤษ — ลบ
        ["bad"] = -2, ["terrible"] = -3, ["awful"] = -3, ["slow"] = -1.5, ["angry"] = -3, ["disappointed"] = -2.5, ["broken"] = -2,
        ["down"] = -1, ["worst"] = -3, ["hate"] = -3, ["useless"] = -2.5, ["complaint"] = -2.5, ["refund"] = -2, ["cancel"] = -2,
        ["unacceptable"] = -3, ["frustrated"] = -2.5, ["bug"] = -1.5, ["error"] = -1.5, ["never again"] = -3, ["waiting"] = -1,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // คำที่ต้องรีบส่งต่อคนดูแลทันทีแม้คะแนนรวมไม่ต่ำมาก (ความเสี่ยงทางธุรกิจ/กฎหมาย)
    private static readonly string[] EscalationTerms = ["ฟ้อง", "ทนาย", "สคบ", "ยกเลิกสัญญา", "เปลี่ยนเจ้า", "ประจาน", "lawyer", "sue", "lawsuit", "terminate", "switch provider"];
    private static readonly string[] Negations = ["ไม่", "ไม่ได้", "not ", "no ", "never ", "don't ", "didn't ", "isn't ", "wasn't "];
    private static readonly string[] Intensifiers = ["มาก", "สุดๆ", "ที่สุด", "very", "really", "so ", "extremely"];

    private readonly double _alertThreshold;

    /// <summary>สร้างบริการ</summary>
    /// <param name="options">ค่าตั้ง (เกณฑ์แจ้งเตือน)</param>
    public SentimentAnalysisService(IOptions<CustomerMgntOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _alertThreshold = options.Value.NegativeSentimentAlertThreshold;
    }

    /// <summary>วิเคราะห์อารมณ์ของข้อความ</summary>
    /// <param name="text">ข้อความ (ยาวสุดที่วิเคราะห์ 20,000 ตัวอักษร)</param>
    /// <returns>คะแนน -1..1, ป้าย, ความมั่นใจ, คำที่พบ และควรแจ้งเตือนหรือไม่</returns>
    public SentimentDTO Analyze(string? text)
    {
        // Step 1: ข้อความว่าง = เป็นกลาง
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SentimentDTO(0, SentimentLabel.Neutral, 0, [], false);
        }

        // Step 2: normalize (ตัวพิมพ์เล็ก, จำกัดความยาวกัน CPU DoS)
        string s = (text.Length > 20_000 ? text[..20_000] : text).ToLowerInvariant();
        double sum = 0;
        int hits = 0;
        var signals = new List<string>();

        // Step 3: หา lexicon แบบ longest-match-first (ให้ "แย่มาก" ไม่ถูกนับซ้ำเป็น "แย่")
        Span<bool> covered = s.Length <= 4096 ? stackalloc bool[s.Length] : new bool[s.Length];
        foreach (KeyValuePair<string, double> term in Lexicon.OrderByDescending(t => t.Key.Length))
        {
            int idx = 0;
            while ((idx = s.IndexOf(term.Key, idx, StringComparison.Ordinal)) >= 0)
            {
                if (!covered[idx] && IsWordBoundary(s, idx, term.Key.Length))
                {
                    // Step 4: ปรับน้ำหนักด้วย negation (ก่อนหน้า) และ intensifier (ถัดไป)
                    double w = term.Value;
                    if (HasPrefix(s, idx, Negations) && !term.Key.StartsWith("ไม่", StringComparison.Ordinal))
                    {
                        w = -w * 0.8;
                    }

                    if (HasSuffix(s, idx + term.Key.Length, Intensifiers) || HasPrefix(s, idx, Intensifiers))
                    {
                        w *= 1.5;
                    }

                    sum += w;
                    hits++;
                    signals.Add(term.Key);
                    covered.Slice(idx, term.Key.Length).Fill(true);
                }

                idx += term.Key.Length;
            }
        }

        // Step 5: เครื่องหมายอัศเจรีย์ซ้ำๆ เพิ่มความเข้มของอารมณ์ที่มีอยู่
        int exclaims = s.Count(c => c == '!');
        if (exclaims >= 2 && sum != 0)
        {
            sum *= 1 + Math.Min(exclaims, 5) * 0.1;
        }

        // Step 6: บีบคะแนนเข้า -1..1 ด้วย tanh (ไม่ขึ้นกับความยาวข้อความมากเกินไป)
        double score = Math.Round(Math.Tanh(sum / 4.0), 4);
        bool escalate = EscalationTerms.Any(t => s.Contains(t, StringComparison.Ordinal));
        if (escalate)
        {
            signals.Add("escalation");
            score = Math.Min(score, -0.6);
        }

        SentimentLabel label = score <= -0.15 ? SentimentLabel.Negative : score >= 0.15 ? SentimentLabel.Positive : SentimentLabel.Neutral;
        double confidence = Math.Round(Math.Min(1.0, (Math.Abs(score) * 0.7) + (Math.Min(hits, 5) * 0.06)), 3);
        return new SentimentDTO(score, label, confidence, signals.Distinct().ToList(), escalate || score <= _alertThreshold);
    }

    /// <summary>ตรวจขอบคำ (ภาษาอังกฤษต้องไม่อยู่กลางคำ; ภาษาไทยไม่มีช่องว่างจึงยอมรับเสมอ)</summary>
    /// <param name="s">ข้อความ</param>
    /// <param name="start">ตำแหน่งเริ่ม</param>
    /// <param name="length">ความยาว</param>
    /// <returns>true ถ้าเป็นคำเต็ม</returns>
    private static bool IsWordBoundary(string s, int start, int length)
    {
        if (!char.IsAsciiLetter(s[start]))
        {
            return true;
        }

        bool left = start == 0 || !char.IsAsciiLetter(s[start - 1]);
        int end = start + length;
        bool right = end >= s.Length || !char.IsAsciiLetter(s[end]) || s[end] == 's';
        return left && right;
    }

    /// <summary>มีคำใดในรายการอยู่ก่อนตำแหน่ง (ภายใน 12 ตัวอักษร)</summary>
    private static bool HasPrefix(string s, int idx, string[] words)
    {
        int from = Math.Max(0, idx - 12);
        ReadOnlySpan<char> window = s.AsSpan(from, idx - from);
        foreach (string w in words)
        {
            if (window.EndsWith(w.AsSpan(), StringComparison.Ordinal) || window.EndsWith((w.TrimEnd() + " ").AsSpan(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>มีคำใดในรายการตามหลังตำแหน่ง</summary>
    private static bool HasSuffix(string s, int idx, string[] words)
    {
        ReadOnlySpan<char> rest = s.AsSpan(idx).TrimStart();
        foreach (string w in words)
        {
            if (rest.StartsWith(w.AsSpan().TrimEnd(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
