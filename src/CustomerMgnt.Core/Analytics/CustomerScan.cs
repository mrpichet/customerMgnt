using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;

namespace CustomerMgnt.Core.Analytics;

/// <summary>ตัวช่วยอ่านลูกค้าทั้งหมดแบบ keyset paging (ไม่โหลดเกินเพดานเพื่อป้องกันหน่วยความจำล้น)</summary>
internal static class CustomerScan
{
    /// <summary>ขนาดหน้าที่อ่านต่อครั้ง</summary>
    private const int PageSize = 5_000;

    /// <summary>อ่านลูกค้าทั้งหมดเรียงตาม id จนถึงเพดาน</summary>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="max">จำนวนสูงสุด</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการลูกค้า</returns>
    public static async Task<IReadOnlyList<CustomerDTO>> LoadAllAsync(ICustomerDAO customers, int max, CancellationToken ct)
    {
        // Step 1: อ่านทีละหน้า โดยใช้ id สุดท้ายเป็นจุดเริ่มของหน้าถัดไป
        var all = new List<CustomerDTO>();
        long after = 0;
        while (all.Count < max)
        {
            IReadOnlyList<CustomerDTO> batch = await customers.ListBatchAsync(after, Math.Min(PageSize, max - all.Count), ct).ConfigureAwait(false);
            if (batch.Count == 0) break;
            all.AddRange(batch);
            after = batch[^1].Id;
        }

        return all;
    }
}
