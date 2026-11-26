using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;

namespace CustomerMgnt.Core.MemberAnalytics.Services;

/// <summary>สถิติการรับข้อมูลของแหล่ง 1 แหล่ง (นับรายชั่วโมงย้อนหลัง 24 ชม.)</summary>
public sealed class AnaSourceStatsDoc
{
    public string Source { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public DateTimeOffset? LastReceivedAt { get; set; }
    /// <summary>ชั่วโมง (yyyyMMddHH) → (รับ, ปฏิเสธ)</summary>
    public Dictionary<string, int[]> Hours { get; set; } = [];
}

/// <summary>
/// รับข้อมูลจากระบบต้นทาง (§36): ทุกชุด ≤ 1,000 รายการ แต่ละรายการตรวจและบันทึกแยกกัน ตอบ 200 พร้อมผลรายรายการ
/// ส่งซ้ำ/มาช้า: รายการที่เก่ากว่าที่มีอยู่ (updatedAt / revision) = Stale ไม่ทับข้อมูลใหม่
/// </summary>
public sealed class AnalyticsIngestService
{
    public const int MaxBatch = 1000;
    private readonly IAnaDocumentDAO _docs;
    private readonly IAnaReadModelDAO _model;
    private readonly IAnaMessageDAO _messages;
    private readonly IMemberDAO _members;
    private readonly ICampaignDAO _campaigns;
    private readonly AnaSettingsProvider _settings;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public AnalyticsIngestService(IAnaDocumentDAO docs, IAnaReadModelDAO model, IAnaMessageDAO messages, IMemberDAO members, ICampaignDAO campaigns, AnaSettingsProvider settings,
        TimeProvider clock)
    {
        _docs = docs;
        _model = model;
        _messages = messages;
        _members = members;
        _campaigns = campaigns;
        _settings = settings;
        _clock = clock;
    }

    private static void Guard<T>(MembershipCaller caller, IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(items);
        caller.Require(AnaPermissions.Ingest);
        if (items.Count > MaxBatch)
        {
            throw AnaErrors.Bad("batch_too_large", $"A batch may contain at most {MaxBatch} items.");
        }
    }

    /// <summary>ประมวลผลทีละรายการ (ข้อผิดพลาดของรายการหนึ่งไม่กระทบรายการอื่น)</summary>
    private async Task<IngestResultDTO> RunAsync<T>(MembershipCaller caller, string source, IReadOnlyList<T> items, Func<T, string> key,
        Func<T, Task<(IngestItemStatus Status, string? Error)>> apply, CancellationToken ct)
    {
        var result = new IngestResultDTO { RequestId = caller.RequestId };
        for (int i = 0; i < items.Count; i++)
        {
            T item = items[i];
            string k = item is null ? string.Empty : key(item);
            var r = new IngestItemResultDTO { Index = i, Key = k };
            var errors = new List<ValidationResult>();
            if (item is null || !Validator.TryValidateObject(item, new ValidationContext(item), errors, validateAllProperties: true))
            {
                r.Status = IngestItemStatus.Rejected;
                r.Error = item is null ? "Item is null." : string.Join("; ", errors.Select(e => e.ErrorMessage));
            }
            else
            {
                (r.Status, r.Error) = await apply(item).ConfigureAwait(false);
            }

            if (r.Status is IngestItemStatus.Rejected or IngestItemStatus.Unknown)
            {
                await IssueAsync($"{source}.{r.Status.ToString().ToLowerInvariant()}", k, new { source, key = k, error = r.Error }, ct).ConfigureAwait(false);
            }

            result.Results.Add(r);
        }

        result.Accepted = result.Results.Count(x => x.Status == IngestItemStatus.Accepted);
        result.Stale = result.Results.Count(x => x.Status == IngestItemStatus.Stale);
        result.Unknown = result.Results.Count(x => x.Status == IngestItemStatus.Unknown);
        result.Rejected = result.Results.Count(x => x.Status == IngestItemStatus.Rejected);
        await StatsAsync(source, caller.SourceSystem, items.Count, result.Rejected + result.Unknown, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>บันทึกปัญหาข้อมูล (รวมรายการซ้ำเป็นจำนวนครั้ง)</summary>
    private async Task IssueAsync(string kind, string key, object sample, CancellationToken ct)
    {
        string id = $"{kind}:{key}";
        DateTimeOffset now = _clock.GetUtcNow();
        DataIssueDTO issue = (await _docs.GetAsync<DataIssueDTO>(AnaCollections.DataIssue, id, ct).ConfigureAwait(false))?.Value
            ?? new DataIssueDTO { Id = StableId(id), Kind = kind, Key = key, FirstSeen = now };
        issue.Count++;
        issue.LastSeen = now;
        issue.SampleJson = MembershipJson.Write(sample);
        await _docs.UpsertAsync(AnaCollections.DataIssue, id, null, issue, now, ct).ConfigureAwait(false);
    }

    private static long StableId(string s) => (long)(BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(s)), 0) & 0x7FFF_FFFF_FFFF_FFFF);

