using SharedKernel;

namespace Notif.Core.Entities;

/// <summary>
/// A Telegram chat subscribed to job alerts (PBL6-63, SRS TELE-01-04).
/// Unsubscribing deactivates the row instead of deleting it, so a re-subscribe reuses it.
/// </summary>
public sealed class TelegramSubscription : Entity
{
    /// <summary>Telegram chat id. Group and supergroup ids are negative and exceed int32, hence long.</summary>
    public long ChatId { get; private set; }

    public bool IsActive { get; private set; }

    private TelegramSubscription() { }

    public TelegramSubscription(long chatId)
    {
        if (chatId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chatId), "Telegram chat id must not be zero.");
        }

        ChatId = chatId;
        IsActive = true;
    }

    public void Activate()
    {
        IsActive = true;
        Touch();
    }

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }
}
