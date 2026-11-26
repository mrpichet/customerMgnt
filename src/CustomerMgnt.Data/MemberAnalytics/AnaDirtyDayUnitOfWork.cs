using CustomerMgnt.Core.MemberAnalytics.DataAccess;
using CustomerMgnt.Core.MemberAnalytics.Engine;
using CustomerMgnt.Core.Membership.DataAccess;
using Microsoft.Extensions.Logging;

namespace CustomerMgnt.Data.MemberAnalytics;

/// <summary>
/// ครอบ unit of work ของระบบสมาชิก: หลังบันทึกสำเร็จ ทำเครื่องหมายวันของรายการขายที่เพิ่ม/แก้ (เช่น คืนสินค้า ยกเลิกบิลย้อนหลัง)
/// ให้ read model ยอดขายคำนวณใหม่ — ถ้าทำเครื่องหมายไม่ได้จะ log ไว้และไม่ทำให้การบันทึกล้ม (รอบปกติ/คำสั่งคำนวณใหม่จะตามแก้)
/// </summary>
public sealed partial class AnaDirtyDayUnitOfWork : IMembershipUnitOfWork
{
    private readonly IMembershipUnitOfWork _inner;
    private readonly IAnaReadModelDAO _model;
    private readonly ILogger<AnaDirtyDayUnitOfWork> _log;

    /// <summary>สร้าง</summary>
    public AnaDirtyDayUnitOfWork(IMembershipUnitOfWork inner, IAnaReadModelDAO model, ILogger<AnaDirtyDayUnitOfWork> log)
    {
        _inner = inner;
        _model = model;
        _log = log;
    }

    /// <inheritdoc />
    public async Task CommitAsync(MembershipChangeSet changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        await _inner.CommitAsync(changes, ct).ConfigureAwait(false);
        var days = new HashSet<DateOnly>();
        if (changes.NewTransaction is { } n)
        {
            days.Add(AnaTime.Day(n.OccurredAt));
        }

        if (changes.TransactionUpdate is { } u)
        {
            days.Add(AnaTime.Day(u.OccurredAt));
        }

        if (days.Count == 0)
        {
            return;
        }

        try
        {
            await _model.MarkDirtyAsync(AnaTables.Sales, days, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMarkFailed(_log, ex);
        }
    }

    [LoggerMessage(EventId = 4340, Level = LogLevel.Warning, Message = "Could not mark analytics days dirty after commit")]
    private static partial void LogMarkFailed(ILogger logger, Exception ex);
}
