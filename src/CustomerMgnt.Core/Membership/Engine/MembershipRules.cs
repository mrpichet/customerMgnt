using System.Globalization;
using System.Text;
using System.Text.Json;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.Domain;

namespace CustomerMgnt.Core.Membership.Engine;

/// <summary>กฎของสมาชิก: รูปแบบเบอร์/อีเมล, การ mask, state machine (spec 3.1)</summary>
public static class MemberRules
{
    private static readonly Dictionary<MemberStatus, MemberStatus[]> Transitions = new()
    {
        [MemberStatus.PendingVerification] = [MemberStatus.Active, MemberStatus.Closed],
        [MemberStatus.Active] = [MemberStatus.Suspended, MemberStatus.Inactive, MemberStatus.Closed],
        [MemberStatus.Suspended] = [MemberStatus.Active, MemberStatus.Closed],
        [MemberStatus.Inactive] = [MemberStatus.Active, MemberStatus.Closed],
        [MemberStatus.Closed] = [],
    };

    /// <summary>เปลี่ยนสถานะได้หรือไม่</summary>
    /// <param name="from">สถานะเดิม</param>
    /// <param name="to">สถานะใหม่</param>
    /// <returns>true ถ้าได้</returns>
    public static bool CanTransition(MemberStatus from, MemberStatus to) => Transitions[from].Contains(to);

    /// <summary>ทำเบอร์มือถือไทยให้เป็นรูปมาตรฐาน 0XXXXXXXXX (+66 → 0)</summary>
    /// <param name="phone">เบอร์ที่กรอก</param>
    /// <returns>ตัวเลข 9-10 หลัก</returns>
    /// <exception cref="ValidationException">รูปแบบไม่ถูกต้อง</exception>
    public static string NormalizePhone(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);
        var sb = new StringBuilder();
        foreach (char ch in phone.Trim())
        {
            if (char.IsAsciiDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch is not (' ' or '-' or '(' or ')' or '+' or '.'))
            {
                throw new ValidationException("Phone may contain digits only.", new Dictionary<string, string[]> { ["phone"] = ["Phone may contain digits only."] });
            }
        }

        string digits = sb.ToString();
        if (digits.StartsWith("66", StringComparison.Ordinal) && digits.Length is 11)
        {
            digits = "0" + digits[2..];
        }

        ValidationException.ThrowIf(digits.Length is < 9 or > 10 || digits[0] != '0', "phone", "Phone must be a Thai number of 9-10 digits starting with 0.");
        return digits;
    }

    /// <summary>ทำอีเมลเป็นตัวพิมพ์เล็กและตัดช่องว่าง</summary>
    /// <param name="email">อีเมล</param>
    /// <returns>อีเมลมาตรฐาน หรือ null</returns>
    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        string e = email.Trim().ToLowerInvariant();
        int at = e.IndexOf('@', StringComparison.Ordinal);
        ValidationException.ThrowIf(at <= 0 || at == e.Length - 1 || e.Contains(' ', StringComparison.Ordinal), "email", "Invalid email.");
        return e;
    }

    /// <summary>mask เบอร์ เช่น 081-xxx-5678</summary>
    /// <param name="last4">4 ตัวท้าย</param>
    /// <param name="prefix">3 ตัวแรก (ถ้ามี)</param>
    /// <returns>ข้อความ</returns>
    public static string? MaskPhone(string? last4, string? prefix = null) =>
        last4 is null ? null : $"{prefix ?? "0xx"}-xxx-{last4}";

    /// <summary>mask อีเมล เช่น s***@mail.com</summary>
    /// <param name="email">อีเมล</param>
    /// <returns>ข้อความ</returns>
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        int at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : email[0] + "***" + email[at..];
    }

    /// <summary>ชื่อแบบย่อ: ชื่อ + อักษรแรกของนามสกุล</summary>
    /// <param name="first">ชื่อ</param>
    /// <param name="last">นามสกุล</param>
    /// <returns>เช่น "สมชาย ใ."</returns>
    public static string ShortName(string first, string last) =>
        string.IsNullOrEmpty(last) ? first : $"{first} {StringInfo.GetNextTextElement(last, 0)}.";

    /// <summary>อายุ (ปี) จากปีเกิดและเดือน-วันเกิด</summary>
    /// <param name="birthYear">ปีเกิด</param>
    /// <param name="monthDay">MM-dd</param>
    /// <param name="today">วันนี้</param>
    /// <returns>อายุ หรือ null</returns>
    public static int? Age(int? birthYear, string? monthDay, DateOnly today)
    {
        if (birthYear is not int y)
        {
            return null;
        }

        int age = today.Year - y;
        if (monthDay is { Length: 5 } && string.CompareOrdinal(today.ToString("MM-dd", CultureInfo.InvariantCulture), monthDay) < 0)
        {
            age--;
        }

        return age;
    }
}

