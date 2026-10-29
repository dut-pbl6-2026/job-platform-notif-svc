using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Notif.Core.Entities;
using Notif.Core.Interfaces;
using Notif.Core.Templates;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Workers;
using SharedKernel.Events;
using SharedKernel.Kafka;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// feat(admin) Slice 5: job.updated events now carry Pending/Active/Rejected statuses.
/// The consumer emails the recruiter on approval/rejection only, and stays idempotent
/// on redelivery via the notification log.
/// </summary>
public class JobEventsConsumerTests
{
    [Fact]
    public async Task JobUpdated_Active_SendsApprovedEmailAndLogs()
    {
        var jobId = Guid.NewGuid();
        var (consumer, sender, provider) = Build();

        var outcome = await consumer.HandleAsync(Envelope(jobId, "Active"));

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(1, sender.Calls);
        Assert.Contains("approved", sender.LastSubject);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var log = await db.NotificationLogs.SingleAsync();
        Assert.Equal("job.updated", log.EventType);
        Assert.Equal("active", log.StatusSnapshot);
        Assert.Equal(EmailTemplates.JobApproved, log.TemplateName);
        Assert.Equal("sent", log.Status);
    }

    [Fact]
    public async Task JobUpdated_Active_Replayed_IsIdempotent()
    {
        var (consumer, sender, provider) = Build();
        var json = Envelope(Guid.NewGuid(), "Active");

        var first = await consumer.HandleAsync(json);
        var second = await consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Handled, first);
        Assert.Equal(MessageOutcome.Handled, second);
        Assert.Equal(1, sender.Calls);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        Assert.Equal(1, await db.NotificationLogs.CountAsync());
    }

    [Fact]
    public async Task JobUpdated_Rejected_SendsRejectedEmailAndLogs()
    {
        var (consumer, sender, provider) = Build();

        var outcome = await consumer.HandleAsync(Envelope(Guid.NewGuid(), "Rejected"));

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(1, sender.Calls);
        Assert.Contains("not approved", sender.LastSubject);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var log = await db.NotificationLogs.SingleAsync();
        Assert.Equal("rejected", log.StatusSnapshot);
        Assert.Equal(EmailTemplates.JobRejected, log.TemplateName);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Closed")]
    [InlineData("Deleted")]
    public async Task JobUpdated_NonApprovalStatus_SendsNothing(string status)
    {
        var (consumer, sender, provider) = Build();

        var outcome = await consumer.HandleAsync(Envelope(Guid.NewGuid(), status));

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(0, sender.Calls);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        Assert.Equal(0, await db.NotificationLogs.CountAsync());
    }

    [Fact]
    public async Task JobUpdated_EmptyJobId_IsSkipped()
    {
        var (consumer, sender, _) = Build();

        var outcome = await consumer.HandleAsync(Envelope(Guid.Empty, "Active"));

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task JobCreated_IsSkipped()
    {
        var (consumer, sender, _) = Build();
        var payload = Payload(Guid.NewGuid(), "Active");
        var json = JsonSerializer.Serialize(
            EventEnvelope<JobCreatedEvent>.Create(
                JobEventTypes.Created,
                new JobCreatedEvent(
                    payload.JobId, payload.Title, payload.Description, payload.CompanyId,
                    payload.CompanyName, payload.Location, payload.SalaryMin, payload.SalaryMax,
                    payload.Currency, payload.CategoryId, payload.CategoryName,
                    payload.EmploymentType, payload.ExperienceLevel, payload.RecruiterId,
                    payload.Requirements, payload.Benefits, payload.Status, payload.OccurredAt)),
            KafkaJson.Options);

        var outcome = await consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task JobUpdated_SendFailure_IsRetriedOnRedelivery()
    {
        var (consumer, sender, provider) = Build();
        sender.FailFirst = 1;
        var json = Envelope(Guid.NewGuid(), "Active");

        var first = await consumer.HandleAsync(json);
        var second = await consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Retry, first);
        Assert.Equal(MessageOutcome.Handled, second);
        Assert.Equal(2, sender.Calls);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        Assert.Equal("sent", (await db.NotificationLogs.SingleAsync()).Status);
    }

    private static string Envelope(Guid jobId, string status) =>
        JsonSerializer.Serialize(
            EventEnvelope<JobUpdatedEvent>.Create(JobEventTypes.Updated, Payload(jobId, status)),
            KafkaJson.Options);

    private static JobUpdatedEvent Payload(Guid jobId, string status) => new(
        jobId, "Backend Developer", "Build APIs", Guid.NewGuid(), "Acme Corp", "Da Nang",
        20_000_000m, 40_000_000m, "VND", null, null, "FullTime", "Senior",
        Guid.NewGuid(), "C#", "Insurance", status, DateTime.UtcNow);

    private static (TestableConsumer Consumer, FakeSender Sender, IServiceProvider Provider) Build()
    {
        var sender = new FakeSender();
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<NotifDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<INotificationService>(sender);
        var provider = services.BuildServiceProvider();
        var consumer = new TestableConsumer(provider);
        return (consumer, sender, provider);
    }

    private sealed class FakeSender : INotificationService
    {
        public int Calls { get; private set; }
        public int FailFirst { get; set; }
        public string LastSubject { get; private set; } = string.Empty;

        public Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default)
        {
            Calls++;
            LastSubject = subject;
            if (Calls <= FailFirst)
            {
                throw new InvalidOperationException("SMTP unavailable");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestableConsumer : JobEventsConsumer
    {
        public TestableConsumer(IServiceProvider services)
            : base(
                Options.Create(new KafkaOptions()),
                new StubConfig(new Dictionary<string, string?>
                {
                    ["NOTIF_DEFAULT_RECIPIENT"] = "recruiter@example.test",
                    ["KAFKA_TOPIC_JOB_EVENTS"] = "job-events",
                }),
                services,
                NullLogger<JobEventsConsumer>.Instance)
        {
        }

        public Task<MessageOutcome> HandleAsync(string value) =>
            HandleMessageAsync(null, value, CancellationToken.None);
    }

    private sealed class StubConfig : IConfiguration
    {
        private readonly Dictionary<string, string?> _values;

        public StubConfig(Dictionary<string, string?> values) => _values = values;

        public string? this[string key]
        {
            get => _values.TryGetValue(key, out var value) ? value : null;
            set => _values[key] = value;
        }

        public IEnumerable<IConfigurationSection> GetChildren() => Array.Empty<IConfigurationSection>();

        public IChangeToken GetReloadToken() => throw new NotSupportedException();

        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
    }
}
