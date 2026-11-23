using Common.Core.Domain;
using Common.Core.Exceptions;
using Common.Core.Security;
using CustomerMgnt.Core.DataAccess;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;

namespace CustomerMgnt.Core.Services;

/// <summary>
/// Lead &amp; Pipeline Management: ติดตามโอกาสการขายในแต่ละขั้น ตั้งแต่เริ่มทักจนปิดการขาย
/// ทุกการย้าย stage ถูกบันทึกประวัติ (ใช้คำนวณ velocity, win rate และเป็นข้อมูลฝึก lead scoring)
/// </summary>
public sealed class PipelineService
{
    private readonly IOpportunityDAO _opportunities;
    private readonly ICustomerDAO _customers;
    private readonly TimeProvider _clock;

    /// <summary>สร้างบริการ</summary>
    /// <param name="opportunities">DAO โอกาสการขาย</param>
    /// <param name="customers">DAO ลูกค้า</param>
    /// <param name="clock">นาฬิกา</param>
    public PipelineService(IOpportunityDAO opportunities, ICustomerDAO customers, TimeProvider clock)
    {
        _opportunities = opportunities;
        _customers = customers;
        _clock = clock;
    }

    /// <summary>ความน่าจะเป็นมาตรฐานของแต่ละ stage (%) เมื่อเซลส์ไม่ได้ประเมินเอง</summary>
    /// <param name="stage">stage</param>
    /// <returns>0..100</returns>
    public static int DefaultProbability(PipelineStage stage) => stage switch
    {
        PipelineStage.New => 10,
        PipelineStage.Contacted => 20,
        PipelineStage.Qualified => 40,
        PipelineStage.Proposal => 60,
        PipelineStage.Negotiation => 80,
        PipelineStage.Won => 100,
        _ => 0,
    };

