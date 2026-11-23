using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;

namespace CustomerMgnt.Data.InMemory;

// Implementation ในหน่วยความจำของทุก DAO — พฤติกรรมเหมือน ADO (version, unique, idempotency) เพื่อให้เทสต์ service ได้ผลตรงกับฐานข้อมูลจริง

/// <summary>ลูกค้า (in-memory)</summary>
/// <param name="db">ฐานข้อมูลในหน่วยความจำ</param>
public sealed class InMemoryCustomerADO(InMemoryDatabase db) : ICustomerDAO
{
    /// <inheritdoc />
    public Task<long> InsertAsync(CustomerDTO customer, CancellationToken ct)
    {
        lock (db.Sync)
        {
            // Step 1: unique code / tax hash
            if (db.Customers.Exists(c => c.Code == customer.Code) || (customer.TaxIdHash is not null && db.Customers.Exists(c => c.TaxIdHash == customer.TaxIdHash)))
            {
                throw new ConflictException("The record already exists.");
            }

            // Step 2: เก็บสำเนา
            CustomerDTO copy = InMemoryDatabase.Copy(customer);
            copy.Id = db.NextId();
            db.Customers.Add(copy);
            return Task.FromResult(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(CustomerDTO customer, CancellationToken ct)
    {
        lock (db.Sync)
        {
            int i = db.Customers.FindIndex(c => c.Id == customer.Id);
            if (i < 0 || db.Customers[i].Version != customer.Version) return Task.FromResult(false);
            if (db.Customers.Exists(c => c.Id != customer.Id && c.Code == customer.Code)) throw new ConflictException("The record already exists.");
            CustomerDTO copy = InMemoryDatabase.Copy(customer);
            copy.Version++;
            db.Customers[i] = copy;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<CustomerDTO?> GetAsync(long id, CancellationToken ct) => Find(c => c.Id == id);

    /// <inheritdoc />
    public Task<CustomerDTO?> GetByCodeAsync(string code, CancellationToken ct) => Find(c => c.Code == code);

    /// <inheritdoc />
    public Task<CustomerDTO?> FindByTaxIdHashAsync(string taxIdHash, CancellationToken ct) => Find(c => c.TaxIdHash == taxIdHash);

    /// <inheritdoc />
    public Task<PagedResultDTO<CustomerDTO>> SearchAsync(CustomerSearchCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        lock (db.Sync)
        {
            // Step 1: กรองตามเงื่อนไข (ค่า null = ไม่กรอง)
            string? text = string.IsNullOrWhiteSpace(criteria.Text) ? null : criteria.Text.Trim();
            List<CustomerDTO> all = db.Customers.Where(c =>
                    (text is null || Contains(c.DisplayName, text) || Contains(c.Code, text) || Contains(c.Email, text) || Contains(c.LegalName, text))
                    && (criteria.Kind is null || c.Kind == criteria.Kind) && (criteria.Status is null || c.Status == criteria.Status)
                    && (criteria.OwnerSubject is null || c.OwnerSubject == criteria.OwnerSubject) && (criteria.Segment is null || c.Segment == criteria.Segment))
                .OrderBy(c => c.DisplayName, StringComparer.CurrentCulture).ThenBy(c => c.Id).ToList();

            // Step 2: แบ่งหน้า
            int page = Math.Max(1, criteria.Page), size = Math.Clamp(criteria.PageSize, 1, 500);
            return Task.FromResult(new PagedResultDTO<CustomerDTO>(all.Skip((page - 1) * size).Take(size).Select(InMemoryDatabase.Copy).ToList(), all.Count, page, size));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CustomerDTO>> GetManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var set = ids.ToHashSet();
        return List(c => set.Contains(c.Id));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CustomerDTO>> ListBatchAsync(long afterId, int take, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<CustomerDTO>>(db.Customers.Where(c => c.Id > afterId).OrderBy(c => c.Id).Take(Math.Clamp(take, 1, 10_000)).Select(InMemoryDatabase.Copy).ToList());
        }
    }

    /// <inheritdoc />
    public Task UpdateSegmentsAsync(IReadOnlyDictionary<long, string> segments, CancellationToken ct)
    {
        lock (db.Sync)
        {
            foreach (CustomerDTO c in db.Customers)
            {
                if (segments.TryGetValue(c.Id, out string? s)) c.Segment = s;
            }
        }

        return Task.CompletedTask;
    }

    private static bool Contains(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private Task<CustomerDTO?> Find(Predicate<CustomerDTO> match)
    {
        lock (db.Sync)
        {
            CustomerDTO? c = db.Customers.Find(match);
            return Task.FromResult(c is null ? null : InMemoryDatabase.Copy(c));
        }
    }

    private Task<IReadOnlyList<CustomerDTO>> List(Func<CustomerDTO, bool> match)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<CustomerDTO>>(db.Customers.Where(match).Select(InMemoryDatabase.Copy).ToList());
        }
    }
}

/// <summary>ความสัมพันธ์และตัวตนในช่องทาง (in-memory)</summary>
/// <param name="db">ฐานข้อมูลในหน่วยความจำ</param>
public sealed class InMemoryRelationshipADO(InMemoryDatabase db) : IRelationshipDAO
{
    /// <inheritdoc />
    public Task<long> InsertAsync(PartyRelationshipDTO relationship, CancellationToken ct)
    {
        lock (db.Sync)
        {
            PartyRelationshipDTO copy = InMemoryDatabase.Copy(relationship);
            copy.Id = db.NextId();
            db.Relationships.Add(copy);
            return Task.FromResult(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task<bool> EndAsync(long id, DateOnly validTo, CancellationToken ct)
    {
        lock (db.Sync)
        {
            PartyRelationshipDTO? r = db.Relationships.Find(x => x.Id == id);
            if (r is null || r.ValidFrom > validTo) return Task.FromResult(false);
            r.ValidTo = validTo;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<PartyRelationshipDTO?> GetAsync(long id, CancellationToken ct)
    {
        lock (db.Sync)
        {
            PartyRelationshipDTO? r = db.Relationships.Find(x => x.Id == id);
            return Task.FromResult(r is null ? null : WithNames(r));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PartyRelationshipDTO>> ListByCustomerAsync(long customerId, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<PartyRelationshipDTO>>(db.Relationships.Where(r => r.PersonId == customerId || r.OrganizationId == customerId)
                .OrderBy(r => r.ValidTo is null ? 0 : 1).ThenByDescending(r => r.ValidFrom).Select(WithNames).ToList());
        }
    }

    /// <inheritdoc />
    public Task<long> InsertIdentityAsync(ChannelIdentityDTO identity, CancellationToken ct)
    {
        lock (db.Sync)
        {
            if (db.Identities.Exists(i => i.Channel == identity.Channel && i.ExternalId == identity.ExternalId)) throw new ConflictException("The record already exists.");
            ChannelIdentityDTO copy = InMemoryDatabase.Copy(identity);
            copy.Id = db.NextId();
            db.Identities.Add(copy);
            return Task.FromResult(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task<ChannelIdentityDTO?> FindIdentityAsync(ChannelKind channel, string externalId, CancellationToken ct)
    {
        lock (db.Sync)
        {
            ChannelIdentityDTO? i = db.Identities.Find(x => x.Channel == channel && x.ExternalId == externalId);
            return Task.FromResult(i is null ? null : InMemoryDatabase.Copy(i));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChannelIdentityDTO>> ListIdentitiesAsync(long customerId, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<ChannelIdentityDTO>>(db.Identities.Where(i => i.CustomerId == customerId).OrderBy(i => i.Channel).ThenBy(i => i.Id).Select(InMemoryDatabase.Copy).ToList());
        }
    }

    private PartyRelationshipDTO WithNames(PartyRelationshipDTO r)
    {
        PartyRelationshipDTO copy = InMemoryDatabase.Copy(r);
        copy.PersonName = db.CustomerName(r.PersonId);
        copy.OrganizationName = db.CustomerName(r.OrganizationId);
        return copy;
    }
}

/// <summary>ห้องสนทนาและข้อความ (in-memory)</summary>
/// <param name="db">ฐานข้อมูลในหน่วยความจำ</param>
public sealed class InMemoryConversationADO(InMemoryDatabase db) : IConversationDAO
{
    /// <inheritdoc />
    public Task<ConversationDTO?> FindOpenAsync(ChannelKind channel, string externalUserId, CancellationToken ct)
    {
        lock (db.Sync)
        {
            ConversationDTO? c = db.Conversations.Find(x => x.Channel == channel && x.ExternalUserId == externalUserId && x.Status != ConversationStatus.Resolved);
            return Task.FromResult(c is null ? null : WithName(c));
        }
    }

    /// <inheritdoc />
    public Task<long> InsertAsync(ConversationDTO conversation, CancellationToken ct)
    {
        lock (db.Sync)
        {
            ConversationDTO copy = InMemoryDatabase.Copy(conversation);
            copy.Id = db.NextId();
            db.Conversations.Add(copy);
            return Task.FromResult(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task UpdateAsync(ConversationDTO conversation, CancellationToken ct)
    {
        lock (db.Sync)
        {
            int i = db.Conversations.FindIndex(x => x.Id == conversation.Id);
            if (i >= 0) db.Conversations[i] = InMemoryDatabase.Copy(conversation);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<ConversationDTO?> GetAsync(long id, CancellationToken ct)
    {
        lock (db.Sync)
        {
            ConversationDTO? c = db.Conversations.Find(x => x.Id == id);
            return Task.FromResult(c is null ? null : WithName(c));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationDTO>> ListAsync(ConversationStatus? status, long? customerId, bool needsAttentionOnly, int take, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<ConversationDTO>>(db.Conversations
                .Where(c => (status is null || c.Status == status) && (customerId is null || c.CustomerId == customerId) && (!needsAttentionOnly || c.NeedsAttention))
                .OrderByDescending(c => c.NeedsAttention).ThenByDescending(c => c.LastMessageAt).Take(Math.Clamp(take, 1, 1_000)).Select(WithName).ToList());
        }
    }

    /// <inheritdoc />
    public Task<long?> InsertMessageAsync(ChatMessageDTO message, CancellationToken ct)
    {
        lock (db.Sync)
        {
            // Step 1: idempotency ตาม external message id
            if (message.ExternalMessageId is not null && db.Messages.Exists(m => m.ConversationId == message.ConversationId && m.ExternalMessageId == message.ExternalMessageId))
            {
                return Task.FromResult<long?>(null);
            }

            ChatMessageDTO copy = InMemoryDatabase.Copy(message);
            copy.Id = db.NextId();
            db.Messages.Add(copy);
            return Task.FromResult<long?>(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessageDTO>> ListMessagesAsync(long conversationId, int take, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<ChatMessageDTO>>(db.Messages.Where(m => m.ConversationId == conversationId)
                .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).Take(Math.Clamp(take, 1, 1_000)).OrderBy(m => m.SentAt).ThenBy(m => m.Id).Select(InMemoryDatabase.Copy).ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, (double Average, int Count)>> SentimentByCustomerAsync(IReadOnlyCollection<long> customerIds, DateTimeOffset since, CancellationToken ct)
    {
        lock (db.Sync)
        {
            var ids = customerIds.ToHashSet();
            Dictionary<long, long?> convCustomer = db.Conversations.ToDictionary(c => c.Id, c => c.CustomerId);
            return Task.FromResult<IReadOnlyDictionary<long, (double Average, int Count)>>(db.Messages
                .Where(m => m.Direction == MessageDirection.Inbound && m.SentAt >= since && convCustomer.GetValueOrDefault(m.ConversationId) is long cid && ids.Contains(cid))
                .GroupBy(m => convCustomer[m.ConversationId]!.Value)
                .ToDictionary(g => g.Key, g => (g.Average(m => m.Sentiment), g.Count())));
        }
    }

    private ConversationDTO WithName(ConversationDTO c)
    {
        ConversationDTO copy = InMemoryDatabase.Copy(c);
        copy.CustomerName = db.CustomerName(c.CustomerId);
        return copy;
    }
}

/// <summary>กิจกรรม (in-memory)</summary>
/// <param name="db">ฐานข้อมูลในหน่วยความจำ</param>
/// <param name="clock">นาฬิกา (ใช้คำนวณช่วง 90 วัน)</param>
public sealed class InMemoryActivityADO(InMemoryDatabase db, TimeProvider clock) : IActivityDAO
{
    /// <inheritdoc />
    public Task<long> InsertAsync(ActivityDTO activity, CancellationToken ct)
    {
        lock (db.Sync)
        {
            ActivityDTO copy = InMemoryDatabase.Copy(activity);
            copy.Id = db.NextId();
            db.Activities.Add(copy);
            return Task.FromResult(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task UpdateAsync(ActivityDTO activity, CancellationToken ct)
    {
        lock (db.Sync)
        {
            int i = db.Activities.FindIndex(x => x.Id == activity.Id);
            if (i >= 0) db.Activities[i] = InMemoryDatabase.Copy(activity);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<ActivityDTO?> GetAsync(long id, CancellationToken ct)
    {
        lock (db.Sync)
        {
            ActivityDTO? a = db.Activities.Find(x => x.Id == id);
            return Task.FromResult(a is null ? null : InMemoryDatabase.Copy(a));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ActivityDTO>> ListByCustomerAsync(long customerId, bool openOnly, int take, CancellationToken ct) =>
        List(a => a.CustomerId == customerId && (!openOnly || a.Status == ActivityStatus.Planned), q => q.OrderByDescending(a => a.DueAt), take);

    /// <inheritdoc />
    public Task<IReadOnlyList<ActivityDTO>> ListOpenByOwnerAsync(string ownerSubject, DateTimeOffset until, CancellationToken ct) =>
        List(a => a.OwnerSubject == ownerSubject && a.Status == ActivityStatus.Planned && a.DueAt <= until, q => q.OrderBy(a => a.DueAt), 1_000);

    /// <inheritdoc />
    public Task<IReadOnlyList<ActivityDTO>> ListDueRemindersAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        List(a => a.Status == ActivityStatus.Planned && a.ReminderSentAt is null && a.RemindBeforeMinutes is { } m && a.DueAt.AddMinutes(-m) <= now && a.DueAt > now.AddDays(-1),
            q => q.OrderBy(a => a.DueAt), take);

    /// <inheritdoc />
    public Task<bool> TryMarkReminderSentAsync(long id, DateTimeOffset sentAt, CancellationToken ct)
    {
        lock (db.Sync)
        {
            ActivityDTO? a = db.Activities.Find(x => x.Id == id);
            if (a is null || a.ReminderSentAt is not null) return Task.FromResult(false);
            a.ReminderSentAt = sentAt;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, (DateTimeOffset? LastContactAt, int Done90Days)>> ContactStatsAsync(IReadOnlyCollection<long> customerIds, CancellationToken ct)
    {
        lock (db.Sync)
        {
            var ids = customerIds.ToHashSet();
            DateTimeOffset d90 = clock.GetUtcNow().AddDays(-90);
            return Task.FromResult<IReadOnlyDictionary<long, (DateTimeOffset? LastContactAt, int Done90Days)>>(db.Activities
                .Where(a => ids.Contains(a.CustomerId) && a.Status == ActivityStatus.Done)
                .GroupBy(a => a.CustomerId)
                .ToDictionary(g => g.Key, g => (g.Max(a => a.CompletedAt), g.Count(a => a.CompletedAt >= d90))));
        }
    }

    private Task<IReadOnlyList<ActivityDTO>> List(Func<ActivityDTO, bool> match, Func<IEnumerable<ActivityDTO>, IEnumerable<ActivityDTO>> order, int take)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<ActivityDTO>>(order(db.Activities.Where(match)).Take(Math.Clamp(take, 1, 5_000)).Select(InMemoryDatabase.Copy).ToList());
        }
    }
}

/// <summary>คำขอ/TOR (in-memory)</summary>
/// <param name="db">ฐานข้อมูลในหน่วยความจำ</param>
public sealed class InMemoryCustomerRequestADO(InMemoryDatabase db) : ICustomerRequestDAO
{
    /// <inheritdoc />
    public Task<long> InsertAsync(CustomerRequestDTO request, CancellationToken ct)
    {
        lock (db.Sync)
        {
            if (db.Requests.Exists(r => r.RequestNo == request.RequestNo)) throw new ConflictException("The record already exists.");
            CustomerRequestDTO copy = Clone(request);
            copy.Id = db.NextId();
            foreach (TorItemDTO i in copy.Items)
            {
                i.Id = db.NextId();
                i.RequestId = copy.Id;
            }

            db.Requests.Add(copy);
            return Task.FromResult(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(CustomerRequestDTO request, CancellationToken ct)
    {
        lock (db.Sync)
        {
            int idx = db.Requests.FindIndex(r => r.Id == request.Id);
            if (idx < 0 || db.Requests[idx].Version != request.Version) return Task.FromResult(false);

            // คงรายการเดิม อัปเดตเฉพาะ requirement key (เหมือน ADO)
            CustomerRequestDTO old = db.Requests[idx];
            CustomerRequestDTO copy = Clone(request);
            copy.Items = old.Items.Select(i =>
            {
                TorItemDTO c = InMemoryDatabase.Copy(i);
                c.RequirementKey = request.Items.Find(x => x.Id == i.Id)?.RequirementKey ?? i.RequirementKey;
                return c;
            }).ToList();
            copy.Version++;
            db.Requests[idx] = copy;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<CustomerRequestDTO?> GetAsync(long id, CancellationToken ct)
    {
        lock (db.Sync)
        {
            CustomerRequestDTO? r = db.Requests.Find(x => x.Id == id);
            return Task.FromResult(r is null ? null : Clone(r));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CustomerRequestDTO>> ListAsync(long? customerId, RequestStatus? status, int take, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<CustomerRequestDTO>>(db.Requests.Where(r => (customerId is null || r.CustomerId == customerId) && (status is null || r.Status == status))
                .OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(take, 1, 1_000)).Select(r => { CustomerRequestDTO c = InMemoryDatabase.Copy(r); c.Items = []; return c; }).ToList());
        }
    }

    /// <inheritdoc />
    public Task<int> NextSequenceAsync(int year, CancellationToken ct)
    {
        lock (db.Sync)
        {
            int next = db.RequestSequences.GetValueOrDefault(year) + 1;
            db.RequestSequences[year] = next;
            return Task.FromResult(next);
        }
    }

    private static CustomerRequestDTO Clone(CustomerRequestDTO r)
    {
        CustomerRequestDTO c = InMemoryDatabase.Copy(r);
        c.Items = r.Items.Select(InMemoryDatabase.Copy).ToList();
        return c;
    }
}

/// <summary>โอกาสการขาย (in-memory)</summary>
/// <param name="db">ฐานข้อมูลในหน่วยความจำ</param>
public sealed class InMemoryOpportunityADO(InMemoryDatabase db) : IOpportunityDAO
{
    /// <inheritdoc />
    public Task<long> InsertAsync(OpportunityDTO opportunity, CancellationToken ct)
    {
        lock (db.Sync)
        {
            OpportunityDTO copy = InMemoryDatabase.Copy(opportunity);
            copy.Id = db.NextId();
            db.Opportunities.Add(copy);
            return Task.FromResult(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(OpportunityDTO opportunity, StageHistoryDTO? history, CancellationToken ct)
    {
        lock (db.Sync)
        {
            int i = db.Opportunities.FindIndex(x => x.Id == opportunity.Id);
            if (i < 0 || db.Opportunities[i].Version != opportunity.Version) return Task.FromResult(false);
            OpportunityDTO copy = InMemoryDatabase.Copy(opportunity);
            copy.Version++;
            db.Opportunities[i] = copy;
            if (history is not null)
            {
                StageHistoryDTO h = InMemoryDatabase.Copy(history);
                h.Id = db.NextId();
                h.OpportunityId = opportunity.Id;
                db.StageHistory.Add(h);
            }

            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<OpportunityDTO?> GetAsync(long id, CancellationToken ct)
    {
        lock (db.Sync)
        {
            OpportunityDTO? o = db.Opportunities.Find(x => x.Id == id);
            return Task.FromResult(o is null ? null : WithName(o));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OpportunityDTO>> ListAsync(long? customerId, bool openOnly, string? ownerSubject, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<OpportunityDTO>>(db.Opportunities
                .Where(o => (customerId is null || o.CustomerId == customerId) && (!openOnly || !o.IsClosed) && (ownerSubject is null || o.OwnerSubject == ownerSubject))
                .OrderBy(o => o.ExpectedCloseDate).ThenBy(o => o.Id).Select(WithName).ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OpportunityDTO>> ListClosedAsync(DateTimeOffset since, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<OpportunityDTO>>(db.Opportunities.Where(o => o.IsClosed && o.ClosedAt >= since).OrderBy(o => o.ClosedAt).Select(WithName).ToList());
        }
    }

    /// <inheritdoc />
    public Task UpdateScoresAsync(IReadOnlyDictionary<long, double> scores, CancellationToken ct)
    {
        lock (db.Sync)
        {
            foreach (OpportunityDTO o in db.Opportunities)
            {
                if (scores.TryGetValue(o.Id, out double s)) o.Score = s;
            }
        }

        return Task.CompletedTask;
    }

    private OpportunityDTO WithName(OpportunityDTO o)
    {
        OpportunityDTO copy = InMemoryDatabase.Copy(o);
        copy.CustomerName = db.CustomerName(o.CustomerId);
        return copy;
    }
}

/// <summary>นโยบาย SLA (in-memory)</summary>
/// <param name="db">ฐานข้อมูลในหน่วยความจำ</param>
public sealed class InMemorySlaADO(InMemoryDatabase db) : ISlaDAO
{
    /// <inheritdoc />
    public Task<long> InsertAsync(SlaPolicyDTO policy, CancellationToken ct)
    {
        lock (db.Sync)
        {
            EnsureUniqueCodes(policy);
            SlaPolicyDTO copy = Clone(policy);
            copy.Id = db.NextId();
            copy.Version = 1;
            AssignLevelIds(copy);
            db.SlaPolicies.Add(copy);
            return Task.FromResult(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(SlaPolicyDTO policy, CancellationToken ct)
    {
        lock (db.Sync)
        {
            int i = db.SlaPolicies.FindIndex(x => x.Id == policy.Id);
            if (i < 0 || db.SlaPolicies[i].Version != policy.Version) return Task.FromResult(false);
            EnsureUniqueCodes(policy);
            SlaPolicyDTO copy = Clone(policy);
            copy.CustomerId = db.SlaPolicies[i].CustomerId;
            copy.Version++;
            AssignLevelIds(copy);
            db.SlaPolicies[i] = copy;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<SlaPolicyDTO?> GetAsync(long id, CancellationToken ct)
    {
        lock (db.Sync)
        {
            SlaPolicyDTO? p = db.SlaPolicies.Find(x => x.Id == id);
            return Task.FromResult(p is null ? null : Clone(p));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SlaPolicyDTO>> ListByCustomerAsync(long? customerId, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<SlaPolicyDTO>>(db.SlaPolicies.Where(p => p.CustomerId == customerId).OrderByDescending(p => p.ValidFrom).ThenByDescending(p => p.Id).Select(Clone).ToList());
        }
    }

    private static void EnsureUniqueCodes(SlaPolicyDTO policy)
    {
        if (policy.Levels.GroupBy(l => l.Code, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new ConflictException("The record already exists.");
    }

    private void AssignLevelIds(SlaPolicyDTO p)
    {
        foreach (SeverityLevelDTO l in p.Levels)
        {
            l.Id = db.NextId();
            l.PolicyId = p.Id;
        }
    }

    private static SlaPolicyDTO Clone(SlaPolicyDTO p)
    {
        SlaPolicyDTO c = InMemoryDatabase.Copy(p);
        c.Levels = p.Levels.OrderBy(l => l.Rank).ThenBy(l => l.Code, StringComparer.Ordinal).Select(InMemoryDatabase.Copy).ToList();
        return c;
    }
}

/// <summary>ลิงก์ภายนอก (in-memory)</summary>
/// <param name="db">ฐานข้อมูลในหน่วยความจำ</param>
public sealed class InMemoryExternalLinkADO(InMemoryDatabase db) : IExternalLinkDAO
{
    /// <inheritdoc />
    public Task<long> UpsertAsync(ExternalLinkDTO link, CancellationToken ct)
    {
        lock (db.Sync)
        {
            ExternalLinkDTO? existing = db.Links.Find(l => l.CustomerId == link.CustomerId && l.System == link.System && l.EntityType == link.EntityType && l.ExternalId == link.ExternalId);
            if (existing is not null)
            {
                existing.Title = link.Title ?? existing.Title;
                existing.CustomerRequestId ??= link.CustomerRequestId;
                return Task.FromResult(existing.Id);
            }

            ExternalLinkDTO copy = InMemoryDatabase.Copy(link);
            copy.Id = db.NextId();
            db.Links.Add(copy);
            return Task.FromResult(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ExternalLinkDTO>> ListAsync(long customerId, ExternalSystem? system, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<ExternalLinkDTO>>(db.Links.Where(l => l.CustomerId == customerId && (system is null || l.System == system)).OrderByDescending(l => l.CreatedAt).Select(InMemoryDatabase.Copy).ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ExternalLinkDTO>> ListByRequestAsync(long requestId, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<ExternalLinkDTO>>(db.Links.Where(l => l.CustomerRequestId == requestId).OrderBy(l => l.Id).Select(InMemoryDatabase.Copy).ToList());
        }
    }
}

/// <summary>พฤติกรรม สัญญา แคตตาล็อก (in-memory)</summary>
/// <param name="db">ฐานข้อมูลในหน่วยความจำ</param>
public sealed class InMemoryEngagementADO(InMemoryDatabase db) : IEngagementDAO
{
    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");

    /// <inheritdoc />
    public Task<int> InsertManyAsync(IReadOnlyList<EngagementEventDTO> events, CancellationToken ct)
    {
        lock (db.Sync)
        {
            foreach (EngagementEventDTO e in events)
            {
                EngagementEventDTO copy = InMemoryDatabase.Copy(e);
                copy.Id = db.NextId();
                db.Events.Add(copy);
            }

            return Task.FromResult(events.Count);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EngagementEventDTO>> ListAsync(IReadOnlyCollection<long> customerIds, DateTimeOffset since, IReadOnlyCollection<EngagementKind>? kinds, CancellationToken ct)
    {
        lock (db.Sync)
        {
            var ids = customerIds.ToHashSet();
            return Task.FromResult<IReadOnlyList<EngagementEventDTO>>(db.Events
                .Where(e => ids.Contains(e.CustomerId) && e.OccurredAt >= since && (kinds is null || kinds.Count == 0 || kinds.Contains(e.Kind)))
                .Select(InMemoryDatabase.Copy).ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<DateOnly, decimal>> MonthlyPurchasesAsync(long? customerId, DateTimeOffset since, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyDictionary<DateOnly, decimal>>(db.Events
                .Where(e => e.Kind == EngagementKind.Purchase && e.OccurredAt >= since && (customerId is null || e.CustomerId == customerId))
                .GroupBy(e =>
                {
                    DateTimeOffset local = TimeZoneInfo.ConvertTime(e.OccurredAt, Bangkok);
                    return new DateOnly(local.Year, local.Month, 1);
                })
                .ToDictionary(g => g.Key, g => g.Sum(e => e.Value)));
        }
    }

    /// <inheritdoc />
    public Task<long> InsertContractAsync(ContractDTO contract, CancellationToken ct)
    {
        lock (db.Sync)
        {
            if (db.Contracts.Exists(c => c.ContractNo == contract.ContractNo)) throw new ConflictException("The record already exists.");
            ContractDTO copy = InMemoryDatabase.Copy(contract);
            copy.Id = db.NextId();
            db.Contracts.Add(copy);
            return Task.FromResult(copy.Id);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContractDTO>> ListContractsEndingAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        Contracts(c => c.EndDate >= from && c.EndDate <= to, ascending: true);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContractDTO>> ListClosedContractsAsync(CancellationToken ct) =>
        Contracts(c => c.Status != ContractStatus.Active, ascending: false);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContractDTO>> ListContractsAsync(long customerId, CancellationToken ct) =>
        Contracts(c => c.CustomerId == customerId, ascending: false);

    /// <inheritdoc />
    public Task<IReadOnlyList<CatalogItemDTO>> ListCatalogAsync(CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult<IReadOnlyList<CatalogItemDTO>>(db.Catalog.Values.OrderBy(i => i.Code, StringComparer.Ordinal).Select(InMemoryDatabase.Copy).ToList());
        }
    }

    /// <inheritdoc />
    public Task UpsertCatalogAsync(CatalogItemDTO item, CancellationToken ct)
    {
        lock (db.Sync)
        {
            db.Catalog[item.Code] = InMemoryDatabase.Copy(item);
        }

        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<ContractDTO>> Contracts(Func<ContractDTO, bool> match, bool ascending)
    {
        lock (db.Sync)
        {
            IEnumerable<ContractDTO> q = db.Contracts.Where(match);
            q = ascending ? q.OrderBy(c => c.EndDate) : q.OrderByDescending(c => c.EndDate);
            return Task.FromResult<IReadOnlyList<ContractDTO>>(q.Select(InMemoryDatabase.Copy).ToList());
        }
    }
}

/// <summary>โปรไฟล์ผู้ใช้ (in-memory)</summary>
/// <param name="db">ฐานข้อมูลในหน่วยความจำ</param>
public sealed class InMemoryUserProfileADO(InMemoryDatabase db) : IUserProfileDAO
{
    /// <inheritdoc />
    public Task<UserProfileDTO?> GetAsync(string subject, CancellationToken ct)
    {
        lock (db.Sync)
        {
            return Task.FromResult(db.Profiles.TryGetValue(subject, out UserProfileDTO? p) ? InMemoryDatabase.Copy(p) : null);
        }
    }

    /// <inheritdoc />
    public Task UpsertAsync(UserProfileDTO profile, CancellationToken ct)
    {
        lock (db.Sync)
        {
            db.Profiles[profile.Subject] = InMemoryDatabase.Copy(profile);
        }

        return Task.CompletedTask;
    }
}
