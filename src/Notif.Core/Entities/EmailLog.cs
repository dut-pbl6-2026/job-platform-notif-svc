using SharedKernel;

namespace Notif.Core.Entities;

/// <summary>
/// Audit log for every outbound email (SRS NOTIF-01-06).
/// EventId is unique for idempotent Kafka handling: redelivered
/// application.submitted with the same EventId sends only once.
/// </summary>
public class EmailLog : Entity
{
    public Guid EventId { get; private set; }
    public string RecipientEmail { get; private set; } = "";
    public string Subject { get; private set; } = "";
    public string TemplateName { get; private set; } = "";
    public string Status { get; private set; } = "sent";
    public DateTime? SentAt { get; private set; }
    public string? ErrorMessage { get; private set; }

    private EmailLog()
    {
    }

    public EmailLog(Guid eventId, string recipientEmail, string subject, string templateName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEmail, nameof(recipientEmail));
        ArgumentException.ThrowIfNullOrWhiteSpace(subject, nameof(subject));
        EventId = eventId == Guid.Empty ? Guid.NewGuid() : eventId;
        RecipientEmail = recipientEmail.Trim();
        Subject = subject.Trim();
        TemplateName = templateName.Trim();
        Status = "pending";
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
