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
/// application.status_changed sends the applicant template.
/// At-least-once: EmailLog.EventId is unique, redeliveries skip resend.
/// NOTE: events carry ids only (no PII). Recipient resolves from
/// NOTIF_DEFAULT_RECIPIENT until user/job lookup lands (PBL6 follow-up).
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
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        if (await db.EmailLogs.AnyAsync(e => e.EventId == eventId, ct))
        {
            return MessageOutcome.Handled;
        }

        var recipient = ResolveRecipient("recruiter");
        var (subject, html, text) = EmailTemplates.SubmittedToRecruiter(payload.JobTitle, payload.ApplicationId, payload.ApplicantId, payload.JobId);
        return await SendAndLogAsync(db, sender, eventId, recipient, subject, html, text, EmailTemplates.ApplicationSubmitted, ct);
    }

    private async Task<MessageOutcome> SendStatusChangedAsync(ApplicationStatusChangedEvent payload, Guid eventId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        if (await db.EmailLogs.AnyAsync(e => e.EventId == eventId, ct))
        {
            return MessageOutcome.Handled;
        }

        var recipient = ResolveRecipient("applicant");
        var (subject, html, text) = EmailTemplates.StatusChangedToApplicant(payload.JobTitle, payload.NewStatus);
        return await SendAndLogAsync(db, sender, eventId, recipient, subject, html, text, EmailTemplates.ApplicationStatusChanged, ct);
    }

    private async Task<MessageOutcome> SendAndLogAsync(NotifDbContext db, IEmailSender sender, Guid eventId, string recipient, string subject, string html, string text, string template, CancellationToken ct)
    {
        var log = new EmailLog(eventId, recipient, subject, template);
        db.EmailLogs.Add(log);
        try
        {
            await sender.SendAsync(recipient, subject, html, text, ct);
            log.MarkSent();
            await db.SaveChangesAsync(ct);
            return MessageOutcome.Handled;
        }
        catch (DbUpdateException ex)
        {
            _typedLogger.LogWarning(ex, "Email log conflict EventId={EventId}. Skipping resend.", eventId);
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
        var fallback = _config["NOTIF_DEFAULT_RECIPIENT"]
            ?? _config["SMTP_FROM"]
            ?? "recruiter@job-platform.local";
        _typedLogger.LogDebug("Resolving {Kind} recipient via fallback {Recipient} (user lookup TODO).", kind, fallback);
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
