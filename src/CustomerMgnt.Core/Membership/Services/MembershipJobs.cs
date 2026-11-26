using System.Globalization;
using System.Text.Json;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Integration;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>ส่งข้อความแคมเปญ (ส่งเฉพาะคนที่ยินยอมช่องทางนั้น และสถานะ Active)</summary>
public sealed class CampaignMessageService
{
    private readonly IMembershipJobDAO _jobs;
    private readonly IMemberDAO _members;
    private readonly ISegmentDAO _segments;
    private readonly IMembershipAuditDAO _audit;
    private readonly IMessageHubGateway _messageHub;
    private readonly ICampaignDAO _campaigns;
    private readonly MemberAnalytics.DataAccess.IAnaMessageDAO _recipients;
    private readonly PrivacyService _privacy;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public CampaignMessageService(IMembershipJobDAO jobs, IMemberDAO members, ISegmentDAO segments, IMembershipAuditDAO audit, IMessageHubGateway messageHub,
        PrivacyService privacy, TimeProvider clock, ICampaignDAO campaigns, MemberAnalytics.DataAccess.IAnaMessageDAO recipients)
    {
        _campaigns = campaigns;
        _recipients = recipients;
        _jobs = jobs;
        _members = members;
        _segments = segments;
        _audit = audit;
        _messageHub = messageHub;
        _privacy = privacy;
        _clock = clock;
    }

    /// <summary>สร้างงานส่ง (ส่งทันทีหรือตามเวลา)</summary>
    public async Task<MessageJobDTO> CreateAsync(MembershipCaller caller, CampaignMessageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.CampaignMessageSend);
        ValidationException.ThrowIf(request.SegmentId is null && request.MemberIds is not { Count: > 0 }, "segmentId", "Specify a segment or members.");
        ValidationException.ThrowIf(request.MemberIds is { Count: > 10_000 }, "memberIds", "At most 10,000 members per job.");
        ValidationException.ThrowIf(!request.Variables.ContainsKey("body") || request.Variables["body"].Length is 0 or > 1000, "variables.body", "Message body is required (≤ 1,000 characters).");
        if (request.SegmentId is long sid)
        {
            _ = await _segments.GetAsync(sid, ct).ConfigureAwait(false) ?? throw new NotFoundException("Segment not found.");
        }

        string? campaignCode = string.IsNullOrWhiteSpace(request.CampaignCode) ? null : request.CampaignCode.Trim().ToUpperInvariant();
        ValidationException.ThrowIf(campaignCode is not null && await _campaigns.GetByCodeAsync(campaignCode, ct).ConfigureAwait(false) is null, "campaignCode", "Unknown campaign code.");

