using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Core.Domain;
using Common.Core.Security;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Security;

/// <summary>ค่าตั้งการปกป้องข้อมูลส่วนบุคคล (section <c>Privacy</c>)</summary>
public sealed class PrivacyOptions
{
    /// <summary>ชื่อ section</summary>
    public const string SectionName = "Privacy";
    /// <summary>คีย์ HMAC-SHA256 (Base64 ≥ 32 byte) สำหรับ blind index — ส่งผ่าน secret file เท่านั้น</summary>
    public string? BlindIndexKey { get; set; }
    /// <summary>อนุญาตคีย์ชั่วคราว (Development/test เท่านั้น — restart แล้ว blind index เดิมใช้ไม่ได้)</summary>
    public bool AllowEphemeralKey { get; set; }
}

/// <summary>
/// ปกป้องข้อมูลอ่อนไหวของลูกค้า (PDPA):
/// <list type="bullet">
/// <item>เข้ารหัสเลขผู้เสียภาษี/บัตรประชาชนด้วย Hybrid KEM (ECDH-P256 + ML-KEM-768 → AES-256-GCM) ของ Common — ทนต่อ quantum computer</item>
/// <item>สร้าง blind index (HMAC-SHA256) เพื่อค้นหา/ตรวจซ้ำโดยไม่ต้องถอดรหัส</item>
/// <item>ปิดบังค่าเมื่อแสดงผล</item>
/// </list>
/// </summary>
public sealed class PrivacyService
{
    private const string TaxIdContext = "customer.tax_id";
    private readonly PqcEnvelopeService _envelope;
    private readonly byte[] _blindKey;

    /// <summary>สร้างบริการ</summary>
    /// <param name="envelope">บริการเข้ารหัสแบบ PQC hybrid ของ Common</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <exception cref="InvalidOperationException">ไม่ได้ตั้งคีย์และไม่อนุญาตคีย์ชั่วคราว</exception>
    public PrivacyService(PqcEnvelopeService envelope, IOptions<PrivacyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _envelope = envelope;

        // Step 1: โหลดคีย์ blind index (fail fast ถ้า production ไม่ได้ตั้ง)
        PrivacyOptions o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.BlindIndexKey))
        {
            _blindKey = Convert.FromBase64String(o.BlindIndexKey.Trim());
            if (_blindKey.Length < 32)
            {
                throw new InvalidOperationException("Privacy:BlindIndexKey must be at least 32 bytes.");
            }
        }
        else if (o.AllowEphemeralKey)
        {
            _blindKey = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            throw new InvalidOperationException("Privacy:BlindIndexKey is not configured.");
        }
    }

    /// <summary>ทำให้เลขผู้เสียภาษีอยู่ในรูปมาตรฐาน (ตัวเลขล้วน) และตรวจรูปแบบ</summary>
    /// <param name="taxId">ค่าที่ผู้ใช้กรอก (มีขีด/ช่องว่างได้)</param>
    /// <returns>ตัวเลขล้วน</returns>
    /// <exception cref="Common.Core.Exceptions.ValidationException">รูปแบบไม่ถูกต้อง</exception>
    public static string NormalizeTaxId(string taxId)
    {
        // Step 1: เก็บเฉพาะตัวเลข
        var sb = new StringBuilder(taxId.Length);
        foreach (char c in taxId)
        {
            if (char.IsAsciiDigit(c))
            {
                sb.Append(c);
            }
            else if (c is not ('-' or ' '))
            {
                throw new Common.Core.Exceptions.ValidationException("Tax id may contain digits, spaces and dashes only.");
            }
        }

        // Step 2: เลขไทย 13 หลักต้องผ่าน checksum (mod 11), เลขต่างประเทศ 5-20 หลักยอมรับ
        string digits = sb.ToString();
        Common.Core.Exceptions.ValidationException.ThrowIf(digits.Length is < 5 or > 20, "taxId", "Tax id must have 5-20 digits.");
        if (digits.Length == 13)
        {
            int sum = 0;
            for (int i = 0; i < 12; i++)
            {
                sum += (digits[i] - '0') * (13 - i);
            }

            int check = (11 - (sum % 11)) % 10;
            Common.Core.Exceptions.ValidationException.ThrowIf(check != digits[12] - '0', "taxId", "Invalid Thai tax id checksum.");
        }

        return digits;
    }

    /// <summary>เข้ารหัสเลขผู้เสียภาษี (ผูก associated data กับ context เพื่อกันนำ ciphertext ไปใช้ที่ field อื่น)</summary>
    /// <param name="normalizedTaxId">เลขที่ normalize แล้ว</param>
    /// <returns>JSON ของ <see cref="EnvelopeDTO"/></returns>
    public string Encrypt(string normalizedTaxId) =>
        JsonSerializer.Serialize(_envelope.SealString(normalizedTaxId, TaxIdContext));

    /// <summary>ถอดรหัสเลขผู้เสียภาษี (ใช้เฉพาะงานที่จำเป็น เช่น ออกใบกำกับภาษี — ควร audit ทุกครั้ง)</summary>
    /// <param name="envelopeJson">JSON ของ <see cref="EnvelopeDTO"/></param>
    /// <returns>เลขผู้เสียภาษี</returns>
    public string Decrypt(string envelopeJson)
    {
        EnvelopeDTO env = JsonSerializer.Deserialize<EnvelopeDTO>(envelopeJson) ?? throw new CryptographicException("Invalid envelope.");
        return _envelope.OpenString(env, TaxIdContext);
    }

    /// <summary>Blind index ของค่า (HMAC-SHA256 hex) — ค่าเดียวกันได้ผลเดียวกันเสมอ แต่ย้อนกลับไม่ได้</summary>
    /// <param name="normalizedValue">ค่าที่ normalize แล้ว</param>
    /// <returns>hex 64 ตัว</returns>
    public string BlindIndex(string normalizedValue)
    {
        // Step 1: HMAC ด้วยคีย์ลับ (ไม่ใช้ SHA-256 เฉยๆ เพราะเลข 13 หลัก brute force ได้)
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_blindKey, Encoding.UTF8.GetBytes(normalizedValue), mac);
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>ปิดบังค่าให้เหลือ 4 ตัวท้าย</summary>
    /// <param name="value">ค่า</param>
    /// <returns>ค่าที่ปิดบังแล้ว</returns>
    public static string Mask(string value) =>
        value.Length <= 4 ? new string('*', value.Length) : new string('*', value.Length - 4) + value[^4..];
}
