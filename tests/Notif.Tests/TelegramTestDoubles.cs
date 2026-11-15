using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Services;

namespace Notif.Tests;

/// <summary>Records sends; can be told to fail for specific chats (no network).</summary>
internal sealed class FakeTelegramSender : ITelegramSender
{
    public List<(long ChatId, string Text)> Sent { get; } = new();

    public HashSet<long> FailFor { get; } = new();

    public bool FailAll { get; set; }

    public Task SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        if (FailAll || FailFor.Contains(chatId))
        {
            throw new TelegramApiException(403, "Forbidden: bot was blocked by the user");
        }

        Sent.Add((chatId, text));
        return Task.CompletedTask;
    }
}

internal static class TelegramTestData
{
    public static NotifDbContext BuildDb()
    {
        var options = new DbContextOptionsBuilder<NotifDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new NotifDbContext(options);
    }

    /// <summary>A minimal Telegram update carrying a text message from the given chat.</summary>
    public static string Message(long chatId, string text) => JsonSerializer.Serialize(new
    {
        update_id = 1,
        message = new { message_id = 1, chat = new { id = chatId, type = "private" }, text },
    });
}
