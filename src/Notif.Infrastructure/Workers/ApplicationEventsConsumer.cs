using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notif.Core.Entities;
using Notif.Core.Interfaces;
using Notif.Core.Templates;
using Notif.Infrastructure.Data;
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

            case ApplicationEventTypes.StatusChanged:
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
        var (subject, html, text) = EmailTemplates.SubmittedToRecruiter(
            payload.JobTitle, payload.ApplicationId, payload.ApplicantId, payload.JobId);

        return await SendAndLogAsync(
            envelope.EventId, payload.ApplicationId, ApplicationEventTypes.Submitted, null,
            subject, html, text, EmailTemplates.ApplicationSubmitted, "recruiter", ct);
    }

    private async Task<MessageOutcome> SendStatusChangedAsync(
        EventEnvelope<ApplicationStatusChangedEvent> envelope,
        CancellationToken ct)
    {
        var payload = envelope.Payload;
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
        var normalizedStatus = statusSnapshot?.Trim() ?? string.Empty;
        var existing = await db.NotificationLogs.FirstOrDefaultAsync(
            x => x.ApplicationId == applicationId
                && x.EventType == eventType
            && x.StatusSnapshot == normalizedStatus,
            ct);

        if (existing is { Status: "sent" })
        {
            _logger.LogInformation(
                "Notification already sent. ApplicationId={ApplicationId} EventType={EventType} Status={StatusSnapshot}.",
                applicationId, eventType, normalizedStatus);
            return MessageOutcome.Handled;
        }

        var recipient = ResolveRecipient(recipientKind);
        var log = existing ?? new NotificationLog(
            eventId, applicationId, eventType, normalizedStatus, recipient, subject, template);

        if (existing is null)
        {
            db.NotificationLogs.Add(log);
        }

        try
        {
            await sender.SendAsync(recipient, subject, html, text, ct);
            log.MarkSent();
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Email sent. ApplicationId={ApplicationId} EventType={EventType} RecipientKind={RecipientKind}.",
                applicationId, eventType, recipientKind);
            return MessageOutcome.Handled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.MarkFailed(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
            _logger.LogWarning(
                ex,
                "Email send failed. ApplicationId={ApplicationId} EventType={EventType}. Will retry.",
                applicationId, eventType);
            return MessageOutcome.Retry;
        }
    }

    private string ResolveRecipient(string kind)
    {
        var fallback = _configuration["NOTIF_DEFAULT_RECIPIENT"]
            ?? _configuration["SMTP_FROM"]
            ?? "recruiter@job-platform.local";
        _logger.LogDebug(
            "Recipient lookup for {RecipientKind} is using configured fallback. Recipient={Recipient}.",
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