/// <summary>กฎของแคมเปญ: state machine (spec 3.2) และ field ที่แก้แล้วต้องอนุมัติใหม่ (กฎข้อ 9)</summary>
public static class CampaignRules
{
    private static readonly Dictionary<CampaignStatus, CampaignStatus[]> Transitions = new()
    {
        [CampaignStatus.Draft] = [CampaignStatus.PendingApproval, CampaignStatus.Cancelled],
        [CampaignStatus.PendingApproval] = [CampaignStatus.Approved, CampaignStatus.Rejected, CampaignStatus.Draft],
        [CampaignStatus.Approved] = [CampaignStatus.Scheduled, CampaignStatus.Active, CampaignStatus.Cancelled],
        [CampaignStatus.Scheduled] = [CampaignStatus.Active, CampaignStatus.PendingApproval, CampaignStatus.Cancelled],
        [CampaignStatus.Active] = [CampaignStatus.Paused, CampaignStatus.Ended, CampaignStatus.Cancelled, CampaignStatus.PendingApproval],
        [CampaignStatus.Paused] = [CampaignStatus.Active, CampaignStatus.Ended, CampaignStatus.Cancelled],
        [CampaignStatus.Rejected] = [CampaignStatus.Draft],
        [CampaignStatus.Ended] = [],
        [CampaignStatus.Cancelled] = [],
    };

    /// <summary>เปลี่ยนสถานะได้หรือไม่</summary>
    /// <param name="from">เดิม</param>
    /// <param name="to">ใหม่</param>
    /// <returns>true ถ้าได้</returns>
    public static bool CanTransition(CampaignStatus from, CampaignStatus to) => Transitions[from].Contains(to);

    /// <summary>
    /// การแก้ไขนี้ต้องอนุมัติใหม่หรือไม่: แก้เงื่อนไข รางวัล งบ ช่วงเวลา กลุ่มเป้าหมาย stacking จำกัดสิทธิ = ต้อง, แก้ชื่อ/คำอธิบาย = ไม่ต้อง
    /// </summary>
    /// <param name="before">ก่อนแก้</param>
    /// <param name="after">หลังแก้</param>
    /// <returns>true ถ้าต้องอนุมัติใหม่</returns>
    public static bool RequiresReapproval(CampaignDTO before, CampaignDTO after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        static string Key(CampaignDTO c) => JsonSerializer.Serialize(new
        {
            c.StartAt, c.EndAt, c.RewardType, c.Reward, c.Level, c.RequiresCoupon, c.Conditions, c.Targets, c.StackMode, c.ExclusionGroup, c.Priority, c.Limits,
        });
        return Key(before) != Key(after);
    }

