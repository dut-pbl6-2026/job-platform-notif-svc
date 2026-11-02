using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notif.Core.Entities;
using Notif.Core.Interfaces;
using Notif.Core.Templates;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Services;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Notif.Infrastructure.Workers;

/// <summary>Consumes application events and sends idempotent email notifications.</summary>
public class ApplicationEventsConsumer : KafkaConsumerService
{
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _services;
    private readonly ILogger<ApplicationEventsConsumer> _logger;

    public ApplicationEventsConsumer(
        IOptions<KafkaOptions> kafka,
        IConfiguration configuration,
        IServiceProvider services,
        ILogger<ApplicationEventsConsumer> logger)
        : base(kafka, logger)
    {
        _configuration = configuration;
        _services = services;
        _logger = logger;
    }

    protected override string Topic => ResolveSetting("KAFKA_TOPIC_APPLICATION_EVENTS", "application-events");

    protected override string GroupId => ResolveSetting("KAFKA_GROUP_NOTIF", "notif-svc");

    protected override async Task<MessageOutcome> HandleMessageAsync(string? key, string value, CancellationToken ct)
    {
        var eventType = TryGetEventType(value);
        if (string.IsNullOrWhiteSpace(eventType))
        {
            _logger.LogWarning("Kafka poison message on {Topic}: missing eventType. Skipping.", Topic);
            return MessageOutcome.Skip;
        }

        switch (eventType.Trim())
        {
            case ApplicationEventTypes.Submitted:
                if (!TryParseEnvelope<ApplicationSubmittedEvent>(value, out var submitted) || submitted is null)
                {
                    _logger.LogWarning("Kafka poison message on {Topic}: cannot parse {EventType}. Skipping.", Topic, eventType);
                    return MessageOutcome.Skip;
                }

                return await SendSubmittedAsync(submitted, ct);

            // SRS 8.5 names this event application.updated; the shared contract and the
            // app-svc producer use application.status_changed (plan v3.1). Accept both.
            case ApplicationEventTypes.StatusChanged:
            case "application.updated":
                if (!TryParseEnvelope<ApplicationStatusChangedEvent>(value, out var changed) || changed is null)
                {
                    _logger.LogWarning("Kafka poison message on {Topic}: cannot parse {EventType}. Skipping.", Topic, eventType);
                    return MessageOutcome.Skip;
                }

                return await SendStatusChangedAsync(changed, ct);

            default:
                _logger.LogWarning("Kafka unknown event {EventType} on {Topic}. Skipping.", eventType, Topic);
                return MessageOutcome.Skip;
        }
    }

    private async Task<MessageOutcome> SendSubmittedAsync(
        EventEnvelope<ApplicationSubmittedEvent> envelope,
        CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload.ApplicationId == Guid.Empty)
        {
            _logger.LogWarning("Kafka poison message on {Topic}: application.submitted with empty ApplicationId. Skipping.", Topic);
            return MessageOutcome.Skip;
        }

        var baseUrl = _configuration["NOTIF_BASE_URL"] ?? "";
        var (subject, html, text) = EmailTemplates.SubmittedToRecruiter(
            payload.JobTitle, payload.ApplicationId, payload.ApplicantId, payload.JobId, baseUrl);

        return await SendAndLogAsync(
            envelope.EventId, payload.ApplicationId, ApplicationEventTypes.Submitted, null,
            subject, html, text, EmailTemplates.ApplicationSubmitted, "recruiter", ct);
    }

    private async Task<MessageOutcome> SendStatusChangedAsync(
        EventEnvelope<ApplicationStatusChangedEvent> envelope,
        CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload.ApplicationId == Guid.Empty)
        {
            _logger.LogWarning("Kafka poison message on {Topic}: application.status_changed with empty ApplicationId. Skipping.", Topic);
            return MessageOutcome.Skip;
        }

        var (subject, html, text) = EmailTemplates.StatusChangedToApplicant(payload.JobTitle, payload.NewStatus);

        return await SendAndLogAsync(
            envelope.EventId, payload.ApplicationId, ApplicationEventTypes.StatusChanged, payload.NewStatus,
            subject, html, text, EmailTemplates.ApplicationStatusChanged, "applicant", ct);
    }

    private async Task<MessageOutcome> SendAndLogAsync(
        Guid eventId,
        Guid applicationId,
        string eventType,
        string? statusSnapshot,
        string subject,
        string html,
        string text,
        string template,
        string recipientKind,
        CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var recipient = ResolveRecipient(recipientKind);

        return await NotificationDelivery.SendAndLogAsync(
            db, sender, _logger, eventId, applicationId, eventType, statusSnapshot,
            recipient, subject, html, text, template, ct);
    }

    private string ResolveRecipient(string kind)
    {
        // [STUB] Real recipient lookup from auth/profile service is not yet implemented
        // (PBL6-35 follow-up). All emails are delivered to the configured fallback address.
        // Remove this stub once RecipientResolver integrates with the profile/auth API.
        var fallback = _configuration["NOTIF_DEFAULT_RECIPIENT"]
            ?? _configuration["SMTP_FROM"]
            ?? "noreply@job-platform.local";
        _logger.LogWarning(
            "[STUB] Recipient for {RecipientKind} resolved to configured fallback {Recipient}. "
            + "Real user lookup (PBL6-35) is not yet implemented — do not use in production without setting NOTIF_DEFAULT_RECIPIENT.",
            kind, fallback);
        return fallback;
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