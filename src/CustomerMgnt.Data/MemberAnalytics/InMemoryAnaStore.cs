using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Domain;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Services;

namespace CustomerMgnt.Data.MemberAnalytics;

/// <summary>read model / เอกสาร / เหตุการณ์ข้อความของระบบวิเคราะห์ในหน่วยความจำ (Development / test) — คืนสำเนาเสมอ</summary>
public sealed class InMemoryAnaStore : IAnaReadModelDAO, IAnaDocumentDAO, IAnaMessageDAO
{
    private readonly Lock _sync = new();
    private readonly Dictionary<DateOnly, List<AnaMemberDayDTO>> _memberDays = [];
    private readonly Dictionary<long, AnaMemberSnapshotDTO> _snapshots = [];
    private readonly Dictionary<DateOnly, List<AnaPointsDayDTO>> _pointsDays = [];
    private readonly Dictionary<DateOnly, List<AnaCouponDayDTO>> _couponDays = [];
    private readonly Dictionary<(string, DateOnly), DateTimeOffset> _dirty = [];
    private readonly Dictionary<(string, string), (string? Owner, long Version, DateTimeOffset UpdatedAt, string Json)> _docs = [];
    private readonly Dictionary<string, MessageRecipientDTO> _recipients = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, MessageEventDTO> _events = [];
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public InMemoryAnaStore(TimeProvider clock) => _clock = clock;

    private static T C<T>(T value) where T : new() => MembershipJson.Read<T>(MembershipJson.Write(value));

    private Task<T> Run<T>(Func<T> f)
    {
        lock (_sync)
        {
            return Task.FromResult(f());
        }
    }

    private Task Run(Action f)
    {
        lock (_sync)
        {
            f();
            return Task.CompletedTask;
        }
    }

    // ================================================================== read model

    public Task ReplaceMemberDaysAsync(DateOnly day, IReadOnlyList<AnaMemberDayDTO> rows, CancellationToken ct) => Run(() => _memberDays[day] = [.. rows.Select(C)]);

    public Task<IReadOnlyList<AnaMemberDayDTO>> ListMemberDaysAsync(DateOnly from, DateOnly to, IReadOnlyCollection<string>? branches, IReadOnlyCollection<SalesChannel>? channels,
        CancellationToken ct) => Run<IReadOnlyList<AnaMemberDayDTO>>(() => [.. _memberDays.Where(d => d.Key >= from && d.Key <= to).SelectMany(d => d.Value)
            .Where(r => (branches is null || branches.Contains(r.BranchCode, StringComparer.OrdinalIgnoreCase)) && (channels is null || channels.Contains(r.Channel)))
            .OrderBy(r => r.Day).ThenBy(r => r.MemberId).Select(C)]);

    public Task ReplaceSnapshotsAsync(IReadOnlyList<AnaMemberSnapshotDTO> rows, CancellationToken ct) => Run(() =>
    {
        _snapshots.Clear();
        foreach (AnaMemberSnapshotDTO r in rows)
        {
            _snapshots[r.MemberId] = C(r);
        }
    });

    public Task<IReadOnlyList<AnaMemberSnapshotDTO>> ListSnapshotsAsync(CancellationToken ct) =>
        Run<IReadOnlyList<AnaMemberSnapshotDTO>>(() => [.. _snapshots.Values.OrderBy(s => s.MemberId).Select(C)]);

    public Task ReplacePointsDayAsync(DateOnly day, IReadOnlyList<AnaPointsDayDTO> rows, CancellationToken ct) => Run(() => _pointsDays[day] = [.. rows.Select(C)]);

    public Task<IReadOnlyList<AnaPointsDayDTO>> ListPointsDaysAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        Run<IReadOnlyList<AnaPointsDayDTO>>(() => [.. _pointsDays.Where(d => d.Key >= from && d.Key <= to).SelectMany(d => d.Value).OrderBy(r => r.Day).Select(C)]);

    public Task ReplaceCouponDayAsync(DateOnly day, IReadOnlyList<AnaCouponDayDTO> rows, CancellationToken ct) => Run(() => _couponDays[day] = [.. rows.Select(C)]);

    public Task<IReadOnlyList<AnaCouponDayDTO>> ListCouponDaysAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        Run<IReadOnlyList<AnaCouponDayDTO>>(() => [.. _couponDays.Where(d => d.Key >= from && d.Key <= to).SelectMany(d => d.Value).OrderBy(r => r.Day).Select(C)]);

    public Task MarkDirtyAsync(string table, IReadOnlyCollection<DateOnly> days, CancellationToken ct) => Run(() =>
    {
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (DateOnly d in days)
        {
            _dirty[(table, d)] = now;
        }
    });

