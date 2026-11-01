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
/// silent. A notification is sent only when the payload also carries a
/// <c>previousStatus</c> transition — without it an <c>Active</c> event cannot be told
/// apart from an ordinary edit of an already-live job.
/// </para>
/// <para>
/// Gated by <see cref="EnabledSetting"/> (off by default): the event carries no
/// recruiter address, so until a real recipient lookup exists every mail would go to
/// the configured fallback inbox. Idempotency uses one <c>notification_logs</c> row per
/// (job id, event type, status) — the job id is stored in the aggregate id column —
/// so Kafka redelivery and repeated Active updates do not send duplicate emails.
/// </para>
/// </summary>
public class JobEventsConsumer : KafkaConsumerService
{
    /// <summary>
    /// Feature flag that turns the job approval/rejection emails on. Defaults to off:
    /// enabling requires a recruiter recipient lookup and the job-svc contract change
    /// that publishes <c>previousStatus</c> / <c>rejectionReason</c>.
    /// </summary>
    public const string EnabledSetting = "NOTIF_JOB_EVENTS_ENABLED";

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

    /// <summary>
    /// Reads <see cref="EnabledSetting"/> as a boolean. Anything other than
    /// <c>true</c> (including unset) leaves the consumer disabled.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns><c>true</c> when the consumer should be hosted.</returns>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return bool.TryParse(configuration[EnabledSetting]?.Trim(), out var enabled) && enabled;
    }

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
        if (!IsApprovalOutcome(status))
        {
            _logger.LogDebug(
                "Job {JobId} status {Status} needs no recruiter notification.", payload.JobId, status);
            return MessageOutcome.Handled;
        }

        var previousStatus = TryGetPayloadString(value, "previousStatus", "PreviousStatus")?.Trim();
        if (string.IsNullOrWhiteSpace(previousStatus))
        {
            // Without a transition signal an approval outcome cannot be distinguished
            // from an ordinary edit of an already-live job, so do not send anything.
            _logger.LogWarning(
                "job.updated for {JobId} has status {Status} but no previousStatus transition signal; "
                + "skipping the recruiter email (requires the job-svc event contract change).",
                payload.JobId, status);
            return MessageOutcome.Handled;
        }

        if (string.Equals(previousStatus, status, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Job {JobId} is already {Status}; not a transition, no email.", payload.JobId, status);
            return MessageOutcome.Handled;
        }

        var rejectionReason = TryGetPayloadString(value, "rejectionReason", "RejectionReason");
        return await SendJobNotificationAsync(updated.EventId, payload, status, rejectionReason, ct);
    }

    private async Task<MessageOutcome> SendJobNotificationAsync(
        Guid eventId,
        JobUpdatedEvent payload,
        string status,
        string? rejectionReason,
        CancellationToken ct)
    {
        var recipient = ResolveRecipient();
        if (recipient is null)
        {
            // Permanent misconfiguration: Retry would redeliver the same message forever
            // and stall the partition. Startup validation rejects this when the feature
            // flag is on; this branch is defence in depth for config reloads.
            _logger.LogError(
                "Neither NOTIF_DEFAULT_RECIPIENT nor SMTP_FROM is configured; skipping job notification for {JobId}.",
                payload.JobId);
            return MessageOutcome.Skip;
        }

        // TODO(PBL6-35): resolve the recruiter address from auth/profile once a real
        // recipient lookup exists. Until then NOTIF_JOB_EVENTS_ENABLED keeps this
        // consumer off in production.
        var isRejected = string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase);
        var (subject, html, text) = isRejected
            ? EmailTemplates.JobRejectedToRecruiter(string.Empty, payload.Title, rejectionReason)
            : EmailTemplates.JobApprovedToRecruiter(string.Empty, payload.Title);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<INotificationService>();

        return await NotificationDelivery.SendAndLogAsync(
            db, sender, _logger, eventId, payload.JobId, JobEventTypes.Updated,
            isRejected ? "rejected" : "active", recipient, subject, html, text,
            isRejected ? EmailTemplates.JobRejected : EmailTemplates.JobApproved, ct);
    }

    private static bool IsApprovalOutcome(string status) =>
        string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase);

    private string? ResolveRecipient()
    {
        var configured = _configuration["NOTIF_DEFAULT_RECIPIENT"] ?? _configuration["SMTP_FROM"];
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    /// <summary>
    /// Reads an optional payload field that is not part of the shared record yet, so the
    /// job-svc contract can add <c>previousStatus</c>/<c>rejectionReason</c> without a
    /// breaking SharedKernel version bump here.
    /// </summary>
    private static string? TryGetPayloadString(string json, string camelCase, string pascalCase)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetProperty(document.RootElement, "payload", "Payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return TryGetProperty(payload, camelCase, pascalCase, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProperty(
        JsonElement element, string camelCase, string pascalCase, out JsonElement value)
    {
        if (element.TryGetProperty(camelCase, out value))
        {
            return true;
        }

        return element.TryGetProperty(pascalCase, out value);
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
