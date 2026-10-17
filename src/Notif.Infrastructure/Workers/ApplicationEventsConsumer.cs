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

/// <summary>
/// Kafka application-events consumer (PBL6-35, SRS KAFKA-01-04).
/// On application.submitted sends the recruiter template; on
/// application.status_changed (alias application.updated per SRS 8.5.1)
/// sends the applicant template.
/// At-least-once delivery with idempotent retry: on first delivery a new
/// EmailLog row is created; if the send fails, Kafka redelivers the same
/// EventId and the existing failed/pending log is reused so no unique
/// constraint is violated. Only a log already in status "sent" short-circuits.
/// Recipient is resolved from NOTIF_DEFAULT_RECIPIENT (stub — see below).
/// </summary>
public class ApplicationEventsConsumer : KafkaConsumerService
{
    private readonly IConfiguration _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<ApplicationEventsConsumer> _typedLogger;

    public ApplicationEventsConsumer(
        IOptions<KafkaOptions> kafka,
        IConfiguration config,
        IServiceProvider services,
        ILogger<ApplicationEventsConsumer> logger)
        : base(kafka, logger)
    {
        _config = config;
        _services = services;
        _typedLogger = logger;
    }

    protected override string Topic =>
        (_config["KAFKA_TOPIC_APPLICATION_EVENTS"] ?? "application-events").Trim() is { } t && !string.IsNullOrWhiteSpace(t) ? t.Trim() : "application-events";

    protected override string GroupId =>
        (_config["KAFKA_GROUP_NOTIF"] ?? _config["KAFKA_GROUP_ID"] ?? "notif-svc").Trim() is { } g && !string.IsNullOrWhiteSpace(g) ? g.Trim() : "notif-svc";

    protected override async Task<MessageOutcome> HandleMessageAsync(string? key, string value, CancellationToken ct)
    {
        var eventType = TryGetEventType(value);
        if (string.IsNullOrWhiteSpace(eventType))
        {
            _typedLogger.LogWarning("Kafka poison message on {Topic}: missing eventType. Skipping.", Topic);
            return MessageOutcome.Skip;
        }

        try
        {
            switch (eventType.Trim())
            {
                case ApplicationEventTypes.Submitted:
                    if (TryParseEnvelope<ApplicationSubmittedEvent>(value, out var submitted) && submitted is not null)
                    {
                        return await SendSubmittedAsync(submitted.Payload, submitted.EventId, ct);
                    }

                    return MessageOutcome.Skip;

                case ApplicationEventTypes.StatusChanged:
                case "application.updated":
                    if (TryParseEnvelope<ApplicationStatusChangedEvent>(value, out var changed) && changed is not null)
                    {
                        return await SendStatusChangedAsync(changed.Payload, changed.EventId, ct);
                    }

                    return MessageOutcome.Skip;

                default:
                    _typedLogger.LogWarning("Kafka unknown event {EventType} on {Topic}. Skipping.", eventType, Topic);
                    return MessageOutcome.Skip;
            }
        }
        catch (JsonException ex)
        {
            _typedLogger.LogWarning(ex, "Kafka poison JSON on {Topic}. Skipping.", Topic);
            return MessageOutcome.Skip;
        }
    }

