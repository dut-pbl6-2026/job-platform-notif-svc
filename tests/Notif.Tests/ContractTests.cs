using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Notif.Infrastructure.Services;
using SharedKernel.Events;
using SharedKernel.Kafka;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// Contract: the wire shapes this service depends on. Kafka payloads are the ones the consumers read
/// (eventType, eventId, payload fields); the Telegram webhook update is the one TelegramUpdateHandler reads.
/// A rename in the shared package or in the consumers breaks these tests first.
/// </summary>
public class ContractTests
{
    [Fact]
    public void KafkaTopicEventTypes_MatchPublishedNames()
    {
        Assert.Equal("application.submitted", ApplicationEventTypes.Submitted);
        Assert.Equal("application.status_changed", ApplicationEventTypes.StatusChanged);
        Assert.Equal("job.updated", JobEventTypes.Updated);
    }

    [Fact]
    public void ApplicationSubmittedEnvelope_CarriesFieldsTheConsumerReads()
    {
        var payload = new ApplicationSubmittedEvent(
            Guid.NewGuid(), Guid.NewGuid(), "Backend Dev", Guid.NewGuid(), DateTime.UtcNow);
        var node = ToNode(EventEnvelope<ApplicationSubmittedEvent>.Create(ApplicationEventTypes.Submitted, payload));

        Assert.Equal("application.submitted", node["eventType"]!.GetValue<string>());
        Assert.True(Guid.TryParse(node["eventId"]!.GetValue<string>(), out _));
        var p = node["payload"]!.AsObject();
        Assert.True(Guid.TryParse(p["applicationId"]!.GetValue<string>(), out _));
        Assert.Equal("Backend Dev", p["jobTitle"]!.GetValue<string>());
        Assert.NotNull(p["applicantId"]);
        Assert.NotNull(p["jobId"]);
    }

    [Fact]
    public void ApplicationStatusChangedEnvelope_CarriesFieldsTheConsumerReads()
    {
        var payload = new ApplicationStatusChangedEvent(
            Guid.NewGuid(), Guid.NewGuid(), "Backend Dev", Guid.NewGuid(),
            "pending", "accepted", Guid.NewGuid(), DateTime.UtcNow);
        var node = ToNode(EventEnvelope<ApplicationStatusChangedEvent>.Create(ApplicationEventTypes.StatusChanged, payload));

        Assert.Equal("application.status_changed", node["eventType"]!.GetValue<string>());
        var p = node["payload"]!.AsObject();
        Assert.NotNull(p["applicationId"]);
        Assert.Equal("accepted", p["newStatus"]!.GetValue<string>());
        Assert.Equal("pending", p["previousStatus"]!.GetValue<string>());
        Assert.Equal("Backend Dev", p["jobTitle"]!.GetValue<string>());
    }

    [Fact]
    public void JobUpdatedEnvelope_CarriesStatusAndJobId()
    {
        var jobId = Guid.NewGuid();
        var payload = new JobUpdatedEvent(
            jobId, "Backend Developer", "Build APIs", Guid.NewGuid(), "Acme Corp", "Da Nang",
            20_000_000m, 40_000_000m, "VND", null, null, "FullTime", "Senior",
            Guid.NewGuid(), "C#", "Insurance", "Active", DateTime.UtcNow);
        var node = ToNode(EventEnvelope<JobUpdatedEvent>.Create(JobEventTypes.Updated, payload));

        Assert.Equal("job.updated", node["eventType"]!.GetValue<string>());
        var p = node["payload"]!.AsObject();
        Assert.Equal(jobId, Guid.Parse(p["jobId"]!.GetValue<string>()));
        Assert.Equal("Active", p["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task TelegramSubscribeUpdate_ReadsMessageChatIdAndText()
    {
        // Literal inbound update shape from Telegram (message.chat.id, message.text).
        const string update = "{\"update_id\":10001,\"message\":{\"message_id\":7,\"chat\":{\"id\":4242,\"type\":\"private\"},\"text\":\"/subscribe\"}}";
        using var db = BuildDb();
        var handler = new TelegramUpdateHandler(db, new FakeTelegramSender(), NullLogger<TelegramUpdateHandler>.Instance);

        await handler.HandleAsync(update);

        var row = db.TelegramSubscriptions.Single();
        Assert.Equal(4242, row.ChatId);
        Assert.True(row.IsActive);
    }

    private static JsonObject ToNode<T>(EventEnvelope<T> envelope)
        => JsonNode.Parse(JsonSerializer.Serialize(envelope, KafkaJson.Options))!.AsObject();

    private static Notif.Infrastructure.Data.NotifDbContext BuildDb()
        => new(new DbContextOptionsBuilder<Notif.Infrastructure.Data.NotifDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