        DateTimeOffset now = _clock.GetUtcNow();
        var job = new MessageJobDTO
        {
            SegmentId = request.SegmentId, Channel = request.Channel, CampaignCode = campaignCode, TemplateCode = request.TemplateCode, VariablesJson = MembershipJson.Write(request.Variables),
            MemberIdsJson = request.MemberIds is { Count: > 0 } ids ? MembershipJson.Write(ids.Distinct()) : null, Status = JobStatus.Queued,
            ScheduledAt = request.ScheduledAt, RequestedBy = caller.Actor, RequestedAt = now,
        };
        job.Id = await _jobs.InsertMessageJobAsync(job, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "campaign_message.create", $"message_job:{job.Id}", null, new { job.SegmentId, job.Channel, job.TemplateCode, members = request.MemberIds?.Count }, now)], ct).ConfigureAwait(false);
        return job;
    }

    /// <summary>งานส่งทั้งหมด</summary>
    public Task<IReadOnlyList<MessageJobDTO>> ListAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.CampaignMessageSend);
        return _jobs.ListMessageJobsAsync(null, ct);
    }

    /// <summary>งานเบื้องหลัง: ส่งงานที่ถึงเวลา</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        int done = 0;
        foreach (MessageJobDTO job in (await _jobs.ListMessageJobsAsync(JobStatus.Queued, ct).ConfigureAwait(false)).Where(j => j.ScheduledAt is null || j.ScheduledAt <= now))
        {
            job.Status = JobStatus.Running;
            await _jobs.UpdateMessageJobAsync(job, ct).ConfigureAwait(false);
            try
            {
                Dictionary<string, string> vars = MembershipJson.Read<Dictionary<string, string>>(job.VariablesJson);
                CampaignDTO? campaign = job.CampaignCode is null ? null : await _campaigns.GetByCodeAsync(job.CampaignCode, ct).ConfigureAwait(false);
                foreach (long id in await RecipientsAsync(job, ct).ConfigureAwait(false))
                {
                    MemberEntity? m = await _members.GetAsync(id, ct).ConfigureAwait(false);
                    // กลุ่มควบคุมของแคมเปญไม่ได้รับข้อความ (สูตรข้อ 4)
                    bool control = m is not null && campaign is not null && MemberAnalytics.Engine.HoldoutAssigner.IsControl(campaign.Code, m.MemberNo, campaign.HoldoutPct);
                    string? to = m is null || control ? null : Address(m, job.Channel);
                    string messageId = $"cm{job.Id}-{id}";
                    if (to is null || !await _messageHub.SendToCustomerAsync(Kind(job.Channel), to, vars.GetValueOrDefault("subject"), Render(vars["body"], m!), messageId, ct).ConfigureAwait(false))
                    {
                        job.Skipped++;
                        continue;
                    }

                    job.Recipients++;
                    await _recipients.InsertRecipientsAsync([new MemberAnalytics.Domain.MessageRecipientDTO { MessageId = messageId, CampaignCode = campaign?.Code, MemberId = id, SentAt = _clock.GetUtcNow() }], ct).ConfigureAwait(false);
                }

                job.Status = JobStatus.Done;
            }
            catch (Exception ex) when (ex is DependencyUnavailableException or HttpRequestException or TaskCanceledException)
            {
                job.Status = JobStatus.Failed;
                job.Error = "Message service unavailable.";
            }

            job.CompletedAt = _clock.GetUtcNow();
            await _jobs.UpdateMessageJobAsync(job, ct).ConfigureAwait(false);
            done++;
        }

        return done;
    }

    private async Task<List<long>> RecipientsAsync(MessageJobDTO job, CancellationToken ct)
    {
        if (job.MemberIdsJson is not null)
        {
            return MembershipJson.Read<List<long>>(job.MemberIdsJson);
        }

        var ids = new List<long>();
        for (int page = 1; ; page++)
        {
            Common.Core.Domain.PagedResultDTO<long> p = await _segments.PageMemberIdsAsync(job.SegmentId!.Value, page, 1000, ct).ConfigureAwait(false);
            ids.AddRange(p.Items);
            if (p.Items.Count < 1000)
            {
                return ids;
            }
        }
    }

    /// <summary>ที่อยู่ปลายทางเมื่อยินยอมช่องทางนั้น (LINE ยังไม่มี userId ของสมาชิก = ข้าม)</summary>
    private string? Address(MemberEntity m, MarketingChannel channel)
    {
        if (m.Status != MemberStatus.Active || !m.MarketingConsent || !MemberService.ParseChannels(m.MarketingChannels).Contains(channel))
        {
            return null;
        }

        return channel switch
        {
            MarketingChannel.Sms when m.PhoneEnvelope is not null => _privacy.Open(m.PhoneEnvelope, MemberService.PhoneContext),
            MarketingChannel.Email when m.EmailEnvelope is not null => _privacy.Open(m.EmailEnvelope, MemberService.EmailContext),
            _ => null,
        };
    }

    private static ChannelKind Kind(MarketingChannel c) => c switch
    {
        MarketingChannel.Email => ChannelKind.Email,
        MarketingChannel.Line => ChannelKind.Line,
        _ => ChannelKind.Sms,
    };

    private static string Render(string body, MemberEntity m) =>
        body.Replace("{firstName}", m.FirstName, StringComparison.Ordinal).Replace("{memberNo}", m.MemberNo, StringComparison.Ordinal)
            .Replace("{tier}", m.TierCode ?? string.Empty, StringComparison.Ordinal);
}

