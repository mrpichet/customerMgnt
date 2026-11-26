using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Data.Membership;

/// <summary>hash chain ของ audit log: hash = SHA-256(prevHash | at | actor | action | target | before | after) — แก้แถวใดแถวหนึ่งแล้ว chain ขาด</summary>
internal static class MembershipAuditChain
{
    /// <summary>ใส่ prevHash/hash ให้รายการ (ต่อจาก hash ล่าสุด)</summary>
    /// <param name="entry">รายการ</param>
    /// <param name="prevHash">hash ของรายการก่อนหน้า (null = รายการแรก)</param>
    public static void Seal(MembershipAuditDTO entry, string? prevHash)
    {
        entry.PrevHash = prevHash;
        string material = string.Join('|', prevHash ?? string.Empty, entry.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), entry.Actor, entry.ActorType,
            entry.Action, entry.Target, entry.RequestId ?? string.Empty, entry.BeforeMasked ?? string.Empty, entry.AfterMasked ?? string.Empty);
        entry.Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
