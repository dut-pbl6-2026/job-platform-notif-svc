using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Workers;
using SharedKernel.Events;
using SharedKernel.Kafka;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// Regression tests for the Kafka redelivery contract:
/// a failed send must be retried on redelivery, not silently dropped by the
/// EventId idempotency check, and the log row must be reused (no unique violation).
/// Addresses Critical Issue #1 from PR review: idempotency vs retry logic conflict.
/// </summary>
public class ApplicationEventsConsumerRetryTests
{
    private sealed class FakeSender : IEmailSender
    {
        public int Calls { get; private set; }

        public int FailFirst { get; set; }

        public Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default)
        {
            Calls++;
            if (Calls <= FailFirst)
            {
                throw new InvalidOperationException("SMTP unavailable");
            }

            return Task.CompletedTask;
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

    private sealed class TestableConsumer : ApplicationEventsConsumer
    {
        public TestableConsumer(IOptions<KafkaOptions> kafka, IServiceProvider services)
            : base(kafka, new StubConfig(new Dictionary<string, string?>()), services,
                NullLogger<ApplicationEventsConsumer>.Instance)
        {
        }

        public Task<MessageOutcome> HandleAsync(string value, CancellationToken ct = default)
            => HandleMessageAsync(null, value, ct);
    }

    private static (TestableConsumer Consumer, FakeSender Sender, IServiceProvider Provider) Build(string dbName)
    {
        var sender = new FakeSender();
        var services = new ServiceCollection();
        services.AddDbContext<NotifDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IEmailSender>(sender);
        var provider = services.BuildServiceProvider();
        var consumer = new TestableConsumer(Options.Create(new KafkaOptions()), provider);
        return (consumer, sender, provider);
    }

    private static string SubmittedEnvelope(Guid applicationId)
    {
        var payload = new ApplicationSubmittedEvent(
            applicationId, Guid.NewGuid(), "Backend Dev", Guid.NewGuid(), DateTime.UtcNow);
        var envelope = EventEnvelope<ApplicationSubmittedEvent>.Create(ApplicationEventTypes.Submitted, payload);
        return JsonSerializer.Serialize(envelope, KafkaJson.Options);
    }

    // ── Critical Issue #1: idempotency vs retry ─────────────────────────────

    /// <summary>
    /// First delivery fails → Retry. Second delivery (Kafka redelivery) reuses
    /// the existing failed log row and succeeds → Handled. Only ONE log row exists.
    /// </summary>
    [Fact]
    public async Task FailedSend_IsRetriedOnRedelivery_WithSingleLogRow()
    {
        var dbName = Guid.NewGuid().ToString();
        var (consumer, sender, provider) = Build(dbName);
        sender.FailFirst = 1;
        var json = SubmittedEnvelope(Guid.NewGuid());

        var first = await consumer.HandleAsync(json);
        var second = await consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Retry, first);
        Assert.Equal(MessageOutcome.Handled, second);
        Assert.Equal(2, sender.Calls);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var logs = await db.EmailLogs.ToListAsync();
        var log = Assert.Single(logs);
        Assert.Equal("sent", log.Status);
    }

    /// <summary>
    /// Already-sent log short-circuits without resending — idempotent delivery.
    /// </summary>
    [Fact]
    public async Task SentLog_ShortCircuits_WithoutResending()
    {
        var dbName = Guid.NewGuid().ToString();
        var (consumer, sender, _) = Build(dbName);
        var json = SubmittedEnvelope(Guid.NewGuid());

        var first = await consumer.HandleAsync(json);
        var second = await consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Handled, first);
        Assert.Equal(MessageOutcome.Handled, second);
        Assert.Equal(1, sender.Calls);
    }

    // ── SRS alias / event routing ────────────────────────────────────────────

    [Fact]
    public async Task StatusChangedAlias_IsHandled()
    {
        var dbName = Guid.NewGuid().ToString();
        var (consumer, sender, _) = Build(dbName);
        var payload = new ApplicationStatusChangedEvent(
            Guid.NewGuid(), Guid.NewGuid(), "Backend Dev", Guid.NewGuid(),
            "pending", "accepted", Guid.NewGuid(), DateTime.UtcNow);
        var json = JsonSerializer.Serialize(
            EventEnvelope<ApplicationStatusChangedEvent>.Create("application.updated", payload),
            KafkaJson.Options);

        var outcome = await consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(1, sender.Calls);
    }

    // ── Poison / unknown message handling ────────────────────────────────────

    [Fact]
    public async Task PoisonJson_IsSkipped()
    {
        var dbName = Guid.NewGuid().ToString();
        var (consumer, sender, _) = Build(dbName);

        var outcome = await consumer.HandleAsync("{not-json");

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task MissingEventType_IsSkipped()
    {
        var dbName = Guid.NewGuid().ToString();
        var (consumer, sender, _) = Build(dbName);

        var outcome = await consumer.HandleAsync("{\"foo\":1}");

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task UnknownEventType_IsSkipped()
    {
        var dbName = Guid.NewGuid().ToString();
        var (consumer, sender, _) = Build(dbName);
        var json = "{\"eventId\":\"" + Guid.NewGuid() + "\",\"eventType\":\"job.created\",\"version\":1,\"payload\":{}}";

        var outcome = await consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task SubmittedWithEmptyApplicationId_IsSkipped()
    {
        var dbName = Guid.NewGuid().ToString();
        var (consumer, sender, _) = Build(dbName);
        var payload = new ApplicationSubmittedEvent(
            Guid.Empty, Guid.NewGuid(), "Backend Dev", Guid.NewGuid(), DateTime.UtcNow);
        var json = JsonSerializer.Serialize(
            EventEnvelope<ApplicationSubmittedEvent>.Create(ApplicationEventTypes.Submitted, payload),
            KafkaJson.Options);

        var outcome = await consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task StatusChangedWithEmptyApplicationId_IsSkipped()
    {
        var dbName = Guid.NewGuid().ToString();
        var (consumer, sender, _) = Build(dbName);
        var payload = new ApplicationStatusChangedEvent(
            Guid.Empty, Guid.NewGuid(), "Backend Dev", Guid.NewGuid(),
            "pending", "accepted", Guid.NewGuid(), DateTime.UtcNow);
        var json = JsonSerializer.Serialize(
            EventEnvelope<ApplicationStatusChangedEvent>.Create(ApplicationEventTypes.StatusChanged, payload),
            KafkaJson.Options);

        var outcome = await consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Equal(0, sender.Calls);
    }
}
