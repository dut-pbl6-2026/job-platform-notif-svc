namespace Notif.Core.Interfaces;

/// <summary>Sends a plain-text message to a Telegram chat through the Bot API.</summary>
public interface ITelegramSender
{
    Task SendMessageAsync(long chatId, string text, CancellationToken ct = default);
}
