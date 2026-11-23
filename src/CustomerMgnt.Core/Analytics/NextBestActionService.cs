using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;

namespace CustomerMgnt.Core.Analytics;

/// <summary>
/// แนะนำสิ่งที่ควรทำถัดไปกับลูกค้า (Next Best Action) โดยรวมผลจากทุกโมเดล:
/// ความเสี่ยง churn, คะแนน lead, สัญญาที่ใกล้หมด, อารมณ์ในแชท, งานค้าง, การติดต่อล่าสุด, สินค้าแนะนำ และเวลาส่งที่ดีที่สุด
/// เป็นกฎที่อธิบายได้ (explainable) — ทุก action มีเหตุผลกำกับ และเรียงตามความเร่งด่วน (1 = ด่วนที่สุด)
/// </summary>
public sealed class NextBestActionService
{
    private readonly ICustomerDAO _customers;
    private readonly IRelationshipDAO _relationships;
    private readonly IOpportunityDAO _opportunities;
    private readonly IActivityDAO _activities;
    private readonly IConversationDAO _conversations;
    private readonly IEngagementDAO _engagement;
    private readonly PredictionService _predictions;
    private readonly RecommendationService _recommendations;
    private readonly SendTimeOptimizationService _sendTime;
    private readonly TimeProvider _clock;

    /// <summary>สร้างบริการ</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="relationships">DAO ความสัมพันธ์/ช่องทาง</param>
    /// <param name="opportunities">DAO โอกาสการขาย</param>
    /// <param name="activities">DAO กิจกรรม</param>
    /// <param name="conversations">DAO แชท</param>
    /// <param name="engagement">DAO สัญญา</param>
    /// <param name="predictions">โมเดลทำนาย</param>
    /// <param name="recommendations">ระบบแนะนำ</param>
    /// <param name="sendTime">เวลาส่งที่ดีที่สุด</param>
    /// <param name="clock">นาฬิกา</param>
    public NextBestActionService(ICustomerDAO customers, IRelationshipDAO relationships, IOpportunityDAO opportunities, IActivityDAO activities, IConversationDAO conversations,
        IEngagementDAO engagement, PredictionService predictions, RecommendationService recommendations, SendTimeOptimizationService sendTime, TimeProvider clock)
    {
        _customers = customers;
        _relationships = relationships;
        _opportunities = opportunities;
        _activities = activities;
        _conversations = conversations;
        _engagement = engagement;
        _predictions = predictions;
        _recommendations = recommendations;
        _sendTime = sendTime;
        _clock = clock;
    }

    /// <summary>คำแนะนำสำหรับลูกค้ารายเดียว</summary>
    /// <param name="user">ผู้เรียก (analytics.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="max">จำนวน action สูงสุด (1-10)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>action เรียงตามความเร่งด่วน</returns>
    /// <exception cref="NotFoundException">ไม่พบลูกค้า</exception>
    public async Task<IReadOnlyList<NextBestActionDTO>> RecommendAsync(UserContextDTO user, long customerId, int max = 3, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้า
        AccessGuard.Require(user, Permissions.AnalyticsRead);
        CustomerDTO customer = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");

        // Step 2: ดึงสัญญาณทั้งหมดพร้อมกัน
        Task<IReadOnlyList<ChurnRiskDTO>> churnTask = _predictions.PredictChurnAsync(user, customerId, 0, 1, ct);
        Task<IReadOnlyList<LeadScoreDTO>> leadTask = _predictions.ScoreLeadsAsync(user, customerId, 5, ct);
        Task<IReadOnlyList<OpportunityDTO>> oppTask = _opportunities.ListAsync(customerId, true, null, ct);
        Task<IReadOnlyList<ActivityDTO>> actTask = _activities.ListByCustomerAsync(customerId, true, 50, ct);
        Task<IReadOnlyList<ConversationDTO>> convTask = _conversations.ListAsync(null, customerId, false, 20, ct);
        Task<IReadOnlyList<ContractDTO>> contractTask = _engagement.ListContractsAsync(customerId, ct);
        Task<IReadOnlyDictionary<long, (DateTimeOffset? LastContactAt, int Done90Days)>> contactTask = _activities.ContactStatsAsync([customerId], ct);
        Task<IReadOnlyList<ChannelIdentityDTO>> identityTask = _relationships.ListIdentitiesAsync(customerId, ct);
        Task<IReadOnlyList<RecommendationDTO>> recTask = _recommendations.RecommendAsync(user, customerId, 1, null, ct);
        await Task.WhenAll(churnTask, leadTask, oppTask, actTask, convTask, contractTask, contactTask, identityTask, recTask).ConfigureAwait(false);

        // Step 3: ช่องทางที่เหมาะสม + เวลาส่งที่ดีที่สุด
        ChannelKind channel = PreferredChannel(customer, identityTask.Result);
        SendTimeDTO sendTime = await _sendTime.GetBestTimeAsync(user, customerId, channel, ct).ConfigureAwait(false);

        // Step 4: ใช้กฎ
        contactTask.Result.TryGetValue(customerId, out var contact);
        var signals = new NbaSignals(
            customer,
            churnTask.Result.FirstOrDefault(),
            leadTask.Result,
            oppTask.Result,
            actTask.Result,
            convTask.Result,
            contractTask.Result,
            contact.LastContactAt,
            recTask.Result.FirstOrDefault(),
            channel,
            sendTime.NextSendAt);
        return Decide(signals, _clock.GetUtcNow()).Take(Math.Clamp(max, 1, 10)).ToList();
    }