    /// <summary>สร้างโอกาสการขาย</summary>
    /// <param name="user">ผู้เรียก (sales.write)</param>
    /// <param name="request">ข้อมูล</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>โอกาสที่บันทึก</returns>
    public async Task<OpportunityDTO> CreateAsync(UserContextDTO user, CreateOpportunityRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + ลูกค้า
        AccessGuard.Require(user, Permissions.SalesWrite);
        ArgumentNullException.ThrowIfNull(request);
        CustomerDTO customer = await _customers.GetAsync(request.CustomerId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Customer not found.");
        ValidationException.ThrowIf(customer.Status == CustomerStatus.Churned, "customerId", "Re-activate the customer before creating an opportunity.");
        DateTimeOffset now = _clock.GetUtcNow();

        // Step 2: บันทึกที่ stage New
        var o = new OpportunityDTO
        {
            CustomerId = customer.Id,
            Title = InputGuard.Required(request.Title, "title", 300),
            Stage = PipelineStage.New,
            Amount = request.Amount,
            Probability = request.Probability,
            ExpectedCloseDate = request.ExpectedCloseDate == default ? DateOnly.FromDateTime(now.UtcDateTime).AddDays(90) : request.ExpectedCloseDate,
            Source = InputGuard.StripControlCharacters(request.Source),
            OwnerSubject = string.IsNullOrWhiteSpace(request.OwnerSubject) ? user.Subject : request.OwnerSubject,
            StageChangedAt = now,
            CreatedAt = now,
            Version = 1,
            CustomerName = customer.DisplayName,
        };
        o.Id = await _opportunities.InsertAsync(o, ct).ConfigureAwait(false);
        return o;
    }

    /// <summary>ย้าย stage (Won → ลูกค้ากลายเป็น Active อัตโนมัติ)</summary>
    /// <param name="user">ผู้เรียก (sales.write)</param>
    /// <param name="opportunityId">โอกาส</param>
    /// <param name="request">stage ใหม่ + version</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>โอกาสหลังย้าย</returns>
    /// <exception cref="ValidationException">ย้ายจาก stage ที่ปิดแล้ว หรือ Lost ไม่ระบุเหตุผล</exception>
    public async Task<OpportunityDTO> MoveStageAsync(UserContextDTO user, long opportunityId, MoveStageRequest request, CancellationToken ct = default)
    {
        // Step 1: สิทธิ์ + version
        AccessGuard.Require(user, Permissions.SalesWrite);
        ArgumentNullException.ThrowIfNull(request);
        OpportunityDTO o = await _opportunities.GetAsync(opportunityId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Opportunity not found.");
        if (o.Version != request.Version)
        {
            throw new ConflictException("The opportunity was changed by someone else. Reload and try again.");
        }

        // Step 2: กฎ — ปิดแล้วเปิดใหม่ได้เฉพาะ Lost → Qualified (ลูกค้ากลับมาสนใจ), Lost ต้องมีเหตุผล
        ValidationException.ThrowIf(!Enum.IsDefined(request.Stage), "stage", "Unknown stage.");
        ValidationException.ThrowIf(o.Stage == PipelineStage.Won, "stage", "A won opportunity cannot be moved. Create a new opportunity for expansion.");
        ValidationException.ThrowIf(o.Stage == PipelineStage.Lost && request.Stage != PipelineStage.Qualified, "stage", "A lost opportunity can only be re-opened to Qualified.");
        ValidationException.ThrowIf(request.Stage == PipelineStage.Lost && string.IsNullOrWhiteSpace(request.LostReason), "lostReason", "LostReason is required.");

        // Step 3: ประวัติ + ค่าใหม่
        DateTimeOffset now = _clock.GetUtcNow();
        StageHistoryDTO? history = o.Stage == request.Stage ? null : new StageHistoryDTO { OpportunityId = o.Id, FromStage = o.Stage, ToStage = request.Stage, ChangedBy = user.Subject, ChangedAt = now };
        if (history is not null)
        {
            o.Stage = request.Stage;
            o.StageChangedAt = now;
            o.ClosedAt = o.IsClosed ? now : null;
        }

        if (request.Amount is not null) o.Amount = request.Amount.Value;
        o.LostReason = request.Stage == PipelineStage.Lost ? InputGuard.StripControlCharacters(request.LostReason) : null;

        if (!await _opportunities.UpdateAsync(o, history, ct).ConfigureAwait(false))
        {
            throw new ConflictException("The opportunity was changed by someone else. Reload and try again.");
        }

        o.Version++;

        // Step 4: ชนะ → ลูกค้าเป็น Active
        if (o.Stage == PipelineStage.Won && await _customers.GetAsync(o.CustomerId, ct).ConfigureAwait(false) is { Status: CustomerStatus.Prospect or CustomerStatus.Inactive } c)
        {
            c.Status = CustomerStatus.Active;
            c.UpdatedAt = now;
            await _customers.UpdateAsync(c, ct).ConfigureAwait(false);
        }

        return o;
    }

    /// <summary>รายการโอกาสการขาย</summary>
    /// <param name="user">ผู้เรียก (sales.read)</param>
    /// <param name="customerId">ลูกค้า</param>
    /// <param name="openOnly">เฉพาะที่ยังไม่ปิด</param>
    /// <param name="mine">เฉพาะของฉัน</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>รายการ</returns>
    public Task<IReadOnlyList<OpportunityDTO>> ListAsync(UserContextDTO user, long? customerId, bool openOnly, bool mine, CancellationToken ct = default)
    {
        AccessGuard.Require(user, Permissions.SalesRead);
        return _opportunities.ListAsync(customerId, openOnly, mine ? user.Subject : null, ct);
    }

    /// <summary>ภาพรวม Pipeline: จำนวน มูลค่า มูลค่าถ่วงน้ำหนัก เวลาเฉลี่ยในแต่ละ stage และ win rate ย้อนหลัง 1 ปี</summary>
    /// <param name="user">ผู้เรียก (sales.read)</param>
    /// <param name="ct">ยกเลิก</param>
    /// <returns>สรุป</returns>
    public async Task<PipelineSummaryDTO> SummaryAsync(UserContextDTO user, CancellationToken ct = default)
    {
        // Step 1: ข้อมูล
        AccessGuard.Require(user, Permissions.SalesRead);
        DateTimeOffset now = _clock.GetUtcNow();
        Task<IReadOnlyList<OpportunityDTO>> openTask = _opportunities.ListAsync(null, true, null, ct);
        Task<IReadOnlyList<OpportunityDTO>> closedTask = _opportunities.ListClosedAsync(now.AddYears(-1), ct);
        await Task.WhenAll(openTask, closedTask).ConfigureAwait(false);

        // Step 2: ราย stage (ใช้คะแนน ML ถ้ามี ไม่เช่นนั้นความน่าจะเป็นของเซลส์/ค่ามาตรฐาน)
        var stages = Enum.GetValues<PipelineStage>().Where(s => s is not (PipelineStage.Won or PipelineStage.Lost)).Select(stage =>
        {
            var items = openTask.Result.Where(o => o.Stage == stage).ToList();
            decimal amount = items.Sum(o => o.Amount);
            decimal weighted = items.Sum(o => o.Amount * (decimal)EffectiveProbability(o) / 100m);
            double days = items.Count == 0 ? 0 : items.Average(o => (now - o.StageChangedAt).TotalDays);
            return new PipelineStageSummaryDTO(stage, items.Count, amount, Math.Round(weighted, 2), Math.Round(days, 1));
        }).ToList();

        // Step 3: win rate
        int won = closedTask.Result.Count(o => o.Stage == PipelineStage.Won);
        int lost = closedTask.Result.Count(o => o.Stage == PipelineStage.Lost);
        double winRate = won + lost == 0 ? 0 : Math.Round((double)won / (won + lost), 4);
        return new PipelineSummaryDTO(stages, stages.Sum(s => s.Amount), stages.Sum(s => s.WeightedAmount), winRate);
    }

    /// <summary>ความน่าจะเป็นที่ใช้จริง: คะแนน ML → ค่าที่เซลส์ระบุ → ค่ามาตรฐานของ stage</summary>
    /// <param name="o">โอกาส</param>
    /// <returns>0..100</returns>
    public static double EffectiveProbability(OpportunityDTO o) => o.Score ?? o.Probability ?? DefaultProbability(o.Stage);
}