/// <summary>ส่ง event ของระบบสมาชิกออกไปยังระบบอื่น (ค่าเริ่มต้น: บันทึก log อย่างเดียว — ต่อ message broker ได้โดย implement interface นี้)</summary>
public interface IMembershipEventSink
{
    /// <summary>ส่ง event (ต้องทนการส่งซ้ำด้วย eventId)</summary>
    Task PublishAsync(OutboxMessageDTO message, CancellationToken ct);
}

/// <summary>ค่าเริ่มต้นของ <see cref="IMembershipEventSink"/></summary>
public sealed partial class LoggingMembershipEventSink(ILogger<LoggingMembershipEventSink> logger) : IMembershipEventSink
{
    /// <inheritdoc />
    public Task PublishAsync(OutboxMessageDTO message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogEvent(logger, message.Topic, message.EventId);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 5300, Level = LogLevel.Debug, Message = "Membership event {Topic} {EventId}")]
    private static partial void LogEvent(ILogger logger, string topic, string eventId);
}

/// <summary>
/// ส่ง outbox: retry แบบ exponential backoff + jitter สูงสุด 10 ครั้ง / 24 ชม. แล้วย้ายไป dead-letter + log เตือน (Claude.MD §26)
/// </summary>
public sealed partial class OutboxDispatcher
{
    private const int MaxAttempts = 10;

    private readonly IOutboxDAO _outbox;
    private readonly IMemberDAO _members;
    private readonly MemberService _memberService;
    private readonly IMessageHubGateway _messageHub;
    private readonly IMembershipEventSink _sink;
    private readonly PrivacyService _privacy;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxDispatcher> _log;

    /// <summary>สร้าง</summary>
    public OutboxDispatcher(IOutboxDAO outbox, IMemberDAO members, MemberService memberService, IMessageHubGateway messageHub, IMembershipEventSink sink,
        PrivacyService privacy, TimeProvider clock, ILogger<OutboxDispatcher> log)
    {
        _outbox = outbox;
        _members = members;
        _memberService = memberService;
        _messageHub = messageHub;
        _sink = sink;
        _privacy = privacy;
        _clock = clock;
        _log = log;
    }

    /// <summary>ส่งข้อความที่ถึงกำหนด</summary>
    /// <returns>จำนวนที่ส่งสำเร็จ</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        int sent = 0;
        foreach (OutboxMessageDTO msg in await _outbox.ListDueAsync(_clock.GetUtcNow(), 100, ct).ConfigureAwait(false))
        {
            try
            {
                await HandleAsync(msg, ct).ConfigureAwait(false);
                await _outbox.MarkSentAsync(msg.Id, _clock.GetUtcNow(), ct).ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DateTimeOffset now = _clock.GetUtcNow();
                int attempts = msg.Attempts + 1;
                bool dead = attempts >= MaxAttempts || now - msg.CreatedAt > TimeSpan.FromHours(24);
                double backoff = Math.Min(30 * Math.Pow(2, attempts - 1), 3600) * (0.8 + Random.Shared.NextDouble() * 0.4);
                string error = ex is CommonException ce ? ce.ErrorCode : ex.GetType().Name;
                await _outbox.MarkFailedAsync(msg.Id, attempts, now.AddSeconds(backoff), error, dead, ct).ConfigureAwait(false);
                if (dead)
                {
                    LogDeadLetter(_log, msg.Topic, msg.EventId, attempts, error);
                }
            }
        }

