using System.Globalization;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Integration;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;
using CustomerMgnt.Core.Membership.Services;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

/// <summary>
/// กฎแจ้งเตือน (§38, สูตรข้อ 5): ประเมินหลังทุกรอบ refresh — เงื่อนไขจริงและพ้น cooldown ของกฎ+ขอบเขต = ส่งอีเมล,
/// เงื่อนไขกลับเป็นปกติ = บันทึก Resolved (ไม่ส่งอีเมล)
/// </summary>
public sealed partial class AnaAlertService
{
    private readonly IAnaDocumentDAO _docs;
    private readonly IAnaReadModelDAO _model;
    private readonly IAnaSourceDAO _source;
    private readonly ICampaignDAO _campaigns;
    private readonly DataScopeService _scopes;
    private readonly AnaSettingsProvider _settings;
    private readonly IMessageHubGateway _hub;
    private readonly TimeProvider _clock;
    private readonly ILogger<AnaAlertService> _log;

    /// <summary>สร้าง</summary>
    public AnaAlertService(IAnaDocumentDAO docs, IAnaReadModelDAO model, IAnaSourceDAO source, ICampaignDAO campaigns, DataScopeService scopes, AnaSettingsProvider settings,
        IMessageHubGateway hub, TimeProvider clock, ILogger<AnaAlertService> log)
    {
        _docs = docs;
        _model = model;
        _source = source;
        _campaigns = campaigns;
        _scopes = scopes;
        _settings = settings;
        _hub = hub;
        _clock = clock;
        _log = log;
    }

    // ================================================================== กฎ

