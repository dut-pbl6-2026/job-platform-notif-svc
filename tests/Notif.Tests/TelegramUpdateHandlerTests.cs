using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Notif.Infrastructure.Services;
using Xunit;

namespace Notif.Tests;

/// <summary>PBL6-63: /subscribe and /unsubscribe handling (idempotent, persisted per chat id).</summary>
public class TelegramUpdateHandlerTests
{
    private static TelegramUpdateHandler Build(Infrastructure.Data.NotifDbContext db, FakeTelegramSender sender)
        => new(db, sender, NullLogger<TelegramUpdateHandler>.Instance);

    [Fact]
    public async Task Subscribe_CreatesActiveSubscriptionAndReplies()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();

        await Build(db, sender).HandleAsync(TelegramTestData.Message(1001, "/subscribe"));

        var sub = await db.TelegramSubscriptions.SingleAsync();
        Assert.Equal(1001L, sub.ChatId);
        Assert.True(sub.IsActive);
        Assert.Single(sender.Sent);
        Assert.Equal(1001L, sender.Sent[0].ChatId);
        Assert.Equal(Core.Templates.TelegramMessages.Subscribed, sender.Sent[0].Text);
    }

    [Fact]
    public async Task Subscribe_WhenAlreadySubscribed_DoesNotDuplicateRow()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();
        var handler = Build(db, sender);

        await handler.HandleAsync(TelegramTestData.Message(1001, "/subscribe"));
        await handler.HandleAsync(TelegramTestData.Message(1001, "/subscribe"));

        Assert.Single(await db.TelegramSubscriptions.ToListAsync());
        Assert.Equal(Core.Templates.TelegramMessages.AlreadySubscribed, sender.Sent[^1].Text);
    }

    [Fact]
    public async Task Unsubscribe_DeactivatesSubscriptionAndReplies()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();
        var handler = Build(db, sender);
        await handler.HandleAsync(TelegramTestData.Message(1001, "/subscribe"));

        await handler.HandleAsync(TelegramTestData.Message(1001, "/unsubscribe"));

        var sub = await db.TelegramSubscriptions.SingleAsync();
        Assert.False(sub.IsActive);
        Assert.Equal(Core.Templates.TelegramMessages.Unsubscribed, sender.Sent[^1].Text);
    }

    [Fact]
    public async Task Unsubscribe_WhenNotSubscribed_RepliesWithoutCreatingRow()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();

        await Build(db, sender).HandleAsync(TelegramTestData.Message(1001, "/unsubscribe"));

        Assert.False(await db.TelegramSubscriptions.AnyAsync());
        Assert.Equal(Core.Templates.TelegramMessages.NotSubscribed, sender.Sent.Single().Text);
    }

    [Fact]
    public async Task Resubscribe_AfterUnsubscribe_ReactivatesSameRow()
    {
        using var db = TelegramTestData.BuildDb();
        var handler = Build(db, new FakeTelegramSender());
        await handler.HandleAsync(TelegramTestData.Message(1001, "/subscribe"));
        await handler.HandleAsync(TelegramTestData.Message(1001, "/unsubscribe"));

        await handler.HandleAsync(TelegramTestData.Message(1001, "/subscribe"));

        var rows = await db.TelegramSubscriptions.ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].IsActive);
    }

    [Fact]
    public async Task Subscribe_AcceptsGroupSuffixAndNegativeChatIds()
    {
        using var db = TelegramTestData.BuildDb();

        await Build(db, new FakeTelegramSender())
            .HandleAsync(TelegramTestData.Message(-1001234567890, "/subscribe@JobPlatformBot"));

        Assert.True(await db.TelegramSubscriptions.AnyAsync(s => s.ChatId == -1001234567890 && s.IsActive));
    }

    [Fact]
    public async Task Start_RepliesWithWelcome_AndHelpRepliesWithHelp()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();
        var handler = Build(db, sender);

        await handler.HandleAsync(TelegramTestData.Message(7, "/start"));
        await handler.HandleAsync(TelegramTestData.Message(7, "/help"));

        Assert.Equal(Core.Templates.TelegramMessages.Welcome, sender.Sent[0].Text);
        Assert.Equal(Core.Templates.TelegramMessages.Help, sender.Sent[1].Text);
        Assert.False(await db.TelegramSubscriptions.AnyAsync());
    }

    [Fact]
    public async Task UnknownCommand_RepliesWithHelp()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();

        await Build(db, sender).HandleAsync(TelegramTestData.Message(7, "/list"));

        Assert.Equal(Core.Templates.TelegramMessages.Help, sender.Sent.Single().Text);
    }

    [Fact]
    public async Task PlainText_IsIgnored()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();

        await Build(db, sender).HandleAsync(TelegramTestData.Message(7, "hello there"));

        Assert.Empty(sender.Sent);
        Assert.False(await db.TelegramSubscriptions.AnyAsync());
    }

    [Fact]
    public async Task MalformedJson_IsIgnoredWithoutThrowing()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();

        await Build(db, sender).HandleAsync("{not json");

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ReplyFailure_StillPersistsSubscription()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender { FailAll = true };

        await Build(db, sender).HandleAsync(TelegramTestData.Message(1001, "/subscribe"));

        Assert.True(await db.TelegramSubscriptions.AnyAsync(s => s.ChatId == 1001 && s.IsActive));
    }
}
