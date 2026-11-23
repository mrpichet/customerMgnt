using System.Reflection;
using CustomerMgnt.Core.Domain;

namespace CustomerMgnt.Data.InMemory;

/// <summary>
/// ฐานข้อมูลในหน่วยความจำ (ใช้กับ dev/demo/unit test เท่านั้น — ไม่ถาวร) ใช้ lock เดียวเพื่อความถูกต้องแบบง่าย
/// ทุกการอ่าน/เขียนคืนสำเนา (copy) ป้องกันผู้เรียกแก้ object ที่เก็บไว้โดยตรง เหมือนพฤติกรรมของฐานข้อมูลจริง
/// </summary>
public sealed class InMemoryDatabase
{
    private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private long _nextId;

    /// <summary>lock กลาง</summary>
    internal Lock Sync { get; } = new();

    /// <summary>ลูกค้า</summary>
    internal List<CustomerDTO> Customers { get; } = [];

    /// <summary>ความสัมพันธ์</summary>
    internal List<PartyRelationshipDTO> Relationships { get; } = [];

    /// <summary>ตัวตนในช่องทาง</summary>
    internal List<ChannelIdentityDTO> Identities { get; } = [];

    /// <summary>ห้องสนทนา</summary>
    internal List<ConversationDTO> Conversations { get; } = [];

    /// <summary>ข้อความ</summary>
    internal List<ChatMessageDTO> Messages { get; } = [];

    /// <summary>กิจกรรม</summary>
    internal List<ActivityDTO> Activities { get; } = [];

    /// <summary>คำขอ</summary>
    internal List<CustomerRequestDTO> Requests { get; } = [];

    /// <summary>เลขลำดับคำขอรายปี</summary>
    internal Dictionary<int, int> RequestSequences { get; } = [];

    /// <summary>โอกาสการขาย</summary>
    internal List<OpportunityDTO> Opportunities { get; } = [];

    /// <summary>ประวัติ stage</summary>
    internal List<StageHistoryDTO> StageHistory { get; } = [];

    /// <summary>นโยบาย SLA</summary>
    internal List<SlaPolicyDTO> SlaPolicies { get; } = [];

    /// <summary>ลิงก์ภายนอก</summary>
    internal List<ExternalLinkDTO> Links { get; } = [];

    /// <summary>พฤติกรรม</summary>
    internal List<EngagementEventDTO> Events { get; } = [];

    /// <summary>สัญญา</summary>
    internal List<ContractDTO> Contracts { get; } = [];

    /// <summary>แคตตาล็อก</summary>
    internal Dictionary<string, CatalogItemDTO> Catalog { get; } = new(StringComparer.Ordinal);

    /// <summary>โปรไฟล์ผู้ใช้</summary>
    internal Dictionary<string, UserProfileDTO> Profiles { get; } = new(StringComparer.Ordinal);

    /// <summary>สร้างฐานข้อมูลพร้อมนโยบาย SLA มาตรฐาน (เหมือน migration 001)</summary>
    public InMemoryDatabase()
    {
        long policyId = NextId();
        SlaPolicies.Add(new SlaPolicyDTO
        {
            Id = policyId,
            Name = "Standard SLA",
            Description = "Default policy for customers without a specific agreement",
            ValidFrom = new DateOnly(2020, 1, 1),
            Version = 1,
            Levels =
            [
                new() { Id = NextId(), PolicyId = policyId, Code = "S1", Name = "Critical", Description = "Production down, no workaround", Rank = 1, ResponseMinutes = 30, ResolutionMinutes = 240, BusinessHoursOnly = false, IssuePriority = "Immediate" },
                new() { Id = NextId(), PolicyId = policyId, Code = "S2", Name = "High", Description = "Major function impaired", Rank = 2, ResponseMinutes = 60, ResolutionMinutes = 960, IssuePriority = "Urgent" },
                new() { Id = NextId(), PolicyId = policyId, Code = "S3", Name = "Medium", Description = "Partial impact with workaround", Rank = 3, ResponseMinutes = 240, ResolutionMinutes = 2400, IssuePriority = "High" },
                new() { Id = NextId(), PolicyId = policyId, Code = "S4", Name = "Low", Description = "Minor issue or question", Rank = 4, ResponseMinutes = 480, ResolutionMinutes = 4800, IssuePriority = "Normal" },
            ],
        });
    }

    /// <summary>รหัสถัดไป (ไม่ซ้ำทั้งฐานข้อมูล)</summary>
    /// <returns>id</returns>
    internal long NextId() => Interlocked.Increment(ref _nextId);

    /// <summary>สำเนาแบบตื้น (property ที่เป็น list ต้อง copy เพิ่มเองที่ผู้เรียก)</summary>
    /// <typeparam name="T">ชนิด</typeparam>
    /// <param name="value">ต้นฉบับ</param>
    /// <returns>สำเนา</returns>
    internal static T Copy<T>(T value) where T : class => (T)MemberwiseCloneMethod.Invoke(value, null)!;

    /// <summary>ชื่อแสดงของลูกค้า (ต้องถือ lock อยู่แล้ว)</summary>
    /// <param name="id">รหัสลูกค้า</param>
    /// <returns>ชื่อหรือ null</returns>
    internal string? CustomerName(long? id) => id is null ? null : Customers.Find(c => c.Id == id)?.DisplayName;
}
