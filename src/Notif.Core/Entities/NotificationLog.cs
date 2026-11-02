using SharedKernel;

namespace Notif.Core.Entities;

/// <summary>Persistent idempotency and delivery log for Kafka-triggered notifications.</summary>
/// <remarks>
/// <see cref="ApplicationId"/> stores the event aggregate id: the application id for
/// application events, and the job id for job-approval events. The unique index over
/// (aggregate id, event type, status snapshot) is what makes consumer redelivery safe.
/// </remarks>
public sealed class NotificationLog : Entity
{
    public Guid EventId { get; private set; }
    public Guid ApplicationId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string StatusSnapshot { get; private set; } = string.Empty;
    public string RecipientEmail { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string TemplateName { get; private set; } = string.Empty;
    public string Status { get; private set; } = "pending";
    public DateTime? SentAt { get; private set; }
    public string? ErrorMessage { get; private set; }

    private NotificationLog() { }

    public NotificationLog(
        Guid eventId,
        Guid applicationId,
        string eventType,
        string? statusSnapshot,
        string recipientEmail,
        string subject,
        string templateName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType, nameof(eventType));
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEmail, nameof(recipientEmail));
        ArgumentException.ThrowIfNullOrWhiteSpace(subject, nameof(subject));
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName, nameof(templateName));

        EventId = eventId == Guid.Empty ? Guid.NewGuid() : eventId;
        ApplicationId = applicationId;
        EventType = eventType.Trim();
        StatusSnapshot = statusSnapshot?.Trim() ?? string.Empty;
        RecipientEmail = recipientEmail.Trim();
        Subject = subject.Trim();
        TemplateName = templateName.Trim();
    }

    public void MarkSent()
    {
        Status = "sent";
        SentAt = DateTime.UtcNow;
        ErrorMessage = null;
        Touch();
    }

    public void MarkFailed(string error)
    {
        Status = "failed";
        ErrorMessage = error.Length > 2000 ? error[..2000] : error;
        Touch();
    }
}