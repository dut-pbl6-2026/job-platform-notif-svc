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
using Npgsql;
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
        // Lower-cased so "Accepted" and "accepted" share one idempotency row.
        var normalizedStatus = (statusSnapshot?.Trim() ?? string.Empty).ToLowerInvariant();
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
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException dbEx) when (IsUniqueConflict(dbEx))
            {
                // A concurrent delivery already logged this notification; the email
                // went out above, so commit past it instead of looping forever.
                _logger.LogInformation(
                    dbEx,
                    "Notification already logged by a concurrent delivery. ApplicationId={ApplicationId} EventType={EventType}.",
                    applicationId, eventType);
                return MessageOutcome.Handled;
            }

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

    private static bool IsUniqueConflict(DbUpdateException ex)
    {
        Exception? current = ex;
        while (current is not null)
        {
            // Postgres unique_violation (23505), e.g. on
            // IX_notification_logs_ApplicationId_EventType_StatusSnapshot.
            if (current is PostgresException pg && pg.SqlState == "23505")
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
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