    /// <summary>กฎทั้งหมด</summary>
    public async Task<IReadOnlyList<AlertRuleDTO>> ListRulesAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.AlertManage);
        return [.. (await _docs.ListAsync<AlertRuleDTO>(AnaCollections.AlertRule, null, ct).ConfigureAwait(false)).Select(WithVersion).OrderBy(r => r.Name, StringComparer.CurrentCulture)];
    }

    private static AlertRuleDTO WithVersion(AnaDocument<AlertRuleDTO> d)
    {
        d.Value.Version = d.Version;
        return d.Value;
    }

    /// <summary>อ่านกฎ</summary>
    public async Task<AlertRuleDTO> GetRuleAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.AlertManage);
        return WithVersion(await _docs.GetAsync<AlertRuleDTO>(AnaCollections.AlertRule, id.ToString(), ct).ConfigureAwait(false) ?? throw new NotFoundException("Alert rule not found."));
    }

    /// <summary>ค่าเริ่มต้นของพารามิเตอร์แต่ละชนิด</summary>
    public static IReadOnlyDictionary<string, string> DefaultParams(AlertType type) => type switch
    {
        AlertType.CouponUsageLow or AlertType.CouponUsageHigh => new Dictionary<string, string> { ["windowDays"] = "1", ["threshold"] = "10", ["campaign"] = string.Empty },
        AlertType.CampaignBudgetNear => new Dictionary<string, string> { ["pct"] = "90", ["campaign"] = string.Empty },
        AlertType.PointsAdjustmentAbnormal => new Dictionary<string, string> { ["minCount"] = "1" },
        AlertType.NoPosData => new Dictionary<string, string> { ["minutes"] = "60" },
        AlertType.NewMembersDrop => new Dictionary<string, string> { ["pct"] = "50" },
        _ => new Dictionary<string, string> { ["multiplier"] = "3", ["campaign"] = string.Empty },
    };

    private async Task ValidateAsync(AlertRuleDTO r, CancellationToken ct)
    {
        r.Name = r.Name.Trim();
        foreach ((string k, string v) in DefaultParams(r.Type))
        {
            if (!r.Params.TryGetValue(k, out string? given) || given is null)
            {
                r.Params[k] = v;
            }
            else if (k != "campaign")
            {
                ValidationException.ThrowIf(!decimal.TryParse(given, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) || d < 0, $"params.{k}", $"'{k}' must be a non-negative number.");
            }
        }

        ValidationException.ThrowIf(r.Recipients.Count is 0 or > 20, "recipients", "Add 1 to 20 recipients.");
        AnalyticsSettingsDTO s = await _settings.GetAsync(ct).ConfigureAwait(false);
        foreach (AlertRecipientDTO to in r.Recipients.Where(x => x.Channel == AlertChannel.Email))
        {
            ValidationException.ThrowIf(!System.Net.Mail.MailAddress.TryCreate(to.To, out System.Net.Mail.MailAddress? a), "recipients", $"Invalid e-mail '{to.To}'.");
            if (s.AllowRecipientDomains.Count > 0 && !s.AllowRecipientDomains.Contains(a!.Host, StringComparer.OrdinalIgnoreCase))
            {
                throw AnaErrors.Bad("recipient_not_allowed", $"The domain '{a.Host}' is not in the allowed list.");
            }
        }
    }

    /// <summary>สร้างกฎ</summary>
    public async Task<AlertRuleDTO> CreateRuleAsync(MembershipCaller caller, AlertRuleDTO rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(rule);
        caller.Require(AnaPermissions.AlertManage);
        await ValidateAsync(rule, ct).ConfigureAwait(false);
        rule.Id = Guid.NewGuid();
        rule.OwnerSub = caller.User.Subject;
        await _docs.InsertAsync(AnaCollections.AlertRule, rule.Id.ToString(), rule.OwnerSub, rule, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        rule.Version = 1;
        return rule;
    }

    /// <summary>แก้กฎ</summary>
    public async Task<AlertRuleDTO> UpdateRuleAsync(MembershipCaller caller, Guid id, AlertRuleDTO rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(rule);
        AlertRuleDTO before = await GetRuleAsync(caller, id, ct).ConfigureAwait(false);
        await ValidateAsync(rule, ct).ConfigureAwait(false);
        rule.Id = id;
        rule.OwnerSub = before.OwnerSub;
        if (!await _docs.UpdateAsync(AnaCollections.AlertRule, id.ToString(), rule.OwnerSub, rule.Version, rule, _clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            throw MembershipException.VersionConflict();
        }

        rule.Version++;
        return rule;
    }

    /// <summary>ลบกฎ</summary>
    public async Task DeleteRuleAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(AnaPermissions.AlertManage);
        if (!await _docs.DeleteAsync(AnaCollections.AlertRule, id.ToString(), ct).ConfigureAwait(false))
        {
            throw new NotFoundException("Alert rule not found.");
        }
    }

    /// <summary>ประเมินทันที (ไม่ส่งข้อความ ไม่บันทึกเหตุการณ์)</summary>
    public async Task<AlertTestResultDTO> TestRuleAsync(MembershipCaller caller, Guid id, CancellationToken ct)
    {
        AlertRuleDTO rule = await GetRuleAsync(caller, id, ct).ConfigureAwait(false);
        return new AlertTestResultDTO { Results = [.. await EvaluateRuleAsync(rule, ct).ConfigureAwait(false)] };
    }

    /// <summary>AN-23 เหตุการณ์ (เฉพาะสาขาในขอบเขตของผู้เรียก)</summary>
    public async Task<PagedResultDTO<AlertEventDTO>> ListEventsAsync(MembershipCaller caller, AlertEventCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(criteria);
        caller.Require(AnaPermissions.AlertManage);
        DataScope scope = await _scopes.ResolveAsync(caller, ct).ConfigureAwait(false);
        List<AlertEventDTO> all = scope.Empty ? [] : [.. (await _docs.ListAsync<AlertEventDTO>(AnaCollections.AlertEvent, null, ct).ConfigureAwait(false)).Select(d => d.Value)
            .Where(e => criteria.RuleId is null || e.RuleId == criteria.RuleId)
            .Where(e => criteria.State is null || e.State == criteria.State)
            .Where(e => criteria.From is null || AnaTime.Day(e.OccurredAt) >= criteria.From)
            .Where(e => criteria.To is null || AnaTime.Day(e.OccurredAt) <= criteria.To)
            .Where(e => scope.Branches is null || e.BranchCode is null || scope.Branches.Contains(e.BranchCode))
            .OrderByDescending(e => e.OccurredAt)];
        return AnaPaged.Of(all, criteria.Page, criteria.PageSize);
    }

    // ================================================================== ประเมิน

    private static decimal P(AlertRuleDTO r, string key) =>
        decimal.TryParse(r.Params.GetValueOrDefault(key) ?? DefaultParams(r.Type).GetValueOrDefault(key), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : 0;

    private bool InScope(AlertRuleDTO r, string? branch) => r.Scope.Branches.Count == 0 || (branch is not null && r.Scope.Branches.Contains(branch, StringComparer.OrdinalIgnoreCase));

    /// <summary>ค่าปัจจุบันของกฎ ต่อขอบเขตย่อย (สาขา / แคมเปญ)</summary>
    internal async Task<List<AlertEvaluationDTO>> EvaluateRuleAsync(AlertRuleDTO r, CancellationToken ct)
    {
        AnalyticsSettingsDTO s = await _settings.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        DateOnly today = AnaTime.Day(now);
        string? campaign = r.Params.GetValueOrDefault("campaign") is { Length: > 0 } cc ? cc.Trim().ToUpperInvariant() : null;
        var result = new List<AlertEvaluationDTO>();
        switch (r.Type)
        {
            case AlertType.CouponUsageLow:
            case AlertType.CouponUsageHigh:
            {
                int days = Math.Max(1, (int)P(r, "windowDays"));
                decimal threshold = P(r, "threshold");
                List<AnaCouponDayDTO> rows = [.. (await _model.ListCouponDaysAsync(today.AddDays(-days + 1), today, ct).ConfigureAwait(false))
                    .Where(x => (campaign is null || string.Equals(x.CampaignCode, campaign, StringComparison.OrdinalIgnoreCase)) && (r.Scope.Branches.Count == 0 || InScope(r, x.BranchCode)))];
                foreach (IGrouping<string, AnaCouponDayDTO> g in rows.GroupBy(x => x.CampaignCode, StringComparer.OrdinalIgnoreCase))
                {
                    decimal used = g.Where(x => x.Event == CouponMetricEvent.Redeemed).Sum(x => x.Count) - g.Where(x => x.Event == CouponMetricEvent.Reversed).Sum(x => x.Count);
                    bool fire = r.Type == AlertType.CouponUsageLow ? used < threshold : used > threshold;
                    result.Add(new AlertEvaluationDTO { ScopeKey = "campaign:" + g.Key, Value = used, Threshold = threshold, WouldFire = fire, Drilldown = "campaign." + g.Key });
                }

                if (campaign is not null && result.Count == 0)
                {
                    result.Add(new AlertEvaluationDTO { ScopeKey = "campaign:" + campaign, Value = 0, Threshold = threshold, WouldFire = r.Type == AlertType.CouponUsageLow && threshold > 0 });
                }

                break;
            }

            case AlertType.CampaignBudgetNear:
            {
                decimal pct = P(r, "pct");
                foreach (CampaignDTO c in await _campaigns.ListByStatusAsync([CampaignStatus.Active], ct).ConfigureAwait(false))
                {
                    if (campaign is not null && !string.Equals(c.Code, campaign, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    decimal? used = new[]
                    {
                        c.Limits.Budget is > 0 ? AnaMath.Pct(c.Usage.BudgetUsed, c.Limits.Budget.Value) : null,
                        c.Limits.TotalUses is > 0 ? AnaMath.Pct(c.Usage.Uses, c.Limits.TotalUses.Value) : null,
                    }.Max();
                    if (used is decimal u)
                    {
                        result.Add(new AlertEvaluationDTO { ScopeKey = "campaign:" + c.Code, Value = u, Threshold = pct, WouldFire = u >= pct, Drilldown = "campaign." + c.Code });
                    }
                }

                break;
            }

            case AlertType.PointsAdjustmentAbnormal:
            {
                int count = 0;
                (DateTimeOffset f, DateTimeOffset t) = AnaTime.Range(today, today);
                long after = 0;
                while (true)
                {
                    IReadOnlyList<PointLedgerDTO> page = await _source.ListLedgerAsync(f, t, after, 1000, ct).ConfigureAwait(false);
                    count += page.Count(l => l.Type == LedgerType.Adjustment && Math.Abs(l.Points) >= s.LargeAdjustmentPoints);
                    if (page.Count < 1000)
                    {
                        break;
                    }

                    after = page[^1].Id;
                }

                decimal min = Math.Max(1, P(r, "minCount"));
                result.Add(new AlertEvaluationDTO { ScopeKey = "all", Value = count, Threshold = min, WouldFire = count >= min });
                break;
            }

            case AlertType.NoPosData:
                result.AddRange(await NoPosDataAsync(r, now, ct).ConfigureAwait(false));
                break;

            case AlertType.NewMembersDrop:
            {
                decimal pct = P(r, "pct");
                IReadOnlyList<AnaMemberSnapshotDTO> snaps = await _model.ListSnapshotsAsync(ct).ConfigureAwait(false);
                // วันที่ครบแล้วล่าสุด (เมื่อวาน) เทียบค่าเฉลี่ยวันเดียวกันของสัปดาห์ 4 สัปดาห์ก่อน
                DateOnly d = today.AddDays(-1);
                int Count(DateOnly day) => snaps.Count(m => AnaTime.Day(m.RegisteredAt) == day && (r.Scope.Branches.Count == 0 || InScope(r, m.HomeBranch)));
                decimal avg = (decimal)Enumerable.Range(1, 4).Select(i => Count(d.AddDays(-7 * i))).Average();
                int cur = Count(d);
                decimal? change = avg == 0 ? null : Math.Round(Math.Abs(cur - avg) * 100 / avg, 2);
                result.Add(new AlertEvaluationDTO { ScopeKey = "day:" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Value = change, Threshold = pct, WouldFire = change > pct, Drilldown = "member.new" });
                break;
            }

            default:
            {
                decimal mult = P(r, "multiplier");
                List<AnaCouponDayDTO> rows = [.. (await _model.ListCouponDaysAsync(today.AddDays(-7), today, ct).ConfigureAwait(false))
                    .Where(x => x.Event == CouponMetricEvent.Redeemed && (campaign is null || string.Equals(x.CampaignCode, campaign, StringComparison.OrdinalIgnoreCase))
                        && (r.Scope.Branches.Count == 0 || InScope(r, x.BranchCode)))];
                decimal todayCount = rows.Where(x => x.Day == today).Sum(x => x.Count);
                decimal avg = Enumerable.Range(1, 7).Select(i => rows.Where(x => x.Day == today.AddDays(-i)).Sum(x => (decimal)x.Count)).Average();
                result.Add(new AlertEvaluationDTO
                {
                    ScopeKey = campaign is null ? "all" : "campaign:" + campaign, Value = todayCount, Threshold = Math.Round(avg * mult, 2), WouldFire = avg > 0 && todayCount > avg * mult,
                });
                break;
            }
        }

        return result;
    }

    /// <summary>สาขาที่อยู่ในเวลาทำการแต่ไม่มีธุรกรรมเกิน N นาที</summary>
    private async Task<List<AlertEvaluationDTO>> NoPosDataAsync(AlertRuleDTO r, DateTimeOffset now, CancellationToken ct)
    {
        decimal minutes = Math.Max(1, P(r, "minutes"));
        DateTime local = TimeZoneInfo.ConvertTime(now, AnaTime.Zone).DateTime;
        TimeOnly time = TimeOnly.FromDateTime(local);
        List<BranchIngestDTO> open = [.. (await _docs.ListAsync<BranchIngestDTO>(AnaCollections.Branch, null, ct).ConfigureAwait(false)).Select(d => d.Value)
            .Where(b => b.Active && InScope(r, b.BranchCode) && !b.ClosedWeekdays.Contains(local.DayOfWeek)
                && TimeOnly.TryParse(b.OpenTime, CultureInfo.InvariantCulture, out TimeOnly o) && TimeOnly.TryParse(b.CloseTime, CultureInfo.InvariantCulture, out TimeOnly c)
                && time >= o.AddMinutes((double)minutes) && time <= c)];
        if (open.Count == 0)
        {
            return [];
        }

        var last = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        DateOnly today = AnaTime.Day(now);
        (DateTimeOffset f, DateTimeOffset t) = AnaTime.Range(today, today);
        long after = 0;
        while (true)
        {
            IReadOnlyList<SalesTransactionEntity> page = await _source.ListTransactionsAsync(f, t, after, 1000, ct).ConfigureAwait(false);
            foreach (SalesTransactionEntity x in page.Where(x => x.Channel == SalesChannel.Pos && x.BranchCode is not null))
            {
                if (!last.TryGetValue(x.BranchCode!, out DateTimeOffset l) || x.OccurredAt > l)
                {
                    last[x.BranchCode!] = x.OccurredAt;
                }
            }

            if (page.Count < 1000)
            {
                break;
            }

            after = page[^1].Id;
        }

        return [.. open.Select(b =>
        {
            DateTimeOffset since = last.TryGetValue(b.BranchCode, out DateTimeOffset l) ? l
                : AnaTime.Start(today).AddHours(TimeOnly.Parse(b.OpenTime!, CultureInfo.InvariantCulture).ToTimeSpan().TotalHours);
            decimal gap = Math.Round((decimal)(now - since).TotalMinutes, 0);
            return new AlertEvaluationDTO { ScopeKey = "branch:" + b.BranchCode, BranchCode = b.BranchCode, Value = gap, Threshold = minutes, WouldFire = gap > minutes };
        })];
    }

    /// <summary>งานเบื้องหลัง: ประเมินทุกกฎที่เปิด</summary>
    public async Task<int> EvaluateAllAsync(CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        List<AlertEventDTO> events = [.. (await _docs.ListAsync<AlertEventDTO>(AnaCollections.AlertEvent, null, ct).ConfigureAwait(false)).Select(d => d.Value)];
        int fired = 0;
        foreach (AlertRuleDTO rule in (await _docs.ListAsync<AlertRuleDTO>(AnaCollections.AlertRule, null, ct).ConfigureAwait(false)).Select(d => d.Value).Where(r => r.Enabled))
        {
            foreach (AlertEvaluationDTO e in await EvaluateRuleAsync(rule, ct).ConfigureAwait(false))
            {
                AlertEventDTO? last = events.Where(x => x.RuleId == rule.Id && x.ScopeKey == e.ScopeKey && x.State is AlertState.Fired or AlertState.Resolved or AlertState.Failed)
                    .OrderByDescending(x => x.OccurredAt).FirstOrDefault();
                bool active = last?.State is AlertState.Fired or AlertState.Failed;
                if (e.WouldFire)
                {
                    if (active && last!.NotifiedAt is DateTimeOffset n && now - n < TimeSpan.FromMinutes(rule.CooldownMinutes))
                    {
                        continue;
                    }

                    var ev = new AlertEventDTO
                    {
                        Id = Guid.NewGuid(), RuleId = rule.Id, RuleName = rule.Name, Type = rule.Type, ScopeKey = e.ScopeKey, Value = e.Value, Threshold = e.Threshold, OccurredAt = now,
                        Drilldown = e.Drilldown, BranchCode = e.BranchCode,
                    };
                    bool sent = await NotifyAsync(rule, ev, ct).ConfigureAwait(false);
                    ev.State = sent ? AlertState.Fired : AlertState.Failed;
                    ev.NotifiedAt = sent ? now : null;
                    await _docs.InsertAsync(AnaCollections.AlertEvent, ev.Id.ToString(), null, ev, now, ct).ConfigureAwait(false);
                    events.Add(ev);
                    fired++;
                }
                else if (active)
                {
                    var ev = new AlertEventDTO
                    {
                        Id = Guid.NewGuid(), RuleId = rule.Id, RuleName = rule.Name, Type = rule.Type, ScopeKey = e.ScopeKey, Value = e.Value, Threshold = e.Threshold, OccurredAt = now,
                        State = AlertState.Resolved, BranchCode = e.BranchCode,
                    };
                    await _docs.InsertAsync(AnaCollections.AlertEvent, ev.Id.ToString(), null, ev, now, ct).ConfigureAwait(false);
                    events.Add(ev);
                }
            }
        }

        await _docs.PurgeAsync(AnaCollections.AlertEvent, now.AddDays(-90), ct).ConfigureAwait(false);
        return fired;
    }

    private async Task<bool> NotifyAsync(AlertRuleDTO rule, AlertEventDTO e, CancellationToken ct)
    {
        bool ok = true;
        string subject = $"[แจ้งเตือน] {rule.Name}";
        string body = $"กฎ: {rule.Name} ({rule.Type})\nขอบเขต: {e.ScopeKey}\nค่า: {e.Value?.ToString(CultureInfo.InvariantCulture) ?? "-"} เกณฑ์: {e.Threshold.ToString(CultureInfo.InvariantCulture)}\nเวลา: {TimeZoneInfo.ConvertTime(e.OccurredAt, AnaTime.Zone):yyyy-MM-dd HH:mm}";
        foreach (AlertRecipientDTO to in rule.Recipients.Where(x => x.Channel == AlertChannel.Email))
        {
            try
            {
                ok &= await _hub.SendEmailAsync(to.To, subject, body, [], ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                LogNotifyFailed(_log, ex, rule.Id);
                ok = false;
            }
        }

        return ok;
    }

    [LoggerMessage(EventId = 4320, Level = LogLevel.Warning, Message = "Analytics alert notification failed (rule {RuleId})")]
    private static partial void LogNotifyFailed(ILogger logger, Exception ex, Guid ruleId);
}