    /// <summary>กฎตัดสินใจ (pure function — ทดสอบได้)</summary>
    /// <param name="s">สัญญาณของลูกค้า</param>
    /// <param name="now">เวลาปัจจุบัน</param>
    /// <returns>action ทั้งหมดเรียงตามความเร่งด่วน</returns>
    internal static IReadOnlyList<NextBestActionDTO> Decide(NbaSignals s, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(s);
        long id = s.Customer.Id;
        var actions = new List<NextBestActionDTO>();

        // Rule 1: แชทที่ยังเปิด อารมณ์ลบ/ต้องดูแล และยังไม่ตอบ → ตอบทันที
        ConversationDTO? hot = s.Conversations
            .Where(c => c.Status != ConversationStatus.Resolved && (c.NeedsAttention || c.LastSentiment < -0.3))
            .OrderBy(c => c.LastSentiment).FirstOrDefault();
        if (hot is not null)
        {
            actions.Add(new NextBestActionDTO(id, "Respond to unhappy customer conversation", $"Open {hot.Channel} conversation #{hot.Id} has sentiment {hot.LastSentiment:0.00}" + (hot.FirstResponseAt is null ? " and no reply yet" : string.Empty), 1, now, hot.Channel, null));
        }

        // Rule 2: ความเสี่ยง churn สูง → โทรรักษาฐานลูกค้า
        if (s.Churn is { Probability: >= 0.5 } churn)
        {
            actions.Add(new NextBestActionDTO(id, "Retention call by account manager", $"Churn risk {churn.Probability:P0} ({churn.Level}): {string.Join(", ", churn.Signals.Take(3).Select(f => f.Feature))}", churn.Probability >= 0.75 ? 1 : 2, now, ChannelKind.Phone, null));
        }

        // Rule 3: สัญญาจะหมดใน 90 วัน → เริ่มต่อสัญญา (+ เสนอ upsell ถ้ามี)
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        ContractDTO? ending = s.Contracts.Where(c => c.Status == ContractStatus.Active && c.EndDate >= today && c.EndDate.DayNumber - today.DayNumber <= 90).OrderBy(c => c.EndDate).FirstOrDefault();
        if (ending is not null)
        {
            int days = ending.EndDate.DayNumber - today.DayNumber;
            actions.Add(new NextBestActionDTO(id, "Start MA renewal discussion", $"Contract {ending.ContractNo} ends in {days} days (value {ending.AnnualValue:N0})", days <= 30 ? 1 : 2, now, s.PreferredChannel, s.Recommendation?.Name));
        }

        // Rule 4: งานติดตามที่เลยกำหนด
        ActivityDTO? overdue = s.Activities.Where(a => a.IsOverdue(now)).OrderBy(a => a.DueAt).FirstOrDefault();
        if (overdue is not null)
        {
            actions.Add(new NextBestActionDTO(id, $"Complete overdue {overdue.Kind.ToString().ToLowerInvariant()}: {overdue.Subject}", $"Due {overdue.DueAt:yyyy-MM-dd HH:mm} UTC, owner {overdue.OwnerSubject}", 2, now, ChannelForActivity(overdue.Kind, s.PreferredChannel), null));
        }

        // Rule 5: ข้อเสนอค้างใน Proposal/Negotiation เกิน 14 วัน
        OpportunityDTO? stalled = s.Opportunities
            .Where(o => o.Stage is PipelineStage.Proposal or PipelineStage.Negotiation && (now - o.StageChangedAt).TotalDays > 14)
            .OrderByDescending(o => o.Amount).FirstOrDefault();
        if (stalled is not null)
        {
            actions.Add(new NextBestActionDTO(id, $"Follow up on {stalled.Stage.ToString().ToLowerInvariant()}: {stalled.Title}", $"No stage change for {(int)(now - stalled.StageChangedAt).TotalDays} days, amount {stalled.Amount:N0}", 2, now, ChannelKind.Phone, null));
        }

        // Rule 6: lead คะแนนสูงแต่ยังอยู่ช่วงต้น → นัดประชุมเชิงลึก
        LeadScoreDTO? hotLead = s.LeadScores.Where(l => l.Score >= 60).OrderByDescending(l => l.Score).FirstOrDefault();
        OpportunityDTO? early = hotLead is null ? null : s.Opportunities.FirstOrDefault(o => o.Id == hotLead.OpportunityId && o.Stage is PipelineStage.New or PipelineStage.Contacted or PipelineStage.Qualified);
        if (hotLead is not null && early is not null)
        {
            actions.Add(new NextBestActionDTO(id, $"Schedule discovery meeting for '{early.Title}'", $"Lead score {hotLead.Score:0} (grade {hotLead.Grade}) while still in {early.Stage}", 3, now, ChannelKind.Teams, null));
        }

        // Rule 7: ไม่ได้ติดต่อเกิน 60 วัน → ทักทาย ณ เวลาที่ลูกค้าเปิดอ่านบ่อย
        double daysSinceContact = s.LastContactAt is { } last ? (now - last).TotalDays : double.MaxValue;
        if (daysSinceContact > 60 && s.Customer.Status != CustomerStatus.Churned)
        {
            string since = s.LastContactAt is null ? "never contacted" : $"last contact {(int)daysSinceContact} days ago";
            actions.Add(new NextBestActionDTO(id, "Send personalised check-in", since, 3, s.SendAt, s.PreferredChannel, s.Recommendation?.Name));
        }

        // Rule 8: ค่าปริยาย → เสนอสินค้า/เนื้อหาที่แนะนำ (ต้องยินยอมรับการตลาด)
        if (s.Recommendation is not null && s.Customer.MarketingConsent && actions.TrueForAll(a => a.Offer is null))
        {
            actions.Add(new NextBestActionDTO(id, $"Offer {s.Recommendation.Kind.ToString().ToLowerInvariant()}: {s.Recommendation.Name}", s.Recommendation.Reason, 4, s.SendAt, s.PreferredChannel, s.Recommendation.Name));
        }

        if (actions.Count == 0)
        {
            actions.Add(new NextBestActionDTO(id, "No action needed", "Customer is healthy and recently contacted", 5, now, s.PreferredChannel, null));
        }

        return actions.OrderBy(a => a.Priority).ToList();
    }