    private async Task StatsAsync(string source, string clientId, int received, int rejected, CancellationToken ct)
    {
        string id = $"{source}:{clientId}";
        DateTimeOffset now = _clock.GetUtcNow();
        AnaSourceStatsDoc s = (await _docs.GetAsync<AnaSourceStatsDoc>(AnaCollections.SourceStatus, id, ct).ConfigureAwait(false))?.Value
            ?? new AnaSourceStatsDoc { Source = source, ClientId = clientId };
        string hour = now.UtcDateTime.ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture);
        int[] h = s.Hours.GetValueOrDefault(hour) ?? [0, 0];
        s.Hours[hour] = [h[0] + received, h[1] + rejected];
        string cutoff = now.UtcDateTime.AddHours(-24).ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture);
        foreach (string old in s.Hours.Keys.Where(k => string.CompareOrdinal(k, cutoff) <= 0).ToList())
        {
            s.Hours.Remove(old);
        }

        s.LastReceivedAt = now;
        await _docs.UpsertAsync(AnaCollections.SourceStatus, id, null, s, now, ct).ConfigureAwait(false);
    }

    /// <summary>วันที่ต้องคำนวณยอดขายใหม่เมื่อ master เปลี่ยน (ย้อนหลัง recomputeDays)</summary>
    private async Task MarkRecentDirtyAsync(DateOnly? since, CancellationToken ct)
    {
        AnalyticsSettingsDTO s = await _settings.GetAsync(ct).ConfigureAwait(false);
        DateOnly today = AnaTime.Today(_clock);
        DateOnly from = today.AddDays(-s.RecomputeDays);
        if (since is DateOnly d && d > from)
        {
            from = d;
        }

        await _model.MarkDirtyAsync(AnaTables.Sales, [.. AnaTime.Days(from, today)], ct).ConfigureAwait(false);
    }

    /// <summary>PUT ingest/products</summary>
    public async Task<IngestResultDTO> ProductsAsync(MembershipCaller caller, IReadOnlyList<ProductIngestDTO> items, CancellationToken ct)
    {
        Guard(caller, items);
        bool changed = false;
        IngestResultDTO r = await RunAsync(caller, "products", items, x => x.ItemCode, async x =>
        {
            string id = x.ItemCode.Trim();
            AnaProductDTO? cur = (await _docs.GetAsync<AnaProductDTO>(AnaCollections.Product, id, ct).ConfigureAwait(false))?.Value;
            if (cur is not null && x.UpdatedAt < cur.UpdatedAt)
            {
                return (IngestItemStatus.Stale, "Older than the stored product.");
            }

            var p = cur ?? new AnaProductDTO { ItemCode = id };
            bool brandChanged = cur is null || !string.Equals(cur.Brand, x.Brand, StringComparison.Ordinal) || !string.Equals(cur.Category, x.Category, StringComparison.Ordinal);
            p.Name = x.Name;
            p.Brand = string.IsNullOrWhiteSpace(x.Brand) ? null : x.Brand.Trim();
            p.Category = string.IsNullOrWhiteSpace(x.Category) ? null : x.Category.Trim();
            p.Active = x.Active;
            p.UpdatedAt = x.UpdatedAt;
            if (x.UnitCost is decimal cost)
            {
                DateOnly from = x.CostEffectiveFrom ?? AnaTime.Day(x.UpdatedAt);
                p.Costs.RemoveAll(c => c.EffectiveFrom == from);
                p.Costs.Add(new ProductCostDTO { EffectiveFrom = from, UnitCost = cost });
                p.Costs = [.. p.Costs.OrderBy(c => c.EffectiveFrom)];
                changed = true;
            }

            changed |= brandChanged;
            await _docs.UpsertAsync(AnaCollections.Product, id, null, p, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            return (IngestItemStatus.Accepted, null);
        }, ct).ConfigureAwait(false);
        if (changed)
        {
            await MarkRecentDirtyAsync(null, ct).ConfigureAwait(false);
        }

        return r;
    }

    /// <summary>PUT ingest/branches</summary>
    public Task<IngestResultDTO> BranchesAsync(MembershipCaller caller, IReadOnlyList<BranchIngestDTO> items, CancellationToken ct)
    {
        Guard(caller, items);
        return RunAsync(caller, "branches", items, x => x.BranchCode, async x =>
        {
            string id = x.BranchCode.Trim();
            BranchIngestDTO? cur = (await _docs.GetAsync<BranchIngestDTO>(AnaCollections.Branch, id, ct).ConfigureAwait(false))?.Value;
            if (cur is not null && x.UpdatedAt < cur.UpdatedAt)
            {
                return (IngestItemStatus.Stale, "Older than the stored branch.");
            }

            x.BranchCode = id;
            await _docs.UpsertAsync(AnaCollections.Branch, id, null, x, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            return (IngestItemStatus.Accepted, (string?)null);
        }, ct);
    }

    /// <summary>POST ingest/campaign-costs (รายการ = แคมเปญ + วันที่ + ชนิด; revision มากกว่าจึงทับ)</summary>
    public Task<IngestResultDTO> CampaignCostsAsync(MembershipCaller caller, IReadOnlyList<CampaignCostIngestDTO> items, CancellationToken ct)
    {
        Guard(caller, items);
        return RunAsync(caller, "campaign-costs", items, Key, async x =>
        {
            x.CampaignCode = x.CampaignCode.Trim().ToUpperInvariant();
            if (await _campaigns.GetByCodeAsync(x.CampaignCode, ct).ConfigureAwait(false) is null)
            {
                return (IngestItemStatus.Unknown, "Unknown campaign code.");
            }

            string id = Key(x);
            CampaignCostIngestDTO? cur = (await _docs.GetAsync<CampaignCostIngestDTO>(AnaCollections.CampaignCost, id, ct).ConfigureAwait(false))?.Value;
            if (cur is not null && x.Revision <= cur.Revision)
            {
                return (IngestItemStatus.Stale, "Revision is not newer than the stored one.");
            }

            await _docs.UpsertAsync(AnaCollections.CampaignCost, id, null, x, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            return (IngestItemStatus.Accepted, (string?)null);
        }, ct);

        static string Key(CampaignCostIngestDTO x) => $"{x.CampaignCode.Trim().ToUpperInvariant()}:{x.CostDate:yyyy-MM-dd}:{x.CostType}";
    }

    /// <summary>POST ingest/message-events (eventId ซ้ำ = Stale; หา member/campaign จาก messageId ก่อน แล้วจึงใช้ memberNo)</summary>
    public Task<IngestResultDTO> MessageEventsAsync(MembershipCaller caller, IReadOnlyList<MessageEventIngestDTO> items, CancellationToken ct)
    {
        Guard(caller, items);
        return RunAsync(caller, "message-events", items, x => x.EventId.ToString(), async x =>
        {
            if (x.EventId == Guid.Empty)
            {
                return (IngestItemStatus.Rejected, "eventId is required.");
            }

            MessageRecipientDTO? rcpt = string.IsNullOrWhiteSpace(x.MessageId) ? null : await _messages.GetRecipientAsync(x.MessageId.Trim(), ct).ConfigureAwait(false);
            long? memberId = rcpt?.MemberId;
            if (memberId is null && !string.IsNullOrWhiteSpace(x.MemberNo))
            {
                memberId = (await _members.GetByNoAsync(x.MemberNo.Trim().ToUpperInvariant(), ct).ConfigureAwait(false))?.Id;
            }

            if (memberId is null)
            {
                return (IngestItemStatus.Unknown, "Unknown message or member.");
            }

            var e = new MessageEventDTO
            {
                EventId = x.EventId, Type = x.Type, MessageId = x.MessageId, CampaignCode = (x.CampaignCode ?? rcpt?.CampaignCode)?.Trim().ToUpperInvariant(), MemberId = memberId.Value,
                OccurredAt = x.OccurredAt,
            };
            return await _messages.TryInsertEventAsync(e, ct).ConfigureAwait(false) ? (IngestItemStatus.Accepted, null) : (IngestItemStatus.Stale, "Duplicate eventId.");
        }, ct);
    }

    /// <summary>POST ingest/sales-summary (รายการ = วัน + สาขา + ช่องทาง; revision มากกว่าจึงทับ)</summary>
    public Task<IngestResultDTO> SalesSummaryAsync(MembershipCaller caller, IReadOnlyList<SalesSummaryIngestDTO> items, CancellationToken ct)
    {
        Guard(caller, items);
        return RunAsync(caller, "sales-summary", items, Key, async x =>
        {
            string id = Key(x);
            SalesSummaryIngestDTO? cur = (await _docs.GetAsync<SalesSummaryIngestDTO>(AnaCollections.SalesSummary, id, ct).ConfigureAwait(false))?.Value;
            if (cur is not null && x.Revision <= cur.Revision)
            {
                return (IngestItemStatus.Stale, "Revision is not newer than the stored one.");
            }

            x.BranchCode = x.BranchCode.Trim();
            await _docs.UpsertAsync(AnaCollections.SalesSummary, id, null, x, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            return (IngestItemStatus.Accepted, (string?)null);
        }, ct);

        static string Key(SalesSummaryIngestDTO x) => $"{x.BusinessDate:yyyy-MM-dd}:{x.BranchCode.Trim()}:{x.Channel}";
    }
}
