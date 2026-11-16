namespace Notif.Core.Interfaces;

/// <summary>One file attached to an outgoing message. Content is already encoded bytes.</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>Sends a rendered notification email through the configured transport.</summary>
public interface INotificationService
{
    Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default);

    /// <summary>
    /// Attachment-aware send. Transports that cannot attach fall back to the plain body, so an internal
    /// caller that sends a calendar file still delivers the invitation text.
    /// </summary>
    Task SendAsync(
        string to,
        string subject,
        string htmlBody,
        string textBody,
        EmailAttachment? attachment,
        CancellationToken ct = default)
        => SendAsync(to, subject, htmlBody, textBody, ct);
}