        return sent;
    }

    private async Task HandleAsync(OutboxMessageDTO msg, CancellationToken ct)
    {
        switch (msg.Topic)
        {
            case "customer.create":
                await _memberService.CreateCustomerForMemberAsync(MemberId(msg), ct).ConfigureAwait(false);
                break;
            case "points.expiring":
                await NotifyExpiringAsync(msg, ct).ConfigureAwait(false);
                await _sink.PublishAsync(msg, ct).ConfigureAwait(false);
                break;
            default:
                await _sink.PublishAsync(msg, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task NotifyExpiringAsync(OutboxMessageDTO msg, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(msg.PayloadJson);
        MemberEntity? m = await _members.GetAsync(MemberId(msg), ct).ConfigureAwait(false);
        if (m is null || m.Status != MemberStatus.Active || m.PhoneEnvelope is null)
        {
            return;
        }

        int points = doc.RootElement.TryGetProperty("points", out JsonElement p) ? p.GetInt32() : 0;
        string date = doc.RootElement.TryGetProperty("expiresAt", out JsonElement e) ? e.GetDateTimeOffset().ToString("d MMM yyyy", CultureInfo.GetCultureInfo("th-TH")) : string.Empty;
        string phone = _privacy.Open(m.PhoneEnvelope, MemberService.PhoneContext);
        await _messageHub.SendToCustomerAsync(ChannelKind.Sms, phone, null, $"คะแนนสมาชิก {points:N0} คะแนนจะหมดอายุ {date} ใช้คะแนนก่อนหมดอายุได้ที่ร้านหรือ Member Portal", ct).ConfigureAwait(false);
    }

    private static long MemberId(OutboxMessageDTO msg)
    {
        using JsonDocument doc = JsonDocument.Parse(msg.PayloadJson);
        return doc.RootElement.TryGetProperty("memberId", out JsonElement id) ? id.GetInt64() : 0;
    }

    [LoggerMessage(EventId = 5301, Level = LogLevel.Error, Message = "Outbox message {Topic} {EventId} moved to dead-letter after {Attempts} attempts: {Error}")]
    private static partial void LogDeadLetter(ILogger logger, string topic, string eventId, int attempts, string error);
}

/// <summary>งานบำรุงรักษา: คะแนนหมดอายุ, แจ้งเตือนใกล้หมดอายุ, คูปองหมดอายุ, สร้างรหัสคูปอง, ลบ idempotency key เก่า</summary>
public sealed class MembershipMaintenanceService
{
    private readonly IPointDAO _points;
    private readonly ICouponDAO _coupons;
    private readonly IOutboxDAO _outbox;
    private readonly IIdempotencyDAO _idempotency;
    private readonly IMembershipUnitOfWork _uow;
    private readonly PointLedgerService _ledger;
    private readonly CouponService _couponService;
    private readonly MembershipRulesProvider _rules;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public MembershipMaintenanceService(IPointDAO points, ICouponDAO coupons, IOutboxDAO outbox, IIdempotencyDAO idempotency, IMembershipUnitOfWork uow,
        PointLedgerService ledger, CouponService couponService, MembershipRulesProvider rules, TimeProvider clock)
    {
        _points = points;
        _coupons = coupons;
        _outbox = outbox;
        _idempotency = idempotency;
        _uow = uow;
        _ledger = ledger;
        _couponService = couponService;
        _rules = rules;
        _clock = clock;
    }

    /// <summary>ตัดคะแนนของ lot ที่หมดอายุ (ทีละสมาชิก, key <c>expire:{lotId}</c> กันตัดซ้ำ)</summary>
    public async Task<int> ExpirePointsAsync(CancellationToken ct)
    {
        int lots = 0;
        while (true)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            IReadOnlyList<PointLotDTO> expired = await _points.ListExpiredLotsAsync(now, 500, ct).ConfigureAwait(false);
            if (expired.Count == 0)
            {
                return lots;
            }

            int before = lots;
            foreach (IGrouping<long, PointLotDTO> g in expired.GroupBy(l => l.MemberId))
            {
                try
                {
                    var cs = new MembershipChangeSet();
                    PointPosting p = await _ledger.OpenAsync(g.Key, now, ct).ConfigureAwait(false);
                    foreach (PointLotDTO lot in g)
                    {
                        PointLedgerService.ExpireLot(p, cs, lot, now);
                    }

                    PointLedgerService.Finish(p, cs);
                    await _uow.CommitAsync(cs, ct).ConfigureAwait(false);
                    lots += g.Count();
                }
                catch (MembershipConcurrencyException)
                {
                    // มีรายการอื่นของสมาชิกนี้พร้อมกัน — รอบถัดไป
                }
            }

            if (expired.Count < 500 || lots == before)
            {
                return lots;
            }
        }
    }

    /// <summary>แจ้งเตือนคะแนนที่จะหมดอายุภายใน N วัน (วันละครั้ง) ผ่าน outbox</summary>
    public async Task<int> NotifyExpiringAsync(CancellationToken ct)
    {
        MembershipRulesDTO rules = await _rules.GetAsync(ct).ConfigureAwait(false);
        if (rules.ExpiryNotifyDays <= 0)
        {
            return 0;
        }

        DateTimeOffset now = _clock.GetUtcNow();
        IReadOnlyList<(long MemberId, int Points, DateTimeOffset ExpiresAt)> due = await _points.ListExpiringForNoticeAsync(now, now.AddDays(rules.ExpiryNotifyDays), ct).ConfigureAwait(false);
        List<OutboxMessageDTO> messages = [.. due.Where(d => d.Points > 0).Select(d => MembershipEvents.Outbox("points.expiring", new { memberId = d.MemberId, points = d.Points, expiresAt = d.ExpiresAt }, now))];
        for (int i = 0; i < messages.Count; i += 500)
        {
            await _outbox.EnqueueAsync(messages.GetRange(i, Math.Min(500, messages.Count - i)), ct).ConfigureAwait(false);
        }

        return messages.Count;
    }

    /// <summary>คูปองหมดอายุ</summary>
    public Task<int> ExpireCouponsAsync(CancellationToken ct) => _coupons.ExpireDueAsync(_clock.GetUtcNow(), ct);

    /// <summary>สร้างรหัสคูปองของชุดที่รอคิว</summary>
    public async Task<int> GenerateCouponsAsync(CancellationToken ct)
    {
        int n = 0;
        foreach (CouponBatchDTO b in (await _coupons.ListBatchesAsync(null, ct).ConfigureAwait(false)).Where(b => b.Status is JobStatus.Queued or JobStatus.Running && !b.PerMember))
        {
            await _couponService.GenerateAsync(b, ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    /// <summary>ลบ idempotency key ที่หมดอายุ</summary>
    public Task<int> PurgeAsync(CancellationToken ct) => _idempotency.PurgeAsync(_clock.GetUtcNow(), ct);
}

/// <summary>
/// ตัวรันงานเบื้องหลังของระบบสมาชิก — รันได้หลาย instance (lease lock ในฐานข้อมูล ทำที่ instance เดียวต่อรอบ)
/// </summary>
public sealed partial class MembershipWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly MembershipOptions _options;
    private readonly ILogger<MembershipWorker> _log;
    private readonly string _holder = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>สร้าง</summary>
    public MembershipWorker(IServiceScopeFactory scopes, TimeProvider clock, IOptions<MembershipOptions> options, ILogger<MembershipWorker> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopes = scopes;
        _clock = clock;
        _options = options.Value;
        _log = log;
    }

    /// <summary>นิยามงาน: ทุก ๆ ช่วงเวลา หรือรายวันตามเวลาท้องถิ่น (Asia/Bangkok)</summary>
    private sealed record Job(string Name, TimeSpan? Every, TimeOnly? DailyAt, Func<IServiceProvider, CancellationToken, Task<int>> Run);

    private static readonly Job[] Jobs =
    [
        new("outbox", TimeSpan.FromSeconds(10), null, (s, ct) => s.GetRequiredService<OutboxDispatcher>().RunAsync(ct)),
        new("reservation-expiry", TimeSpan.FromMinutes(1), null, (s, ct) => s.GetRequiredService<CheckoutService>().ReleaseExpiredAsync(ct)),
        new("campaign-schedule", TimeSpan.FromMinutes(1), null, (s, ct) => s.GetRequiredService<CampaignService>().RunScheduleAsync(ct)),
        new("campaign-messages", TimeSpan.FromMinutes(1), null, (s, ct) => s.GetRequiredService<CampaignMessageService>().RunAsync(ct)),
        new("coupon-generate", TimeSpan.FromMinutes(1), null, (s, ct) => s.GetRequiredService<MembershipMaintenanceService>().GenerateCouponsAsync(ct)),
        new("exports", TimeSpan.FromMinutes(1), null, (s, ct) => s.GetRequiredService<ReportService>().RunExportsAsync(ct)),
        new("pending-returns", TimeSpan.FromMinutes(5), null, (s, ct) => s.GetRequiredService<CheckoutService>().ProcessPendingReturnsAsync(ct)),
        new("purge", TimeSpan.FromHours(1), null, (s, ct) => s.GetRequiredService<MembershipMaintenanceService>().PurgeAsync(ct)),
        new("coupon-expiry", TimeSpan.FromHours(1), null, (s, ct) => s.GetRequiredService<MembershipMaintenanceService>().ExpireCouponsAsync(ct)),
        new("point-expiry", null, new TimeOnly(0, 15), (s, ct) => s.GetRequiredService<MembershipMaintenanceService>().ExpirePointsAsync(ct)),
        new("tier-review", null, new TimeOnly(1, 0), (s, ct) => s.GetRequiredService<TierService>().RunReviewsAsync(ct)),
        new("segment-refresh", null, new TimeOnly(2, 0), (s, ct) => s.GetRequiredService<SegmentService>().RefreshAllAsync(ct)),
        new("expiry-notice", null, new TimeOnly(9, 0), (s, ct) => s.GetRequiredService<MembershipMaintenanceService>().NotifyExpiringAsync(ct)),
    ];

    private readonly Dictionary<string, DateTimeOffset> _lastRun = [];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkersEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), _clock);
        do
        {
            foreach (Job job in Jobs)
            {
                await TryRunAsync(job, stoppingToken).ConfigureAwait(false);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task TryRunAsync(Job job, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        string leaseKey;
        TimeSpan lease;
        if (job.Every is TimeSpan every)
        {
            if (_lastRun.TryGetValue(job.Name, out DateTimeOffset last) && now - last < every)
            {
                return;
            }

            leaseKey = job.Name;
            lease = every - TimeSpan.FromSeconds(1);
        }
        else
        {
            using IServiceScope s0 = _scopes.CreateScope();
            MembershipRulesDTO rules = await s0.ServiceProvider.GetRequiredService<MembershipRulesProvider>().GetAsync(ct).ConfigureAwait(false);
            DateTime local = TimeZoneInfo.ConvertTime(now, PointMath.Zone(rules.TimeZoneId)).DateTime;
            if (TimeOnly.FromDateTime(local) < job.DailyAt!.Value)
            {
                return;
            }

            leaseKey = $"{job.Name}:{local:yyyy-MM-dd}";
            lease = TimeSpan.FromHours(25);
            if (_lastRun.ContainsKey(leaseKey))
            {
                return;
            }
        }

        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            IMembershipJobDAO jobs = scope.ServiceProvider.GetRequiredService<IMembershipJobDAO>();
            _lastRun[job.Every is null ? leaseKey : job.Name] = now;
            if (!await jobs.TryAcquireLeaseAsync(leaseKey, _holder, now, lease, ct).ConfigureAwait(false))
            {
                return;
            }

            int n = await job.Run(scope.ServiceProvider, ct).ConfigureAwait(false);
            if (n > 0)
            {
                LogJobDone(_log, job.Name, n);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJobFailed(_log, ex, job.Name);
        }
    }

    [LoggerMessage(EventId = 5310, Level = LogLevel.Information, Message = "Membership job {Job} processed {Count} items")]
    private static partial void LogJobDone(ILogger logger, string job, int count);

    [LoggerMessage(EventId = 5311, Level = LogLevel.Error, Message = "Membership job {Job} failed")]
    private static partial void LogJobFailed(ILogger logger, Exception ex, string job);
}
