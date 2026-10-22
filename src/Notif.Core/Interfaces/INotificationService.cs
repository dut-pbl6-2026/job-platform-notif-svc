namespace Notif.Core.Interfaces;

/// <summary>Sends a rendered notification email through the configured transport.</summary>
public interface INotificationService
{
    Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default);
}