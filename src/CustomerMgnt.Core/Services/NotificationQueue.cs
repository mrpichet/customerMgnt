using System.Threading.Channels;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Integration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CustomerMgnt.Core.Services;

/// <summary>แจ้งเตือนพนักงาน 1 รายการ</summary>
/// <param name="RecipientSubject">ผู้รับ (AuthServer subject, null = ส่งให้ผู้รับเริ่มต้น)</param>
/// <param name="Subject">หัวข้อ</param>
/// <param name="Body">ข้อความ</param>
public sealed record StaffNotification(string? RecipientSubject, string Subject, string Body);

/// <summary>
/// คิวแจ้งเตือนแบบ bounded ในหน่วยความจำ (System.Threading.Channels) — request ไม่ต้องรอ MessageHub ทำให้ throughput สูง
/// เมื่อคิวเต็มจะทิ้งรายการเก่าสุด (แจ้งเตือนไม่ใช่ข้อมูลหลัก ข้อมูลจริงอยู่ในฐานข้อมูลแล้ว)
/// </summary>
public sealed class NotificationQueue
{
    private readonly Channel<StaffNotification> _channel = Channel.CreateBounded<StaffNotification>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <summary>ใส่รายการเข้าคิว (ไม่ block)</summary>
    /// <param name="notification">รายการ</param>
    /// <returns>true เมื่อเข้าคิวได้</returns>
    public bool Enqueue(StaffNotification notification) => _channel.Writer.TryWrite(notification);

    /// <summary>ตัวอ่านคิว (ใช้โดย <see cref="NotificationDispatcher"/>)</summary>
    internal ChannelReader<StaffNotification> Reader => _channel.Reader;
}

/// <summary>งานเบื้องหลังที่ส่งแจ้งเตือนจากคิวไปยัง MessageHub (ล้มเหลว = log แล้วทำรายการถัดไป ไม่ให้ทั้งคิวหยุด)</summary>
public sealed class NotificationDispatcher : BackgroundService
{
    private readonly NotificationQueue _queue;
    private readonly IUserProfileDAO _profiles;
    private readonly IMessageHubGateway _hub;
    private readonly CustomerMgntOptions _options;
    private readonly ILogger<NotificationDispatcher> _logger;

    /// <summary>สร้างงาน</summary>
    /// <param name="queue">คิว</param>
    /// <param name="profiles">DAO โปรไฟล์ผู้ใช้</param>
    /// <param name="hub">MessageHub</param>
    /// <param name="options">ค่าตั้ง</param>
    /// <param name="logger">logger</param>
    public NotificationDispatcher(NotificationQueue queue, IUserProfileDAO profiles, IMessageHubGateway hub, IOptions<CustomerMgntOptions> options, ILogger<NotificationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _queue = queue;
        _profiles = profiles;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Step 1: อ่านคิวไปเรื่อยๆ จนกว่าแอปจะหยุด
        await foreach (StaffNotification n in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // Step 2: หาผู้ใช้ MessageHub ของผู้รับ (ไม่มี → ผู้รับเริ่มต้น)
                Guid? target = _options.DefaultAlertUserId;
                if (n.RecipientSubject is { } subject && await _profiles.GetAsync(subject, stoppingToken).ConfigureAwait(false) is { MessageHubUserId: { } id })
                {
                    target = id;
                }

                if (target is null)
                {
                    _logger.LogWarning("Notification dropped: no MessageHub user for {Recipient}", n.RecipientSubject ?? "(default)");
                    continue;
                }

                // Step 3: ส่ง
                await _hub.NotifyUserAsync(target.Value, n.Subject, n.Body, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Step 4: ไม่ให้ข้อผิดพลาดรายการเดียวหยุดงานทั้งหมด
                _logger.LogError(ex, "Failed to send staff notification '{Subject}'", n.Subject);
            }
        }
    }
}
