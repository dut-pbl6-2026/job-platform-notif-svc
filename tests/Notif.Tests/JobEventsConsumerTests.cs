using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
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
/// The consumer emails the recruiter on a real status transition only (approval/rejection),
/// never on an ordinary edit of an already-live job, and stays idempotent on redelivery
/// via the notification log.
/// </summary>
public class JobEventsConsumerTests
{
    [Fact]
    public async Task JobUpdated_Active_TransitionFromPending_SendsApprovedEmailAndLogs()
    {
        var jobId = Guid.NewGuid();
        var (consumer, sender, provider) = Build();

        var outcome = await consumer.HandleAsync(
            Envelope(jobId, "Active", previousStatus: "Pending"));

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
        var json = Envelope(Guid.NewGuid(), "Active", previousStatus: "Pending");

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
    public async Task JobUpdated_Active_EditOfLiveJob_SendsNothing()
    {
        // Ordinary edit of an already-approved job: the event carries no status change.
        var (consumer, sender, provider) = Build();

        var outcome = await consumer.HandleAsync(
            Envelope(Guid.NewGuid(), "Active", previousStatus: "Active"));

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(0, sender.Calls);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        Assert.Equal(0, await db.NotificationLogs.CountAsync());
    }

    [Fact]
    public async Task JobUpdated_Active_WithoutPreviousStatus_SendsNothing()
    {
        // The shared contract has no previousStatus yet: without a transition signal an
        // Active event is indistinguishable from an edit, so it must not be treated as
        // an approval.
        var (consumer, sender, provider) = Build();

        var outcome = await consumer.HandleAsync(
            Envelope(Guid.NewGuid(), "Active", previousStatus: null));

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(0, sender.Calls);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        Assert.Equal(0, await db.NotificationLogs.CountAsync());
    }

    [Fact]
    public async Task JobUpdated_Rejected_TransitionFromPending_SendsRejectedEmailWithReason()
    {
        var (consumer, sender, provider) = Build();

        var outcome = await consumer.HandleAsync(
            Envelope(Guid.NewGuid(), "Rejected", previousStatus: "Pending", rejectionReason: "Spam content"));

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(1, sender.Calls);
        Assert.Contains("not approved", sender.LastSubject);
        Assert.Contains("Spam content", sender.LastText);
        Assert.Contains("Spam content", sender.LastHtml);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var log = await db.NotificationLogs.SingleAsync();
        Assert.Equal("rejected", log.StatusSnapshot);
        Assert.Equal(EmailTemplates.JobRejected, log.TemplateName);
    }

    [Fact]
    public async Task JobUpdated_Rejected_WithoutReason_FallsBackToNoReasonProvided()
    {
        var (consumer, sender, _) = Build();

        var outcome = await consumer.HandleAsync(
            Envelope(Guid.NewGuid(), "Rejected", previousStatus: "Pending", rejectionReason: null));

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(1, sender.Calls);
        Assert.Contains("No reason provided", sender.LastText);
    }

    [Fact]
    public async Task JobUpdated_Rejected_EditOfRejectedJob_SendsNothing()
    {
        var (consumer, sender, provider) = Build();

        var outcome = await consumer.HandleAsync(
            Envelope(Guid.NewGuid(), "Rejected", previousStatus: "Rejected", rejectionReason: "Spam content"));

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(0, sender.Calls);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        Assert.Equal(0, await db.NotificationLogs.CountAsync());
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Closed")]
    [InlineData("Deleted")]
    public async Task JobUpdated_NonApprovalStatus_SendsNothing(string status)
    {
        var (consumer, sender, provider) = Build();

        var outcome = await consumer.HandleAsync(
            Envelope(Guid.NewGuid(), status, previousStatus: "Pending"));

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

        var outcome = await consumer.HandleAsync(
            Envelope(Guid.Empty, "Active", previousStatus: "Pending"));

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
        var json = Envelope(Guid.NewGuid(), "Active", previousStatus: "Pending");

        var first = await consumer.HandleAsync(json);
        var second = await consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Retry, first);
        Assert.Equal(MessageOutcome.Handled, second);
        Assert.Equal(2, sender.Calls);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        Assert.Equal("sent", (await db.NotificationLogs.SingleAsync()).Status);
    }

    [Fact]
    public async Task JobUpdated_NoRecipientConfigured_IsSkippedInsteadOfStalling()
    {
        // A missing recipient is a permanent misconfiguration: Retry would redeliver the
        // same message forever and block the partition.
        var (consumer, sender, _) = Build(withRecipient: false);

        var outcome = await consumer.HandleAsync(
            Envelope(Guid.NewGuid(), "Active", previousStatus: "Pending"));

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Equal(0, sender.Calls);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("yes", false)]
    public void IsEnabled_ReadsFeatureFlag(string? value, bool expected)
    {
        var config = new StubConfig(new Dictionary<string, string?>
        {
            [JobEventsConsumer.EnabledSetting] = value,
        });

        Assert.Equal(expected, JobEventsConsumer.IsEnabled(config));
    }

    private static string Envelope(
        Guid jobId,
        string status,
        string? previousStatus,
        string? rejectionReason = null)
    {
        var payload = JsonSerializer.SerializeToNode(Payload(jobId, status), KafkaJson.Options)!.AsObject();
        // previousStatus/rejectionReason are not on the shared record yet; the consumer
        // reads them from the envelope so the contract can be extended without a bump.
        payload["previousStatus"] = previousStatus;
        payload["rejectionReason"] = rejectionReason;

        var envelope = new JsonObject
        {
            ["eventId"] = Guid.NewGuid(),
            ["eventType"] = JobEventTypes.Updated,
            ["version"] = 1,
            ["timestamp"] = DateTime.UtcNow,
            ["payload"] = payload,
        };

        return envelope.ToJsonString();
    }

    private static JobUpdatedEvent Payload(Guid jobId, string status) => new(
        jobId, "Backend Developer", "Build APIs", Guid.NewGuid(), "Acme Corp", "Da Nang",
        20_000_000m, 40_000_000m, "VND", null, null, "FullTime", "Senior",
        Guid.NewGuid(), "C#", "Insurance", status, DateTime.UtcNow);

    private static (TestableConsumer Consumer, FakeSender Sender, IServiceProvider Provider) Build(
        bool withRecipient = true)
    {
        var sender = new FakeSender();
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<NotifDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<INotificationService>(sender);
        var provider = services.BuildServiceProvider();
        var consumer = new TestableConsumer(provider, withRecipient);
        return (consumer, sender, provider);
    }

    private sealed class FakeSender : INotificationService
    {
        public int Calls { get; private set; }
        public int FailFirst { get; set; }
        public string LastSubject { get; private set; } = string.Empty;
        public string LastHtml { get; private set; } = string.Empty;
        public string LastText { get; private set; } = string.Empty;

        public Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default)
        {
            Calls++;
            LastSubject = subject;
            LastHtml = htmlBody;
            LastText = textBody;
            if (Calls <= FailFirst)
            {
                throw new InvalidOperationException("SMTP unavailable");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestableConsumer : JobEventsConsumer
    {
        public TestableConsumer(IServiceProvider services, bool withRecipient)
            : base(
                Options.Create(new KafkaOptions()),
                new StubConfig(BuildConfig(withRecipient)),
                services,
                NullLogger<JobEventsConsumer>.Instance)
        {
        }

        public Task<MessageOutcome> HandleAsync(string value) =>
            HandleMessageAsync(null, value, CancellationToken.None);

        private static Dictionary<string, string?> BuildConfig(bool withRecipient)
        {
            var values = new Dictionary<string, string?>
            {
                [JobEventsConsumer.EnabledSetting] = "true",
                ["KAFKA_TOPIC_JOB_EVENTS"] = "job-events",
            };

            if (withRecipient)
            {
                values["NOTIF_DEFAULT_RECIPIENT"] = "recruiter@example.test";
            }

            return values;
        }
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
