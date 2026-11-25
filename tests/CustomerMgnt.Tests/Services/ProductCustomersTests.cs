using Common.Core.Exceptions;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Core.Services;
using CustomerMgnt.Tests.Support;

namespace CustomerMgnt.Tests.Services;

/// <summary>GET /api/products/{code}/customers — ลูกค้าที่มีสัญญา Active ของผลิตภัณฑ์ (ProductMgnt ใช้ตรวจผลกระทบ)</summary>
[TestClass]
public sealed class ProductCustomersTests
{
    public TestContext TestContext { get; set; } = default!;

    [TestMethod]
    public async Task ActiveContracts_ByProduct_AreListed_CaseInsensitive()
    {
        // Step 1: ลูกค้าสองราย: A มีสัญญา PM01 ที่ยังมีผล + สัญญาที่หมดแล้ว, B มีสัญญาผลิตภัณฑ์อื่น
        await using var host = new TestHost();
        CancellationToken ct = TestContext.CancellationToken;
        var user = TestHost.AllPermissions();
        CustomerService customers = host.Get<CustomerService>();
        DataIngestionService ingestion = host.Get<DataIngestionService>();
        CustomerDTO a = await customers.RegisterAsync(user, new RegisterCustomerRequest { Kind = CustomerKind.Juristic, LegalName = "บริษัท เอ จำกัด", Country = "TH", TimeZoneId = "Asia/Bangkok", Status = CustomerStatus.Active }, ct);
        CustomerDTO b = await customers.RegisterAsync(user, new RegisterCustomerRequest { Kind = CustomerKind.Juristic, LegalName = "บริษัท บี จำกัด", Country = "TH", TimeZoneId = "Asia/Bangkok", Status = CustomerStatus.Active }, ct);
        ContractDTO c1 = await ingestion.CreateContractAsync(user, new ContractRequest { CustomerId = a.Id, ContractNo = "MA-001", Title = "MA", StartDate = new(2026, 1, 1), EndDate = new(2027, 12, 31), ProductCode = "pm01" }, ct);
        await ingestion.CreateContractAsync(user, new ContractRequest { CustomerId = a.Id, ContractNo = "MA-OLD", Title = "MA เก่า", StartDate = new(2024, 1, 1), EndDate = new(2025, 12, 31), ProductCode = "PM01" }, ct);
        await ingestion.CreateContractAsync(user, new ContractRequest { CustomerId = b.Id, ContractNo = "MA-002", Title = "MA", StartDate = new(2026, 1, 1), EndDate = new(2027, 12, 31), ProductCode = "PM02" }, ct);
        Assert.AreEqual("PM01", c1.ProductCode);

        // Step 2: เฉพาะสัญญาที่ยังไม่สิ้นสุดของ PM01 (รหัสตัวเล็ก/ใหญ่เหมือนกัน)
        IReadOnlyList<ProductCustomerDTO> rows = await ingestion.ListCustomersByProductAsync(TestHost.With(Permissions.CustomerRead), "Pm01", ct);
        Assert.HasCount(1, rows);
        Assert.AreEqual(a.Code, rows[0].CustomerCode);
        Assert.AreEqual("MA-001", rows[0].ContractNo);
        Assert.AreEqual(new DateOnly(2027, 12, 31), rows[0].ContractEnd);

        // Step 3: ไม่มีสิทธิ์ / รหัสไม่ถูกต้อง
        await Assert.ThrowsExactlyAsync<ForbiddenException>(() => ingestion.ListCustomersByProductAsync(TestHost.With(Permissions.SalesRead), "PM01", ct));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => ingestion.ListCustomersByProductAsync(user, "PM 01;", ct));
    }
}