    public Task<IReadOnlyList<DateOnly>> ListDirtyAsync(string table, CancellationToken ct) =>
        Run<IReadOnlyList<DateOnly>>(() => [.. _dirty.Keys.Where(k => k.Item1 == table).Select(k => k.Item2).Order()]);

    public Task ClearDirtyAsync(string table, IReadOnlyCollection<DateOnly> days, DateTimeOffset markedBefore, CancellationToken ct) => Run(() =>
    {
        foreach (DateOnly d in days)
        {
            if (_dirty.TryGetValue((table, d), out DateTimeOffset at) && at < markedBefore)
            {
                _dirty.Remove((table, d));
            }
        }
    });

    // ================================================================== เอกสาร

    public Task<AnaDocument<T>?> GetAsync<T>(string collection, string id, CancellationToken ct) where T : new() =>
        Run(() => _docs.TryGetValue((collection, id), out var d) ? new AnaDocument<T>(id, d.Owner, d.Version, d.UpdatedAt, MembershipJson.Read<T>(d.Json)) : null);

    public Task<IReadOnlyList<AnaDocument<T>>> ListAsync<T>(string collection, string? owner, CancellationToken ct) where T : new() =>
        Run<IReadOnlyList<AnaDocument<T>>>(() => [.. _docs.Where(d => d.Key.Item1 == collection && (owner is null || string.Equals(d.Value.Owner, owner, StringComparison.Ordinal)))
            .OrderByDescending(d => d.Value.UpdatedAt)
            .Select(d => new AnaDocument<T>(d.Key.Item2, d.Value.Owner, d.Value.Version, d.Value.UpdatedAt, MembershipJson.Read<T>(d.Value.Json)))]);

    public Task<bool> InsertAsync<T>(string collection, string id, string? owner, T value, DateTimeOffset now, CancellationToken ct) => Run(() =>
    {
        if (_docs.ContainsKey((collection, id)))
        {
            return false;
        }

        _docs[(collection, id)] = (owner, 1, now, MembershipJson.Write(value));
        return true;
    });

    public Task<bool> UpdateAsync<T>(string collection, string id, string? owner, long expectedVersion, T value, DateTimeOffset now, CancellationToken ct) => Run(() =>
    {
        if (!_docs.TryGetValue((collection, id), out var d) || d.Version != expectedVersion)
        {
            return false;
        }

        _docs[(collection, id)] = (owner, d.Version + 1, now, MembershipJson.Write(value));
        return true;
    });

    public Task UpsertAsync<T>(string collection, string id, string? owner, T value, DateTimeOffset now, CancellationToken ct) => Run(() =>
    {
        long v = _docs.TryGetValue((collection, id), out var d) ? d.Version + 1 : 1;
        _docs[(collection, id)] = (owner, v, now, MembershipJson.Write(value));
    });

    public Task<bool> DeleteAsync(string collection, string id, CancellationToken ct) => Run(() => _docs.Remove((collection, id)));

    public Task<int> PurgeAsync(string collection, DateTimeOffset before, CancellationToken ct) => Run(() =>
    {
        var keys = _docs.Where(d => d.Key.Item1 == collection && d.Value.UpdatedAt < before).Select(d => d.Key).ToList();
        foreach (var k in keys)
        {
            _docs.Remove(k);
        }

        return keys.Count;
    });

    // ================================================================== ข้อความ

    public Task InsertRecipientsAsync(IReadOnlyList<MessageRecipientDTO> recipients, CancellationToken ct) => Run(() =>
    {
        foreach (MessageRecipientDTO r in recipients)
        {
            _recipients.TryAdd(r.MessageId, C(r));
        }
    });

    public Task<MessageRecipientDTO?> GetRecipientAsync(string messageId, CancellationToken ct) => Run(() => _recipients.TryGetValue(messageId, out MessageRecipientDTO? r) ? C(r) : null);

    public Task<IReadOnlyList<MessageRecipientDTO>> ListRecipientsAsync(string? campaignCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Run<IReadOnlyList<MessageRecipientDTO>>(() => [.. _recipients.Values
            .Where(r => (campaignCode is null || string.Equals(r.CampaignCode, campaignCode, StringComparison.OrdinalIgnoreCase)) && r.SentAt >= from && r.SentAt < to).Select(C)]);

    public Task<bool> TryInsertEventAsync(MessageEventDTO e, CancellationToken ct) => Run(() => _events.TryAdd(e.EventId, C(e)));

    public Task<IReadOnlyList<MessageEventDTO>> ListEventsAsync(string? campaignCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Run<IReadOnlyList<MessageEventDTO>>(() => [.. _events.Values
            .Where(e => (campaignCode is null || string.Equals(e.CampaignCode, campaignCode, StringComparison.OrdinalIgnoreCase)) && e.OccurredAt >= from && e.OccurredAt < to)
            .OrderBy(e => e.OccurredAt).Select(C)]);
}
