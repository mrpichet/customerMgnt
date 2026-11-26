using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Common.Core.Domain;
using Common.Core.Exceptions;
using CustomerMgnt.Core.Membership.DataAccess;
using CustomerMgnt.Core.Membership.Domain;
using CustomerMgnt.Core.Membership.Engine;

namespace CustomerMgnt.Core.Membership.Services;

/// <summary>Dashboard, รายงาน (≤ 5,000 แถวบนจอ) และส่งออกไฟล์ (เบื้องหลัง, หมดอายุ 24 ชม., บันทึกผู้ส่งออกและตัวกรอง)</summary>
public sealed class ReportService
{
    /// <summary>จำนวนแถวสูงสุดที่แสดงบนจอ</summary>
    public const int ScreenLimit = 5000;
    /// <summary>จำนวนแถวสูงสุดของไฟล์ส่งออก</summary>
    public const int ExportLimit = 100_000;

    private readonly IMemberDAO _members;
    private readonly IPointDAO _points;
    private readonly ICampaignDAO _campaigns;
    private readonly ICheckoutDAO _checkout;
    private readonly IOutboxDAO _outbox;
    private readonly IMembershipJobDAO _jobs;
    private readonly IMembershipAuditDAO _audit;
    private readonly ApprovalService _approvals;
    private readonly TimeProvider _clock;

    /// <summary>สร้าง</summary>
    public ReportService(IMemberDAO members, IPointDAO points, ICampaignDAO campaigns, ICheckoutDAO checkout, IOutboxDAO outbox, IMembershipJobDAO jobs,
        IMembershipAuditDAO audit, ApprovalService approvals, TimeProvider clock)
    {
        _members = members;
        _points = points;
        _campaigns = campaigns;
        _checkout = checkout;
        _outbox = outbox;
        _jobs = jobs;
        _audit = audit;
        _approvals = approvals;
        _clock = clock;
    }

