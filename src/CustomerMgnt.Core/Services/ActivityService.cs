using System.Globalization;
using Common.Core.Domain;
using Common.Core.Exceptions;
using Common.Core.Security;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Integration;
using CustomerMgnt.Core.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// การติดตามกิจกรรมและนัดหมาย: บันทึกการโทร ประชุม อีเมล แชต งานติดตาม พร้อมแจ้งเตือนไม่ให้พลาด follow-up
/// การประชุมสร้างใน MessageHub (คำเชิญ, RSVP, agenda, reminder) แล้วเก็บรหัสไว้ เพื่อดู Agenda และวาระค้างย้อนหลังได้
/// </summary>
public sealed class ActivityService
{
    private readonly IActivityDAO _activities;
    private readonly ICustomerDAO _customers;
    private readonly IUserProfileDAO _profiles;
    private readonly IMessageHubGateway _hub;
    private readonly NotificationQueue _notifications;
    private readonly TimeProvider _clock;
    private readonly ILogger<ActivityService> _logger;

    /// <summary>สร้างบริการ</summary>
    /// <param name="activities">DAO กิจกรรม</param>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="profiles">DAO โปรไฟล์ผู้ใช้</param>
    /// <param name="hub">MessageHub</param>
    /// <param name="notifications">คิวแจ้งเตือน</param>
    /// <param name="clock">นาฬิกา</param>
    /// <param name="logger">logger</param>
    public ActivityService(IActivityDAO activities, ICustomerDAO customers, IUserProfileDAO profiles, IMessageHubGateway hub, NotificationQueue notifications, TimeProvider clock, ILogger<ActivityService> logger)
    {
        _activities = activities;
        _customers = customers;
        _profiles = profiles;
        _hub = hub;
        _notifications = notifications;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>สร้างกิจกรรม (ถ้าเป็น Meeting และมีผู้เข้าร่วม → สร้างประชุมใน MessageHub พร้อมคำเชิญและ reminder)</summary>
    /// <param name="user">ผู้เรียก (activity.write)</param>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>กิจกรรมที่บันทึก</returns>
    public async Task<ActivityDTO> CreateAsync(UserContextDTO user, CreateActivityRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้า
        AccessGuard.Require(user, Permissions.ActivityWrite);
        ArgumentNullException.ThrowIfNull(request);
        CustomerDTO customer = await _customers.GetAsync(request.CustomerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
        ValidationException.ThrowIf(!Enum.IsDefined(request.Kind), "kind", "Unknown activity kind.");
        DateTimeOffset now = _clock.GetUtcNow();

        var activity = new ActivityDTO
        {
            CustomerId = customer.Id,
            OpportunityId = request.OpportunityId,
            Kind = request.Kind,
            Subject = InputGuard.Required(request.Subject, "subject", 300),
            Notes = InputGuard.StripControlCharacters(request.Notes),
            Status = request.Status,
            OwnerSubject = string.IsNullOrWhiteSpace(request.OwnerSubject) ? user.Subject : request.OwnerSubject,
            DueAt = request.DueAt == default ? now : request.DueAt.ToUniversalTime(),
            DurationMinutes = request.DurationMinutes,
            RemindBeforeMinutes = request.Status == ActivityStatus.Planned ? request.RemindBeforeMinutes : null,
            CompletedAt = request.Status == ActivityStatus.Done ? now : null,
            CreatedAt = now,
        };

        // Step 2: ประชุม → สร้างใน MessageHub (ผู้จัด = ผู้ใช้ที่ map ไว้)
        if (request.Kind == ActivityKind.Meeting && request.MeetingAttendees.Count > 0 && request.Status == ActivityStatus.Planned)
        {
            UserProfileDTO? organizer = await _profiles.GetAsync(activity.OwnerSubject, ct).ConfigureAwait(false);
            ValidationException.ThrowIf(organizer?.MessageHubUserId is null, "ownerSubject", "The meeting owner has no MessageHub user mapping (PUT /api/users/me/profile).");
            int minutes = Math.Clamp(request.DurationMinutes ?? 60, 15, 600);
            activity.MessageHubMeetingId = await _hub.CreateMeetingAsync(
                $"{activity.Subject} — {customer.DisplayName}", activity.Notes, organizer!.MessageHubUserId!.Value,
                activity.DueAt, activity.DueAt.AddMinutes(minutes), request.MeetingAttendees.Distinct().Take(200).ToList(),
                request.MeetingAgenda.Select(a => InputGuard.Required(a, "meetingAgenda", 300)).Take(50).ToList(),
                request.RemindBeforeMinutes, ct).ConfigureAwait(false);
            activity.DurationMinutes = minutes;
        }

        // Step 3: บันทึก
        activity.Id = await _activities.InsertAsync(activity, ct).ConfigureAwait(false);
        return activity;
    }

    /// <summary>ปิดกิจกรรม พร้อมสร้างงานติดตามต่อ (ถ้าระบุ)</summary>
    /// <param name="user">ผู้เรียก (activity.write)</param>
    /// <param name="activityId">กิจกรรม</param>
    /// <param name="request">ผลลัพธ์</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>(กิจกรรมที่ปิด, งานติดตามใหม่หรือ null)</returns>
    public async Task<(ActivityDTO Completed, ActivityDTO? FollowUp)> CompleteAsync(UserContextDTO user, long activityId, CompleteActivityRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + กิจกรรมยังไม่ปิด
        AccessGuard.Require(user, Permissions.ActivityWrite);
        ArgumentNullException.ThrowIfNull(request);
        ActivityDTO a = await _activities.GetAsync(activityId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Activity not found.");
        ValidationException.ThrowIf(a.Status != ActivityStatus.Planned, "status", "Only planned activities can be completed.");

        // Step 2: ปิด
        DateTimeOffset now = _clock.GetUtcNow();
        a.Status = ActivityStatus.Done;
        a.CompletedAt = now;
        a.Outcome = InputGuard.StripControlCharacters(request.Outcome);
        if (request.Notes is not null)
        {
            a.Notes = string.IsNullOrEmpty(a.Notes) ? InputGuard.StripControlCharacters(request.Notes) : a.Notes + "\n" + InputGuard.StripControlCharacters(request.Notes);
        }

        await _activities.UpdateAsync(a, ct).ConfigureAwait(false);

        // Step 3: งานติดตามต่อ (เวลา 10:00 ของวันที่กำหนด)
        ActivityDTO? followUp = null;
        if (request.FollowUpInDays is { } days)
        {
            followUp = await CreateAsync(user, new CreateActivityRequest
            {
                CustomerId = a.CustomerId,
                OpportunityId = a.OpportunityId,
                Kind = ActivityKind.Task,
                Subject = $"ติดตามผล: {a.Subject}",
                OwnerSubject = a.OwnerSubject,
                DueAt = new DateTimeOffset(now.UtcDateTime.Date.AddDays(days).AddHours(3), TimeSpan.Zero), // 10:00 เวลาไทย
                RemindBeforeMinutes = 60,
            }, ct).ConfigureAwait(false);
        }

        return (a, followUp);
    }

    /// <summary>กิจกรรมของลูกค้า</summary>
    /// <param name="user">ผู้เรียก (customer.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="openOnly">เฉพาะที่ยังไม่ทำ</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    public Task<IReadOnlyList<ActivityDTO>> ListByCustomerAsync(UserContextDTO user, long customerId, bool openOnly, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.CustomerRead);
        return _activities.ListByCustomerAsync(customerId, openOnly, 500, ct);
    }

    /// <summary>งานของฉัน (ยังไม่ทำ กำหนดภายใน N วัน รวมที่เลยกำหนด)</summary>
    /// <param name="user">ผู้เรียก (customer.read)</param>
    /// <param name="days">ภายในกี่วัน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการเรียงตามกำหนด</returns>
    public Task<IReadOnlyList<ActivityDTO>> MyOpenActivitiesAsync(UserContextDTO user, int days = 7, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.CustomerRead);
        return _activities.ListOpenByOwnerAsync(user.Subject, _clock.GetUtcNow().AddDays(Math.Clamp(days, 0, 365)), ct);
    }

    /// <summary>
    /// Agenda และงานค้างของลูกค้า: ประชุมที่จะถึงและวาระที่ยังไม่ปิดจาก MessageHub + กิจกรรมที่ยังไม่ทำใน CustomerMgnt
    /// </summary>
    /// <param name="user">ผู้เรียก (customer.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>Agenda</returns>
    public async Task<CustomerAgendaDTO> GetCustomerAgendaAsync(UserContextDTO user, long customerId, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้า + กิจกรรมทั้งหมด (ล่าสุด 500)
        AccessGuard.Require(user, Permissions.CustomerRead);
        CustomerDTO customer = await _customers.GetAsync(customerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
        IReadOnlyList<ActivityDTO> all = await _activities.ListByCustomerAsync(customerId, false, 500, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();

        // Step 2: ดึงการประชุมจาก MessageHub แบบขนาน (ประชุมใน 90 วันที่ผ่านมาและอนาคต, สูงสุด 30 รายการ)
        Guid[] meetingIds = all.Where(a => a.MessageHubMeetingId is not null && a.DueAt >= now.AddDays(-90))
            .OrderByDescending(a => a.DueAt).Select(a => a.MessageHubMeetingId!.Value).Distinct().Take(30).ToArray();
        ExternalMeetingDTO?[] meetings = await Task.WhenAll(meetingIds.Select(id => SafeGetMeetingAsync(id, ct))).ConfigureAwait(false);

        // Step 3: ประกอบผล
        var valid = meetings.Where(m => m is not null).Select(m => m!).ToList();
        var upcoming = valid.Where(m => m.EndUtc >= now && m.Status != "Cancelled").OrderBy(m => m.StartUtc).ToList();
        var pendingAgenda = valid.Where(m => m.EndUtc < now).SelectMany(m => m.Agenda).Where(i => i.IsPending).ToList();
        var open = all.Where(a => a.Status == ActivityStatus.Planned).OrderBy(a => a.DueAt).ToList();
        return new CustomerAgendaDTO(customer.Code, upcoming, pendingAgenda, open, open.Count(a => a.IsOverdue(now)));
    }

    /// <summary>
    /// ส่งแจ้งเตือนกิจกรรมที่ถึงเวลา (เรียกโดย <see cref="ActivityReminderWorker"/>)
    /// ใช้ TryMarkReminderSent เป็น lock ระดับแถว → รันหลาย instance พร้อมกันได้โดยไม่ส่งซ้ำ
    /// </summary>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>จำนวนแจ้งเตือนที่ส่ง</returns>
    public async Task<int> ProcessDueRemindersAsync(CancellationToken ct = default)
    {
        // Step 1: กิจกรรมที่ถึงเวลาแจ้ง
        DateTimeOffset now = _clock.GetUtcNow();
        IReadOnlyList<ActivityDTO> due = await _activities.ListDueRemindersAsync(now, 500, ct).ConfigureAwait(false);
        int sent = 0;
        foreach (ActivityDTO a in due)
        {
            // Step 2: จองสิทธิ์ส่ง (instance อื่นจองไปแล้ว = ข้าม)
            if (!await _activities.TryMarkReminderSentAsync(a.Id, now, ct).ConfigureAwait(false))
            {
                continue;
            }

            // Step 3: เข้าคิวแจ้งเตือน (ประชุมใน MessageHub มี reminder ของตัวเองแล้ว)
            if (a.MessageHubMeetingId is null)
            {
                string when = a.DueAt.ToOffset(TimeSpan.FromHours(7)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
                _notifications.Enqueue(new StaffNotification(a.OwnerSubject, $"แจ้งเตือน: {a.Kind} — {a.Subject}", $"กำหนด {when} (เวลาไทย)\nลูกค้า #{a.CustomerId}\n{a.Notes}"));
                sent++;
            }
        }

        return sent;
    }

    /// <summary>อ่านการประชุมแบบไม่ล้มทั้งงานเมื่อ MessageHub มีปัญหา</summary>
    /// <param name="id">รหัสการประชุม</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>การประชุม หรือ null</returns>
    private async Task<ExternalMeetingDTO?> SafeGetMeetingAsync(Guid id, CancellationToken ct)
    {
        try
        {
            return await _hub.GetMeetingAsync(id, ct).ConfigureAwait(false);
        }
        catch (DependencyUnavailableException ex)
        {
            _logger.LogWarning(ex, "MessageHub meeting {MeetingId} unavailable", id);
            return null;
        }
    }
}

/// <summary>งานเบื้องหลังตรวจ reminder ของกิจกรรมเป็นระยะ</summary>
public sealed class ActivityReminderWorker : BackgroundService
{
    private readonly ActivityService _activities;
    private readonly CustomerMgntOptions _options;
    private readonly ILogger<ActivityReminderWorker> _logger;

    /// <summary>สร้างงาน</summary>
    /// <param name="activities">บริการกิจกรรม</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="logger">logger</param>
    public ActivityReminderWorker(ActivityService activities, IOptions<CustomerMgntOptions> options, ILogger<ActivityReminderWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _activities = activities;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Step 1: ตั้งรอบตรวจ (อย่างน้อย 10 วินาที)
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _options.ReminderScanSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // Step 2: ส่งแจ้งเตือนที่ถึงเวลา
                int n = await _activities.ProcessDueRemindersAsync(stoppingToken).ConfigureAwait(false);
                if (n > 0)
                {
                    _logger.LogInformation("Queued {Count} activity reminders", n);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Step 3: DB ล่มชั่วคราวไม่ทำให้ worker ตาย
                _logger.LogError(ex, "Activity reminder scan failed");
            }
        }
    }
}
