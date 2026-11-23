using Common.Core.Domain;
using CustomerMgnt.Core;
using CustomerMgnt.Core.Domain;
using CustomerMgnt.Core.Integration;
using CustomerMgnt.Core.Security;
using CustomerMgnt.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CustomerMgnt.Tests.Support;

/// <summary>DI ของ business layer จริง + ข้อมูลในหน่วยความจำ + ระบบภายนอกปลอม + นาฬิกาควบคุมได้</summary>
internal sealed class TestHost : IAsyncDisposable
{
    public TestHost(DateTimeOffset? now = null)
    {
        Clock = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero));
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:InMemory"] = "true",
            ["Pqc:AllowEphemeralKeys"] = "true",
            ["Privacy:AllowEphemeralKey"] = "true",
        }).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddCustomerMgntCore(config, enableBackgroundWorkers: false);
        services.AddCustomerMgntData(config);
        services.Replace(ServiceDescriptor.Singleton<IProjectMgntGateway>(Projects));
        services.Replace(ServiceDescriptor.Singleton<IRequirementMgntGateway>(Requirements));
        services.Replace(ServiceDescriptor.Singleton<IIssueTrackingGateway>(Issues));
        services.Replace(ServiceDescriptor.Singleton<IMessageHubGateway>(Hub));
        Services = services.BuildServiceProvider();
    }

    public FakeTimeProvider Clock { get; }
    public ServiceProvider Services { get; }
    public FakeProjectMgnt Projects { get; } = new();
    public FakeRequirementMgnt Requirements { get; } = new();
    public FakeIssueTracking Issues { get; } = new();
    public FakeMessageHub Hub { get; } = new();

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>ผู้ใช้ที่มีสิทธิ์ทุกอย่าง (ไม่ใช่ admin)</summary>
    public static UserContextDTO AllPermissions(string subject = "u1") => new(subject, "Tester", [], Permissions.All.ToArray(), "127.0.0.1");

    /// <summary>ผู้ใช้ที่มีเฉพาะสิทธิ์ที่ระบุ</summary>
    public static UserContextDTO With(params string[] permissions) => new("u2", "Limited", [], permissions, "127.0.0.1");

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

internal sealed class FakeProjectMgnt : IProjectMgntGateway
{
    public List<string> Created { get; } = [];
    public List<ExternalProjectDTO> Projects { get; } = [];

    public Task<IReadOnlyList<ExternalProjectDTO>> GetProjectsAsync(IReadOnlyCollection<long> projectIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExternalProjectDTO>>(Projects.Where(p => projectIds.Contains(p.Id)).ToList());

    public Task<long> CreateProjectAsync(string code, string name, string? description, string methodology, long projectManagerId, DateOnly startDate, decimal budget, CancellationToken ct)
    {
        Created.Add(code);
        return Task.FromResult(1000L + Created.Count);
    }
}

internal sealed class FakeRequirementMgnt : IRequirementMgntGateway
{
    public int Projects { get; private set; }
    public List<(string Title, string Priority)> Requirements { get; } = [];
    public bool Fail { get; set; }

    public Task<long> CreateProjectAsync(string key, string name, string? description, string standard, DateOnly startDate, DateOnly targetEndDate, decimal budget, CancellationToken ct)
    {
        if (Fail) throw new Common.Core.Exceptions.DependencyUnavailableException("RequirementMgnt down");
        Projects++;
        return Task.FromResult(500L);
    }

    public Task<long?> GetFirstModuleIdAsync(long projectId, CancellationToken ct) => Task.FromResult<long?>(7);

    public Task<(long Id, string Key)> CreateRequirementAsync(long moduleId, string title, string content, string priority, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        Requirements.Add((title, priority));
        return Task.FromResult((10L + Requirements.Count, $"SR-{Requirements.Count}"));
    }
}

internal sealed class FakeIssueTracking : IIssueTrackingGateway
{
    public List<ExternalIssueDTO> Issues { get; } = [];
    public List<(string CustomerCode, string? Severity, string Priority)> Created { get; } = [];

    public Task<IReadOnlyList<ExternalIssueDTO>> SearchIssuesAsync(string? customerCode, IReadOnlyCollection<long> projectIds, bool openOnly, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExternalIssueDTO>>(Issues.Where(i => (customerCode is null || i.CustomerCode == customerCode) && (!openOnly || !i.IsClosed)).ToList());

    public Task<long> CreateIssueAsync(long projectId, int issueTypeId, string subject, string? description, string priority, string customerCode, string? severityCode, CancellationToken ct)
    {
        Created.Add((customerCode, severityCode, priority));
        return Task.FromResult(9000L + Created.Count);
    }
}

internal sealed class FakeMessageHub : IMessageHubGateway
{
    public List<(ChannelKind Channel, string Recipient, string Body)> Sent { get; } = [];
    public List<string> Notifications { get; } = [];

    public Task<Guid> CreateMeetingAsync(string title, string? description, Guid organizerId, DateTimeOffset startUtc, DateTimeOffset endUtc, IReadOnlyList<Guid> attendees, IReadOnlyList<string> agenda, int? reminderMinutes, CancellationToken ct) =>
        Task.FromResult(Guid.NewGuid());

    public Task<ExternalMeetingDTO?> GetMeetingAsync(Guid meetingId, CancellationToken ct) => Task.FromResult<ExternalMeetingDTO?>(null);

    public Task<bool> SendToCustomerAsync(ChannelKind channel, string recipient, string? subject, string body, CancellationToken ct)
    {
        Sent.Add((channel, recipient, body));
        return Task.FromResult(true);
    }

    public Task NotifyUserAsync(Guid userId, string subject, string body, CancellationToken ct)
    {
        Notifications.Add(subject);
        return Task.CompletedTask;
    }
}