    /// <summary>ตัวเลขภาพรวม (แสดงเฉพาะส่วนที่ผู้เรียกมีสิทธิ์)</summary>
    public async Task<DashboardDTO> GetDashboardAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.MemberRead);
        DateTimeOffset since = _clock.GetUtcNow().AddDays(-30);
        MemberStatsDTO stats = await _members.GetStatsAsync(since, ct).ConfigureAwait(false);
        var d = new DashboardDTO { TotalMembers = stats.Total, ActiveMembers = stats.Active, NewMembers30d = stats.NewSince, MembersByTier = stats.ByTier };
        if (caller.Has(MembershipPermissions.PointsRead))
        {
            PointTotalsDTO p = await _points.GetTotalsAsync(since, ct).ConfigureAwait(false);
            (d.PointsOutstanding, d.PointsEarned30d, d.PointsRedeemed30d) = (p.Outstanding, p.EarnedSince, p.RedeemedSince);
        }

        if (caller.Has(MembershipPermissions.PromotionRead))
        {
            d.ActiveCampaigns = (await _campaigns.ListByStatusAsync([CampaignStatus.Active], ct).ConfigureAwait(false)).Count;
        }

        if (caller.Has(MembershipPermissions.TxnRead))
        {
            SalesTotalsDTO s = await _checkout.GetTotalsAsync(since, ct).ConfigureAwait(false);
            (d.Sales30d, d.Transactions30d) = (s.Sales, s.Transactions);
        }

        if (caller.Has(MembershipPermissions.SettingsManage))
        {
            (d.OutboxBacklog, d.DeadLetters) = await _outbox.GetStatsAsync(ct).ConfigureAwait(false);
        }

        d.PendingApprovals = await _approvals.CountPendingAsync(caller, ct).ConfigureAwait(false);
        return d;
    }

    /// <summary>รายงานบนจอ</summary>
    public async Task<ReportDTO> GetReportAsync(MembershipCaller caller, string report, ReportFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(filter);
        caller.Require(MembershipPermissions.ReportRead);
        return await BuildAsync(report, filter, ScreenLimit, ct).ConfigureAwait(false);
    }

    /// <summary>ขอส่งออกไฟล์ (สร้างโดยงานเบื้องหลัง)</summary>
    public async Task<ExportJobDTO> RequestExportAsync(MembershipCaller caller, ExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.Require(MembershipPermissions.ReportExport);
        if (request.Report is "members" or "transactions")
        {
            caller.Require(MembershipPermissions.MemberExport);
        }

        DateTimeOffset now = _clock.GetUtcNow();
        var job = new ExportJobDTO
        {
            Report = request.Report, Format = request.Format, FiltersJson = MembershipJson.Write(request.Filters), Status = JobStatus.Queued,
            RequestedBy = caller.Actor, RequestedAt = now,
        };
        job.Id = await _jobs.InsertExportAsync(job, ct).ConfigureAwait(false);
        await _audit.AppendAsync([MembershipEvents.Audit(caller, "report.export", $"export:{job.Id}", null, new { job.Report, job.Format, filters = request.Filters }, now)], ct).ConfigureAwait(false);
        return job;
    }

    /// <summary>งานส่งออกของฉัน</summary>
    public Task<IReadOnlyList<ExportJobDTO>> ListExportsAsync(MembershipCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.ReportExport);
        return _jobs.ListExportsAsync(caller.Actor, null, ct);
    }

    /// <summary>ดาวน์โหลดไฟล์ (เจ้าของงานเท่านั้น, ยังไม่หมดอายุ)</summary>
    /// <returns>(ชื่อไฟล์, content type, ข้อมูล)</returns>
    public async Task<(string FileName, string ContentType, byte[] Content)> DownloadAsync(MembershipCaller caller, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(MembershipPermissions.ReportExport);
        ExportJobDTO? job = await _jobs.GetExportAsync(id, withContent: true, ct).ConfigureAwait(false);
        if (job is null || !string.Equals(job.RequestedBy, caller.Actor, StringComparison.Ordinal))
        {
            throw new NotFoundException("Export not found.");
        }

        if (job.Status != JobStatus.Done || job.Content is null || job.ExpiresAt <= _clock.GetUtcNow())
        {
            throw MembershipException.Gone("export_expired", "The file is not available.");
        }

        await _audit.AppendAsync([MembershipEvents.Audit(caller, "report.download", $"export:{job.Id}", null, new { job.Report, job.RowCount }, _clock.GetUtcNow())], ct).ConfigureAwait(false);
        bool xlsx = job.Format == "Xlsx";
        return ($"{job.Report}-{job.RequestedAt:yyyyMMddHHmm}.{(xlsx ? "xlsx" : "csv")}",
            xlsx ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv; charset=utf-8", job.Content);
    }

    /// <summary>งานเบื้องหลัง: สร้างไฟล์ที่รอคิว</summary>
    public async Task<int> RunExportsAsync(CancellationToken ct)
    {
        int n = 0;
        foreach (ExportJobDTO job in await _jobs.ListExportsAsync(null, JobStatus.Queued, ct).ConfigureAwait(false))
        {
            job.Status = JobStatus.Running;
            await _jobs.UpdateExportAsync(job, ct).ConfigureAwait(false);
            try
            {
                ReportDTO r = await BuildAsync(job.Report, MembershipJson.Read<ReportFilter>(job.FiltersJson), ExportLimit, ct).ConfigureAwait(false);
                job.Content = job.Format == "Xlsx" ? Xlsx(r) : Csv(r);
                job.RowCount = r.Rows.Count;
                job.Status = JobStatus.Done;
                job.ExpiresAt = _clock.GetUtcNow().AddHours(24);
            }
            catch (Exception ex) when (ex is CommonException or InvalidOperationException or FormatException)
            {
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
            }

            await _jobs.UpdateExportAsync(job, ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    private async Task<ReportDTO> BuildAsync(string report, ReportFilter f, int limit, CancellationToken ct)
    {
        DateTimeOffset to = f.To ?? _clock.GetUtcNow();
        DateTimeOffset from = f.From ?? to.AddDays(-30);
        ValidationException.ThrowIf(from > to, "from", "from must be before to.");
        return report switch
        {
            "members" => await MembersAsync(f, from, to, limit, ct).ConfigureAwait(false),
            "points" => await PointsAsync(from, to, limit, ct).ConfigureAwait(false),
            "promotions" => await PromotionsAsync(f, from, to, limit, ct).ConfigureAwait(false),
            "transactions" => await TransactionsAsync(f, from, to, limit, ct).ConfigureAwait(false),
            _ => throw new NotFoundException("Report not found."),
        };
    }

    private async Task<ReportDTO> MembersAsync(ReportFilter f, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct)
    {
        var r = new ReportDTO { Title = "สมาชิก", Columns = ["เลขสมาชิก", "ชื่อ", "เบอร์ (mask)", "สถานะ", "Tier", "คะแนนคงเหลือ", "รหัสลูกค้า", "วันที่สมัคร"] };
        for (int page = 1; r.Rows.Count < limit; page++)
        {
            PagedResultDTO<MemberEntity> p = await _members.SearchAsync(new MemberSearchCriteria { Status = f.Status, TierCode = f.TierCode, Page = page, PageSize = 200 }, ct).ConfigureAwait(false);
            foreach (MemberEntity m in p.Items.Where(m => m.CreatedAt >= from && m.CreatedAt <= to))
            {
                r.Rows.Add([m.MemberNo, MemberRules.ShortName(m.FirstName, m.LastName), m.PhoneMasked ?? "", m.Status.ToString(), m.TierCode ?? "",
                    m.PointsAvailable.ToString(CultureInfo.InvariantCulture), m.CustomerCode ?? "", m.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)]);
            }

            if (p.Items.Count < 200 || page * 200 >= p.Total)
            {
                break;
            }
        }

        return Finish(r, limit, ("สมาชิก", r.Rows.Count));
    }

    private async Task<ReportDTO> PointsAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct)
    {
        // สรุปรายวันตามชนิดรายการ
        var sums = new SortedDictionary<(string Day, LedgerType Type), (long Points, int Count)>();
        for (int page = 1; ; page++)
        {
            PagedResultDTO<PointLedgerDTO> p = await _points.SearchLedgerAsync(new LedgerCriteria { From = from, To = to, Page = page, PageSize = 500 }, ct).ConfigureAwait(false);
            foreach (PointLedgerDTO l in p.Items)
            {
                var key = (l.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), l.Type);
                sums.TryGetValue(key, out (long Points, int Count) v);
                sums[key] = (v.Points + l.Points, v.Count + 1);
            }

            if (p.Items.Count < 500 || page * 500 >= p.Total || page >= 400)
            {
                break;
            }
        }

        var r = new ReportDTO { Title = "คะแนน", Columns = ["วันที่", "ชนิด", "จำนวนรายการ", "คะแนน"] };
        foreach (((string day, LedgerType type), (long points, int count)) in sums)
        {
            r.Rows.Add([day, type.ToString(), count.ToString(CultureInfo.InvariantCulture), points.ToString(CultureInfo.InvariantCulture)]);
        }

        return Finish(r, limit,
            ("ได้รับ", sums.Where(s => s.Key.Type == LedgerType.Earn).Sum(s => s.Value.Points)),
            ("ใช้", -sums.Where(s => s.Key.Type == LedgerType.Redeem).Sum(s => s.Value.Points)),
            ("หมดอายุ", -sums.Where(s => s.Key.Type == LedgerType.Expire).Sum(s => s.Value.Points)));
    }

    private async Task<ReportDTO> PromotionsAsync(ReportFilter f, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct)
    {
        PagedResultDTO<CampaignDTO> p = await _campaigns.SearchAsync(new CampaignSearchCriteria { Page = 1, PageSize = 500 }, ct).ConfigureAwait(false);
        List<CampaignDTO> list = [.. p.Items.Where(c => (f.CampaignId is null || c.Id == f.CampaignId) && c.StartAt <= to && c.EndAt >= from)];
        IReadOnlyDictionary<(long CampaignId, string Scope), CampaignUsageDTO> counters = list.Count == 0
            ? new Dictionary<(long, string), CampaignUsageDTO>()
            : await _campaigns.GetCountersAsync([.. list.Select(c => c.Id)], ["total"], ct).ConfigureAwait(false);
        var r = new ReportDTO { Title = "โปรโมชัน", Columns = ["รหัส", "ชื่อ", "ประเภท", "สถานะ", "เริ่ม", "สิ้นสุด", "ใช้สิทธิ์", "งบ", "ใช้งบ"] };
        foreach (CampaignDTO c in list.OrderBy(c => c.StartAt))
        {
            CampaignUsageDTO u = counters.TryGetValue((c.Id, "total"), out CampaignUsageDTO? x) ? x : new CampaignUsageDTO();
            r.Rows.Add([c.Code, c.Name, c.RewardType.ToString(), c.Status.ToString(), c.StartAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                c.EndAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), u.Uses.ToString(CultureInfo.InvariantCulture),
                c.Limits.Budget?.ToString("F2", CultureInfo.InvariantCulture) ?? "", u.BudgetUsed.ToString("F2", CultureInfo.InvariantCulture)]);
        }

        return Finish(r, limit, ("แคมเปญ", list.Count));
    }

    private async Task<ReportDTO> TransactionsAsync(ReportFilter f, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct)
    {
        var r = new ReportDTO { Title = "รายการขาย", Columns = ["วันที่", "ระบบต้นทาง", "เลขที่รายการ", "เลขสมาชิก", "สาขา", "ยอดชำระ", "ส่วนลด", "คะแนนได้", "คะแนนใช้", "สถานะ"] };
        decimal sales = 0;
        for (int page = 1; r.Rows.Count < limit; page++)
        {
            PagedResultDTO<SalesTransactionEntity> p = await _checkout.SearchTransactionsAsync(new TransactionSearchCriteria { From = from, To = to, BranchCode = f.BranchCode, Page = page, PageSize = 500 }, ct).ConfigureAwait(false);
            foreach (SalesTransactionEntity t in p.Items)
            {
                sales += t.NetPaid;
                r.Rows.Add([t.OccurredAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), t.SourceSystem, t.TransactionId, t.MemberNo, t.BranchCode ?? "",
                    t.NetPaid.ToString("F2", CultureInfo.InvariantCulture), t.DiscountTotal.ToString("F2", CultureInfo.InvariantCulture),
                    t.PointsEarned.ToString(CultureInfo.InvariantCulture), t.PointsRedeemed.ToString(CultureInfo.InvariantCulture), t.Status.ToString()]);
            }

            if (p.Items.Count < 500 || page * 500 >= p.Total)
            {
                break;
            }
        }

        return Finish(r, limit, ("ยอดขาย", sales), ("รายการ", r.Rows.Count));
    }

    private static ReportDTO Finish(ReportDTO r, int limit, params (string Key, decimal Value)[] totals)
    {
        if (r.Rows.Count > limit)
        {
            r.Rows.RemoveRange(limit, r.Rows.Count - limit);
            r.Truncated = true;
        }

        foreach ((string k, decimal v) in totals)
        {
            r.Totals[k] = v;
        }

        return r;
    }

    // ------------------------------------------------------------------ ไฟล์

    /// <summary>CSV UTF-8 BOM (กันสูตร Excel: ค่าขึ้นต้นด้วย = + - @ ใส่ ' นำหน้า)</summary>
    internal static byte[] Csv(ReportDTO r)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', r.Columns.Select(CsvCell)).Append("\r\n");
        foreach (List<string> row in r.Rows)
        {
            sb.AppendJoin(',', row.Select(CsvCell)).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string CsvCell(string v)
    {
        v = SafeText(v);
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    private static string SafeText(string v) => v.Length > 0 && v[0] is '=' or '+' or '-' or '@' && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? "'" + v : v;

    /// <summary>XLSX แบบเรียบง่าย (1 sheet, ข้อความ inline) โดยไม่ใช้ไลบรารีภายนอก</summary>
    internal static byte[] Xlsx(ReportDTO r)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }

            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Report\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            void Row(IEnumerable<string> cells)
            {
                sheet.Append("<row>");
                foreach (string c in cells)
                {
                    if (decimal.TryParse(c, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) && c.Length < 16 && !c.StartsWith('0'))
                    {
                        sheet.Append("<c><v>").Append(d.ToString(CultureInfo.InvariantCulture)).Append("</v></c>");
                    }
                    else
                    {
                        sheet.Append("<c t=\"inlineStr\"><is><t>").Append(SecurityElement.Escape(c)).Append("</t></is></c>");
                    }
                }

                sheet.Append("</row>");
            }

            Row(r.Columns);
            foreach (List<string> row in r.Rows)
            {
                Row(row);
            }

            sheet.Append("</sheetData></worksheet>");
            Add("xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return ms.ToArray();
    }
}
