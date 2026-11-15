using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Data;

namespace Notif.Infrastructure.Services;

public sealed record TelegramBroadcastResult(int Sent, int Failed);

/// <summary>
/// Sends one message to every active Telegram subscription (PBL6-63). A failure for one chat is
/// counted and logged, and the loop continues. Cancellation stops the loop. No retries, no parse_mode.
/// </summary>
public sealed class TelegramBroadcaster
{
    private readonly NotifDbContext _db;
    private readonly ITelegramSender _sender;
    private readonly ILogger<TelegramBroadcaster> _logger;

    public TelegramBroadcaster(NotifDbContext db, ITelegramSender sender, ILogger<TelegramBroadcaster> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TelegramBroadcastResult> BroadcastAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var chatIds = await _db.TelegramSubscriptions
            .AsNoTracking()
            .Where(s => s.IsActive)
            .Select(s => s.ChatId)
            .ToListAsync(ct);

        var sent = 0;
        var failed = 0;
        foreach (var chatId in chatIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _sender.SendMessageAsync(chatId, text, ct);
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Telegram broadcast to one chat failed. Continuing with the rest.");
            }
        }

        _logger.LogInformation("Telegram broadcast finished. Sent={Sent} Failed={Failed}.", sent, failed);
        return new TelegramBroadcastResult(sent, failed);
    }
}