    /// <summary>จำนวนขั้นอนุมัติ (2 ขั้นเมื่องบเกินเกณฑ์)</summary>
    /// <param name="c">แคมเปญ</param>
    /// <param name="rules">กฎธุรกิจ</param>
    /// <returns>1 หรือ 2</returns>
    public static int ApprovalLevels(CampaignDTO c, MembershipRulesDTO rules)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(rules);
        return c.Limits.Budget is decimal b && b > rules.SecondLevelBudget ? 2 : Math.Clamp(rules.ApprovalLevels, 1, 2);
    }

    /// <summary>ตรวจความสมบูรณ์ของค่ารางวัลตามชนิด</summary>
    /// <param name="c">แคมเปญ</param>
    /// <exception cref="ValidationException">ค่าไม่ครบ</exception>
    public static void Validate(CampaignDTO c)
    {
        ArgumentNullException.ThrowIfNull(c);
        ValidationException.ThrowIf(c.EndAt <= c.StartAt, "endAt", "End must be after start.");
        CampaignRewardDTO r = c.Reward;
        (bool ok, string field) = c.RewardType switch
        {
            RewardType.PercentDiscount => (r.Percent is > 0 and <= 100, "reward.percent"),
            RewardType.AmountDiscount => (r.Amount is > 0, "reward.amount"),
            RewardType.BuyXGetY => (r.BuyQty is > 0 && r.GetQty is > 0, "reward.buyQty"),
            RewardType.SpendThreshold => (r.Threshold is > 0 && r.Amount is > 0, "reward.threshold"),
            RewardType.BonusPoints => (r.BonusPoints is > 0, "reward.bonusPoints"),
            RewardType.PointMultiplier => (r.Multiplier is > 1, "reward.multiplier"),
            RewardType.IssueCoupon => (r.CouponBatchId is > 0, "reward.couponBatchId"),
            _ => (false, "rewardType"),
        };
        ValidationException.ThrowIf(!ok, field, $"Reward value is required for {c.RewardType}.");
        ValidationException.ThrowIf(c.StackMode == StackMode.Group && string.IsNullOrWhiteSpace(c.ExclusionGroup), "exclusionGroup", "Exclusion group is required for Group stacking.");
        ValidationException.ThrowIf(!c.Targets.AllMembers && c.Targets.TierCodes.Count == 0 && c.Targets.SegmentIds.Count == 0, "targets", "Choose all members, tiers or segments.");
    }
}

/// <summary>Tier (spec 2.8): ขึ้นทันทีเมื่อถึงเกณฑ์ (ข้ามได้หลายระดับ), ครบรอบตรวจคง/ลด 1 ระดับ</summary>
public static class TierRules
{
    /// <summary>Tier เริ่มต้น (rank ต่ำสุดที่ใช้งาน)</summary>
    /// <param name="tiers">Tier ทั้งหมด</param>
    /// <returns>Tier หรือ null</returns>
    public static TierDTO? Initial(IEnumerable<TierDTO> tiers) => tiers.Where(t => t.IsActive).OrderBy(t => t.Rank).FirstOrDefault();

    /// <summary>Tier ที่ควรขึ้นจากยอดซื้อ (null = ไม่ขึ้น)</summary>
    /// <param name="tiers">Tier ทั้งหมด</param>
    /// <param name="currentCode">Tier ปัจจุบัน</param>
    /// <param name="qualifyingSpend">ยอดซื้อในรอบ</param>
    /// <returns>Tier ใหม่ที่สูงกว่า หรือ null</returns>
    public static TierDTO? Upgrade(IEnumerable<TierDTO> tiers, string? currentCode, decimal qualifyingSpend)
    {
        List<TierDTO> active = [.. tiers.Where(t => t.IsActive).OrderBy(t => t.Rank)];
        int currentRank = active.FirstOrDefault(t => string.Equals(t.Code, currentCode, StringComparison.OrdinalIgnoreCase))?.Rank ?? 0;
        return active.Where(t => t.Rank > currentRank && qualifyingSpend >= t.UpgradeThreshold).OrderByDescending(t => t.Rank).FirstOrDefault();
    }

    /// <summary>ผลตรวจครบรอบ: คง หรือ ลด 1 ระดับ</summary>
    /// <param name="tiers">Tier ทั้งหมด</param>
    /// <param name="currentCode">Tier ปัจจุบัน</param>
    /// <param name="qualifyingSpend">ยอดซื้อในรอบ</param>
    /// <returns>(Tier หลังตรวจ, เหตุผล Retain/Downgrade/Upgrade)</returns>
    public static (TierDTO? Tier, string Reason) Review(IEnumerable<TierDTO> tiers, string? currentCode, decimal qualifyingSpend)
    {
        List<TierDTO> active = [.. tiers.Where(t => t.IsActive).OrderBy(t => t.Rank)];
        TierDTO? up = Upgrade(active, currentCode, qualifyingSpend);
        if (up is not null)
        {
            return (up, "Upgrade");
        }

        TierDTO? current = active.FirstOrDefault(t => string.Equals(t.Code, currentCode, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            return (Initial(active), "Initial");
        }

        if (qualifyingSpend >= current.RetainThreshold)
        {
            return (current, "Retain");
        }

        TierDTO? lower = active.Where(t => t.Rank < current.Rank).OrderByDescending(t => t.Rank).FirstOrDefault();
        return lower is null ? (current, "Retain") : (lower, "Downgrade");
    }
}
