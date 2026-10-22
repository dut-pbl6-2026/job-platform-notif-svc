using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using Notif.Core.Interfaces;

namespace Notif.Infrastructure.Services;

/// <summary>SMTP email sender (MailHog locally, STARTTLS/prod otherwise).</summary>
public class SmtpEmailSender : INotificationService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        var host = _config["SMTP_HOST"] ?? "";
        var portStr = _config["SMTP_PORT"] ?? "587";
        var user = _config["SMTP_USER"];
        var pass = _config["SMTP_PASS"];
        var from = _config["SMTP_FROM"] ?? "noreply@job-platform.local";
        if (!int.TryParse(portStr, out var port))
        {
            port = 587;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("SMTP not configured. Set SMTP_HOST.");
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to.Trim()));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody();

        using var client = new SmtpClient();
        var secure = SecureSocketOptions.StartTls;
        if (host is "localhost" or "127.0.0.1" or "mailhog")
        {
            secure = SecureSocketOptions.None;
        }

        await client.ConnectAsync(host, port, secure, ct);
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
        {
            await client.AuthenticateAsync(user, pass, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
        _logger.LogInformation("Email sent to {Recipient} subject {Subject}.", to, subject);
    }
}
