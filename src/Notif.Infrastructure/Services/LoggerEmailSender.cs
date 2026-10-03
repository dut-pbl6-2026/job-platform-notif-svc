using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Notif.Core.Interfaces;

namespace Notif.Infrastructure.Services;

/// <summary>Fallback sender when SMTP is not configured: logs instead of sending.</summary>
public class LoggerEmailSender : INotificationService
{
    private readonly ILogger<LoggerEmailSender> _logger;

    public LoggerEmailSender(ILogger<LoggerEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        _logger.LogWarning("SMTP not configured. Email to {Recipient} subject {Subject} logged only.", to, subject);
        return Task.CompletedTask;
    }
}
