using System.Security.Cryptography;
using System.Text;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Integration;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>ค่าตั้งของระบบสมาชิก (section <c>Membership</c>)</summary>
public sealed class MembershipOptions
{
    /// <summary>ชื่อ section</summary>
    public const string SectionName = "Membership";

    /// <summary>
    /// รหัส OTP คงที่สำหรับ Development เท่านั้น (ไม่ส่ง SMS) — Program.cs ปฏิเสธการตั้งค่านี้นอก Development
    /// </summary>
    public string? OtpFixedCodeForDevelopment { get; set; }

    /// <summary>เปิดงานเบื้องหลัง (outbox, หมดอายุ, Tier, segment)</summary>
    public bool WorkersEnabled { get; set; } = true;

    /// <summary>ข้อความ SMS ของ OTP ({code}, {minutes})</summary>
    public string OtpMessageTemplate { get; set; } = "รหัสยืนยันสมาชิก {code} ใช้ได้ {minutes} นาที ห้ามบอกรหัสนี้กับผู้อื่น";
}

/// <summary>
/// OTP 6 หลักทาง SMS ผ่าน MessageHub (กฎข้อ 2): อายุ/จำนวนครั้งผิด/จำนวนครั้งขอต่อเบอร์ ตามกฎธุรกิจ
/// เก็บเฉพาะ HMAC ของรหัส — ไม่เคยบันทึกรหัสลงฐานข้อมูลหรือ log
/// </summary>
public sealed class OtpService
{
    private readonly IMemberPrivacyDAO _dao;
    private readonly IMessageHubGateway _messageHub;
    private readonly PrivacyService _privacy;
    private readonly MembershipRulesProvider _rules;
    private readonly MembershipOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OtpService> _logger;

    /// <summary>สร้าง</summary>
    public OtpService(IMemberPrivacyDAO dao, IMessageHubGateway messageHub, PrivacyService privacy, MembershipRulesProvider rules,
        IOptions<MembershipOptions> options, TimeProvider clock, ILogger<OtpService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dao = dao;
        _messageHub = messageHub;
        _privacy = privacy;
        _rules = rules;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>ส่ง OTP ไปยังเบอร์</summary>
    /// <param name="purpose">วัตถุประสงค์ เช่น member:12</param>
    /// <param name="memberId">สมาชิก (ถ้ามี)</param>
    /// <param name="phone">เบอร์ที่ normalize แล้ว</param>
    /// <param name="payloadJson">ข้อมูลที่ใช้ตอนยืนยัน (เช่น เบอร์ใหม่แบบเข้ารหัส)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>challenge</returns>
    /// <exception cref="MembershipException">429 rate_limited</exception>
    public async Task<OtpChallengeDTO> IssueAsync(string purpose, long? memberId, string phone, string? payloadJson, CancellationToken ct)
    {
        // Step 1: จำกัดจำนวนครั้งต่อเบอร์ใน 10 นาที
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        string phoneHash = PhoneHash(phone);
        int recent = await _dao.CountOtpSinceAsync(phoneHash, now.AddMinutes(-10), ct).ConfigureAwait(false);
        if (recent >= rules.OtpPerPhonePer10Minutes)
        {
            throw new MembershipException(429, "rate_limited", "Too many OTP requests for this phone. Try again later.");
        }

        // Step 2: สร้างรหัสและเก็บเฉพาะ HMAC
        string id = MembershipEvents.NewId("otp");
        bool fixedCode = !string.IsNullOrEmpty(_options.OtpFixedCodeForDevelopment);
        string code = fixedCode ? _options.OtpFixedCodeForDevelopment! : RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var entity = new OtpChallengeEntity
        {
            Id = id,
            Purpose = purpose,
            MemberId = memberId,
            PhoneHash = phoneHash,
            PayloadJson = payloadJson,
            CodeHash = CodeHash(id, code),
            ExpiresAt = now.AddMinutes(rules.OtpTtlMinutes),
            CreatedAt = now,
        };
        await _dao.InsertOtpAsync(entity, ct).ConfigureAwait(false);

        // Step 3: ส่ง SMS (Development ที่ตั้งรหัสคงที่ = ไม่ส่ง)
        if (!fixedCode)
        {
            string body = _options.OtpMessageTemplate.Replace("{code}", code, StringComparison.Ordinal)
                .Replace("{minutes}", rules.OtpTtlMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            try
            {
                await _messageHub.SendToCustomerAsync(ChannelKind.Sms, phone, null, body, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "OTP SMS could not be sent for challenge {ChallengeId}", id);
                throw new DependencyUnavailableException("The SMS service is unavailable. Please try again.", ex);
            }
        }

        return new OtpChallengeDTO { ChallengeId = id, ExpiresAt = entity.ExpiresAt };
    }

    /// <summary>ตรวจ OTP</summary>
    /// <param name="challengeId">challenge</param>
    /// <param name="code">รหัสที่ผู้ใช้กรอก</param>
    /// <param name="purposePrefix">วัตถุประสงค์ที่คาดไว้ (prefix)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>challenge ที่ยืนยันแล้ว</returns>
    /// <exception cref="MembershipException">422 otp_invalid / otp_expired / otp_locked</exception>
    public async Task<OtpChallengeEntity> VerifyAsync(string challengeId, string code, string purposePrefix, CancellationToken ct)
    {
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        OtpChallengeEntity? c = await _dao.GetOtpAsync(challengeId, ct).ConfigureAwait(false);

        // Step 1: challenge ต้องตรงวัตถุประสงค์และยังไม่ถูกใช้
        if (c is null || !c.Purpose.StartsWith(purposePrefix, StringComparison.Ordinal) || c.VerifiedAt is not null)
        {
            throw MembershipException.Unprocessable("otp_invalid", "The OTP is invalid.");
        }

        if (c.Attempts >= rules.OtpMaxAttempts)
        {
            throw MembershipException.Unprocessable("otp_locked", "Too many wrong attempts. Request a new OTP.");
        }

        if (now >= c.ExpiresAt)
        {
            throw MembershipException.Unprocessable("otp_expired", "The OTP has expired. Request a new OTP.");
        }

        // Step 2: เทียบแบบเวลาคงที่
        bool ok = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeHash(c.Id, code.Trim())), Encoding.ASCII.GetBytes(c.CodeHash));
        if (!ok)
        {
            c.Attempts++;
            await _dao.UpdateOtpAsync(c, ct).ConfigureAwait(false);
            throw c.Attempts >= rules.OtpMaxAttempts
                ? MembershipException.Unprocessable("otp_locked", "Too many wrong attempts. Request a new OTP.")
                : MembershipException.Unprocessable("otp_invalid", "The OTP is invalid.");
        }

        c.VerifiedAt = now;
        await _dao.UpdateOtpAsync(c, ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>blind index ของเบอร์</summary>
    public string PhoneHash(string normalizedPhone) => _privacy.BlindIndex("phone:" + normalizedPhone);

    private string CodeHash(string id, string code) => _privacy.BlindIndex("otp:" + id + ":" + code);
}