    private async Task<MessageOutcome> SendSubmittedAsync(ApplicationSubmittedEvent payload, Guid eventId, CancellationToken ct)
    {
        if (payload.ApplicationId == Guid.Empty)
        {
            _typedLogger.LogWarning(
                "Kafka poison message on {Topic}: application.submitted with empty ApplicationId. Skipping.", Topic);
            return MessageOutcome.Skip;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var existing = await db.EmailLogs.FirstOrDefaultAsync(e => e.EventId == eventId, ct);
        if (existing is { Status: "sent" })
        {
            return MessageOutcome.Handled;
        }

        var recipient = ResolveRecipient("recruiter");
        var baseUrl = _config["NOTIF_BASE_URL"] ?? "";
        var (subject, html, text) = EmailTemplates.SubmittedToRecruiter(payload.JobTitle, payload.ApplicationId, payload.ApplicantId, payload.JobId, baseUrl);
        return await SendAndLogAsync(db, sender, existing, eventId, recipient, subject, html, text, EmailTemplates.ApplicationSubmitted, ct);
    }

    private async Task<MessageOutcome> SendStatusChangedAsync(ApplicationStatusChangedEvent payload, Guid eventId, CancellationToken ct)
    {
        if (payload.ApplicationId == Guid.Empty)
        {
            _typedLogger.LogWarning(
                "Kafka poison message on {Topic}: application.status_changed with empty ApplicationId. Skipping.", Topic);
            return MessageOutcome.Skip;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var existing = await db.EmailLogs.FirstOrDefaultAsync(e => e.EventId == eventId, ct);
        if (existing is { Status: "sent" })
        {
            return MessageOutcome.Handled;
        }

        var recipient = ResolveRecipient("applicant");
        var (subject, html, text) = EmailTemplates.StatusChangedToApplicant(payload.JobTitle, payload.NewStatus);
        return await SendAndLogAsync(db, sender, existing, eventId, recipient, subject, html, text, EmailTemplates.ApplicationStatusChanged, ct);
    }

    /// <summary>
    /// Resends through an existing failed/pending log (Kafka redelivery) or creates
    /// a fresh one. Only a log already in status "sent" short-circuits upstream, so a
    /// transient SMTP failure is retried instead of being silently dropped, while the
    /// unique EventId still guarantees at-most-once delivery for a successful send.
    /// </summary>
    private async Task<MessageOutcome> SendAndLogAsync(NotifDbContext db, IEmailSender sender, EmailLog? existing, Guid eventId, string recipient, string subject, string html, string text, string template, CancellationToken ct)
    {
        var log = existing ?? new EmailLog(eventId, recipient, subject, template);
        if (existing is null)
        {
            db.EmailLogs.Add(log);
        }

        try
        {
            await sender.SendAsync(recipient, subject, html, text, ct);
            log.MarkSent();
            await db.SaveChangesAsync(ct);
            return MessageOutcome.Handled;
        }
        catch (DbUpdateException ex)
        {
            // Concurrent delivery: another consumer instance already committed a
            // "sent" row for this EventId between our FirstOrDefaultAsync and
            // SaveChangesAsync. The email was sent above, so treat as handled.
            _typedLogger.LogWarning(ex, "Email log conflict EventId={EventId}: concurrent delivery already saved. Treating as handled.", eventId);
            return MessageOutcome.Handled;
        }
        catch (Exception ex)
        {
            log.MarkFailed(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
            _typedLogger.LogWarning(ex, "Email send failed EventId={EventId}. Will retry.", eventId);
            return MessageOutcome.Retry;
        }
    }

    private string ResolveRecipient(string kind)
    {
        // [STUB] Real recipient lookup from auth/profile service is not yet implemented
        // (PBL6-35 follow-up). All emails are delivered to the configured fallback address.
        // Remove this stub once RecipientResolver integrates with the profile/auth API.
        var fallback = _config["NOTIF_DEFAULT_RECIPIENT"]
            ?? _config["SMTP_FROM"]
            ?? "noreply@job-platform.local";
        _typedLogger.LogWarning(
            "[STUB] Recipient for {RecipientKind} resolved to configured fallback {Recipient}. "
            + "Real user lookup (PBL6-35) is not yet implemented — do not use in production without setting NOTIF_DEFAULT_RECIPIENT.",
            kind, fallback);
        return fallback;
    }

    private static string? TryGetEventType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("eventType", out var camel) && camel.ValueKind == JsonValueKind.String)
            {
                return camel.GetString();
            }

            if (doc.RootElement.TryGetProperty("EventType", out var pascal) && pascal.ValueKind == JsonValueKind.String)
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
