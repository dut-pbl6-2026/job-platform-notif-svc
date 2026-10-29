using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notif.Core.Interfaces;
using Notif.Core.Templates;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Services;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Notif.Infrastructure.Workers;

/// <summary>
/// Consumes <c>job-events</c> and emails the recruiter when the admin approval
/// workflow approves or rejects a job (feat(admin) Slice 5, JOB-01).
/// <para>
/// Only <c>job.updated</c> is relevant: <c>Pending</c> is silent, <c>Active</c> means
/// approved and <c>Rejected</c> means rejected. Other statuses (Closed/Deleted) are
/// silent. Idempotency uses one <c>notification_logs</c> row per
/// (job id, event type, status) — the job id is stored in the aggregate id column —
/// so Kafka redelivery and repeated Active updates do not send duplicate emails.
/// </para>
/// </summary>
public class JobEventsConsumer : KafkaConsumerService
{
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _services;
    private readonly ILogger<JobEventsConsumer> _logger;

    public JobEventsConsumer(
        IOptions<KafkaOptions> kafka,
        IConfiguration configuration,
        IServiceProvider services,
        ILogger<JobEventsConsumer> logger)
        : base(kafka, logger)
    {
        _configuration = configuration;
        _services = services;
        _logger = logger;
    }

    protected override string Topic => ResolveSetting("KAFKA_TOPIC_JOB_EVENTS", "job-events");

    protected override string GroupId => ResolveSetting("KAFKA_GROUP_JOB_NOTIF", "notif-svc");

    protected override async Task<MessageOutcome> HandleMessageAsync(string? key, string value, CancellationToken ct)
    {
        var eventType = TryGetEventType(value);
        if (string.IsNullOrWhiteSpace(eventType))
        {
            _logger.LogWarning("Kafka poison message on {Topic}: missing eventType. Skipping.", Topic);
            return MessageOutcome.Skip;
        }

        if (!string.Equals(eventType.Trim(), JobEventTypes.Updated, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "Kafka event {EventType} on {Topic} is not handled by the job notification consumer. Skipping.",
                eventType, Topic);
            return MessageOutcome.Skip;
        }

        if (!TryParseEnvelope<JobUpdatedEvent>(value, out var updated) || updated is null)
        {
            _logger.LogWarning("Kafka poison message on {Topic}: cannot parse {EventType}. Skipping.", Topic, eventType);
            return MessageOutcome.Skip;
        }

        var payload = updated.Payload;
        if (payload.JobId == Guid.Empty)
        {
            _logger.LogWarning("Kafka poison message on {Topic}: job.updated with empty JobId. Skipping.", Topic);
            return MessageOutcome.Skip;
        }

        var status = payload.Status?.Trim() ?? string.Empty;
        if (string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            return await SendJobNotificationAsync(
                updated.EventId, payload, "active", EmailTemplates.JobApproved, ct);
        }

        if (string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            return await SendJobNotificationAsync(
                updated.EventId, payload, "rejected", EmailTemplates.JobRejected, ct);
        }

        _logger.LogDebug(
            "Job {JobId} status {Status} needs no recruiter notification.", payload.JobId, status);
        return MessageOutcome.Handled;
    }

    private async Task<MessageOutcome> SendJobNotificationAsync(
        Guid eventId,
        JobUpdatedEvent payload,
        string statusSnapshot,
        string templateName,
        CancellationToken ct)
    {
        var recipient = _configuration["NOTIF_DEFAULT_RECIPIENT"] ?? _configuration["SMTP_FROM"];
        if (string.IsNullOrWhiteSpace(recipient))
        {
            _logger.LogError(
                "Neither NOTIF_DEFAULT_RECIPIENT nor SMTP_FROM is configured; cannot send job notification for {JobId}. Will retry.",
                payload.JobId);
            return MessageOutcome.Retry;
        }

        // TODO(PBL6-35): resolve the recruiter address from auth/profile once a real
        // recipient lookup exists. Until then all job emails use the configured fallback.
        var (subject, html, text) = templateName == EmailTemplates.JobRejected
            ? EmailTemplates.JobRejectedToRecruiter(string.Empty, payload.Title, reason: null)
            : EmailTemplates.JobApprovedToRecruiter(string.Empty, payload.Title);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<INotificationService>();

        return await NotificationDelivery.SendAndLogAsync(
            db, sender, _logger, eventId, payload.JobId, JobEventTypes.Updated,
            statusSnapshot, recipient.Trim(), subject, html, text, templateName, ct);
    }

    private string ResolveSetting(string key, string fallback)
    {
        var value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? TryGetEventType(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("eventType", out var camel)
                && camel.ValueKind == JsonValueKind.String)
            {
                return camel.GetString();
            }

            if (document.RootElement.TryGetProperty("EventType", out var pascal)
                && pascal.ValueKind == JsonValueKind.String)
            {
                return pascal.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
