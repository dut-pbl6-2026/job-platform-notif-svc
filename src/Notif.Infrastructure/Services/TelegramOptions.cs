using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Notif.Infrastructure.Services;

/// <summary>
/// Telegram bot settings (PBL6-63). All values come from configuration; nothing is committed.
/// The bot is enabled only when <c>TELEGRAM_BOT_TOKEN</c> is usable. Once it is, the webhook
/// secret and API base URL are required and a misconfiguration fails startup.
/// </summary>
public sealed record TelegramOptions(string BotToken, string WebhookSecret, string ApiBaseUrl)
{
    // Telegram accepts 1-256 characters from this set for secret_token.
    private static readonly Regex SecretPattern = new("^[A-Za-z0-9_-]{1,256}$", RegexOptions.CultureInvariant);

    public bool IsEnabled => BotToken.Length > 0;

    public static TelegramOptions Disabled { get; } = new(string.Empty, string.Empty, string.Empty);

    public static TelegramOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var token = Usable(configuration["TELEGRAM_BOT_TOKEN"]);
        if (token is null)
        {
            return Disabled;
        }

        var secret = Usable(configuration["TELEGRAM_WEBHOOK_SECRET"]);
        if (secret is null)
        {
            throw new InvalidOperationException(
                "TELEGRAM_BOT_TOKEN is set but TELEGRAM_WEBHOOK_SECRET is not. Refusing to start an unauthenticated webhook.");
        }

        if (!SecretPattern.IsMatch(secret))
        {
            throw new InvalidOperationException(
                "TELEGRAM_WEBHOOK_SECRET must be 1-256 characters of A-Z, a-z, 0-9, '_' or '-' (Telegram secret_token rule).");
        }

        var baseUrl = Usable(configuration["TELEGRAM_API_BASE_URL"]);
        if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "TELEGRAM_BOT_TOKEN is set but TELEGRAM_API_BASE_URL is missing or not an absolute URL.");
        }

        return new TelegramOptions(token, secret, baseUrl.TrimEnd('/'));
    }

    private static string? Usable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith("<set via", StringComparison.Ordinal)
            || value.Contains("${", StringComparison.Ordinal))
        {
            return null;
        }

        return value.Trim();
    }
}
