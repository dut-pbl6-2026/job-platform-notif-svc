using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Notif.Core.Entities;
using Notif.Core.Interfaces;
using Notif.Core.Templates;
using Notif.Infrastructure.Data;

namespace Notif.Infrastructure.Services;

/// <summary>
/// Handles one Telegram webhook update: <c>/start</c>, <c>/help</c>, <c>/subscribe</c>, <c>/unsubscribe</c>
/// (PBL6-63, SRS TELE-01-02). Other updates are ignored. Subscribe and unsubscribe are idempotent.
/// A failed reply is logged and does not undo the subscription change.
/// </summary>
public sealed class TelegramUpdateHandler
{
    private static readonly char[] WhitespaceSeparators = [' ', '\t', '\r', '\n'];

    private readonly NotifDbContext _db;
    private readonly ITelegramSender _sender;
    private readonly ILogger<TelegramUpdateHandler> _logger;

    public TelegramUpdateHandler(NotifDbContext db, ITelegramSender sender, ILogger<TelegramUpdateHandler> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task HandleAsync(string json, CancellationToken ct = default)
    {
        if (!TryReadCommand(json, out var chatId, out var command))
        {
            return;
        }

        switch (command)
        {
            case "/start":
                await ReplyAsync(chatId, TelegramMessages.Welcome, ct);
                break;

            case "/help":
                await ReplyAsync(chatId, TelegramMessages.Help, ct);
                break;

            case "/subscribe":
                await SubscribeAsync(chatId, ct);
                break;

            case "/unsubscribe":
                await UnsubscribeAsync(chatId, ct);
                break;

            default:
                await ReplyAsync(chatId, TelegramMessages.Help, ct);
                break;
        }
    }

    private async Task SubscribeAsync(long chatId, CancellationToken ct)
    {
        var existing = await _db.TelegramSubscriptions.FirstOrDefaultAsync(s => s.ChatId == chatId, ct);
        string reply;
        if (existing is null)
        {
            _db.TelegramSubscriptions.Add(new TelegramSubscription(chatId));
            reply = TelegramMessages.Subscribed;
        }
        else if (!existing.IsActive)
        {
            existing.Activate();
            reply = TelegramMessages.Subscribed;
        }
        else
        {
            reply = TelegramMessages.AlreadySubscribed;
        }

        // Save before replying. If this throws, the update fails and Telegram retries it, which is safe.
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Telegram subscribe handled.");
        await ReplyAsync(chatId, reply, ct);
    }

    private async Task UnsubscribeAsync(long chatId, CancellationToken ct)
    {
        var existing = await _db.TelegramSubscriptions.FirstOrDefaultAsync(s => s.ChatId == chatId, ct);
        string reply;
        if (existing is { IsActive: true })
        {
            existing.Deactivate();
            await _db.SaveChangesAsync(ct);
            reply = TelegramMessages.Unsubscribed;
        }
        else
        {
            reply = TelegramMessages.NotSubscribed;
        }

        _logger.LogInformation("Telegram unsubscribe handled.");
        await ReplyAsync(chatId, reply, ct);
    }

    private async Task ReplyAsync(long chatId, string text, CancellationToken ct)
    {
        try
        {
            await _sender.SendMessageAsync(chatId, text, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram reply failed. The update was still processed.");
        }
    }

    /// <summary>Extracts the chat id and normalized command. Returns false for non-command updates.</summary>
    private bool TryReadCommand(string json, out long chatId, out string command)
    {
        chatId = 0;
        command = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!message.TryGetProperty("chat", out var chat)
                || !chat.TryGetProperty("id", out var idEl)
                || idEl.ValueKind != JsonValueKind.Number
                || !idEl.TryGetInt64(out chatId)
                || chatId == 0)
            {
                return false;
            }

            if (!message.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var text = textEl.GetString();
            if (string.IsNullOrWhiteSpace(text) || text[0] != '/')
            {
                return false;
            }

            var first = text.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries)[0];
            // In groups Telegram sends "/subscribe@BotName". Drop the suffix.
            var at = first.IndexOf('@');
            command = (at > 0 ? first[..at] : first).ToLowerInvariant();
            return true;
        }
        catch (JsonException)
        {
            _logger.LogWarning("Telegram update is not valid JSON. Ignoring.");
            return false;
        }
    }
}
