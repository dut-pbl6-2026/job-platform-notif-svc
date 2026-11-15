namespace Notif.Infrastructure.Services;

/// <summary>
/// A Telegram Bot API call failed: a non-2xx response, <c>ok: false</c>, or a transport error (StatusCode 0).
/// The message never contains the request URL, because that URL carries the bot token.
/// </summary>
public sealed class TelegramApiException : Exception
{
    public TelegramApiException(int statusCode, string description)
        : base($"Telegram API call failed (status {statusCode}): {description}")
    {
        StatusCode = statusCode;
        Description = description;
    }

    public int StatusCode { get; }

    public string Description { get; }
}