    /// <summary>ช่องทางที่ควรใช้: LINE (ถ้ายืนยันแล้ว) → Email → โทรศัพท์</summary>
    internal static ChannelKind PreferredChannel(CustomerDTO customer, IReadOnlyList<ChannelIdentityDTO> identities)
    {
        if (identities.Any(i => i.Channel == ChannelKind.Line && i.Verified)) return ChannelKind.Line;
        if (!string.IsNullOrWhiteSpace(customer.Email) || identities.Any(i => i.Channel == ChannelKind.Email)) return ChannelKind.Email;
        if (identities.Count > 0) return identities[0].Channel;
        return ChannelKind.Phone;
    }

    /// <summary>ช่องทางตามชนิดกิจกรรม</summary>
    private static ChannelKind ChannelForActivity(ActivityKind kind, ChannelKind fallback) => kind switch
    {
        ActivityKind.Call => ChannelKind.Phone,
        ActivityKind.Email => ChannelKind.Email,
        ActivityKind.Meeting => ChannelKind.Teams,
        _ => fallback,
    };
}

/// <summary>สัญญาณทั้งหมดที่ใช้ตัดสินใจ Next Best Action</summary>
/// <param name="Customer">ลูกค้า</param>
/// <param name="Churn">ความเสี่ยง churn (null = ไม่มีข้อมูล)</param>
/// <param name="LeadScores">คะแนน lead ของ opportunity ที่เปิด</param>
/// <param name="Opportunities">opportunity ที่เปิด</param>
/// <param name="Activities">กิจกรรมที่ยังไม่เสร็จ</param>
/// <param name="Conversations">แชทล่าสุด</param>
/// <param name="Contracts">สัญญา</param>
/// <param name="LastContactAt">ติดต่อล่าสุด</param>
/// <param name="Recommendation">สินค้าแนะนำอันดับ 1</param>
/// <param name="PreferredChannel">ช่องทางที่เหมาะสม</param>
/// <param name="SendAt">เวลาส่งที่ดีที่สุดครั้งถัดไป</param>
public sealed record NbaSignals(
    CustomerDTO Customer,
    ChurnRiskDTO? Churn,
    IReadOnlyList<LeadScoreDTO> LeadScores,
    IReadOnlyList<OpportunityDTO> Opportunities,
    IReadOnlyList<ActivityDTO> Activities,
    IReadOnlyList<ConversationDTO> Conversations,
    IReadOnlyList<ContractDTO> Contracts,
    DateTimeOffset? LastContactAt,
    RecommendationDTO? Recommendation,
    ChannelKind PreferredChannel,
    DateTimeOffset SendAt);
