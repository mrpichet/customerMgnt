using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CustomerMgnt.Core.MemberAnalytics.Engine;

/// <summary>
/// แบ่งกลุ่มควบคุมของแคมเปญแบบคงที่ (สูตรข้อ 4): bucket = 8 byte แรกของ SHA-256("{campaignCode}:{memberNo}") เป็นเลขไม่ติดลบ mod 10,000
/// กลุ่มควบคุม ⇔ bucket &lt; holdoutPct × 100 — ผลเดิมทุกครั้ง ทุกเครื่อง ไม่ต้องเก็บรายชื่อ
/// </summary>
public static class HoldoutAssigner
{
    /// <summary>bucket 0–9,999 ของสมาชิกในแคมเปญ</summary>
    /// <param name="campaignCode">รหัสแคมเปญ</param>
    /// <param name="memberNo">เลขสมาชิก</param>
    /// <returns>0–9,999</returns>
    public static int Bucket(string campaignCode, string memberNo)
    {
        ArgumentNullException.ThrowIfNull(campaignCode);
        ArgumentNullException.ThrowIfNull(memberNo);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{campaignCode.ToUpperInvariant()}:{memberNo.ToUpperInvariant()}"), hash);
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(hash[..8]);
        return (int)(v % 10_000UL);
    }

    /// <summary>สมาชิกอยู่กลุ่มควบคุมหรือไม่</summary>
    /// <param name="campaignCode">รหัสแคมเปญ</param>
    /// <param name="memberNo">เลขสมาชิก</param>
    /// <param name="holdoutPct">% กลุ่มควบคุม (0 = ไม่มี)</param>
    /// <returns>true = กลุ่มควบคุม</returns>
    public static bool IsControl(string campaignCode, string memberNo, int holdoutPct) =>
        holdoutPct > 0 && !string.IsNullOrEmpty(memberNo) && Bucket(campaignCode, memberNo) < holdoutPct * 100;
}
