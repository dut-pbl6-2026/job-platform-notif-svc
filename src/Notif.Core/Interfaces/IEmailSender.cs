namespace Notif.Core.Interfaces;

/// <summary>Sends a templated email (SMTP in prod, MailHog locally).</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default);
}
