using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Notif.Core.Entities;
using Notif.Infrastructure.Services;
using Xunit;

namespace Notif.Tests;

/// <summary>PBL6-63: broadcast fan-out to active subscriptions with per-chat failure isolation.</summary>
public class TelegramBroadcasterTests
{
    private static TelegramBroadcaster Build(Infrastructure.Data.NotifDbContext db, FakeTelegramSender sender)
        => new(db, sender, NullLogger<TelegramBroadcaster>.Instance);

    [Fact]
    public async Task Broadcast_SendsToActiveSubscriptionsOnly()
    {
        using var db = TelegramTestData.BuildDb();
        db.TelegramSubscriptions.AddRange(
            new TelegramSubscription(10),
            new TelegramSubscription(20),
            new TelegramSubscription(-30));
        var inactive = new TelegramSubscription(40);
        inactive.Deactivate();
        db.TelegramSubscriptions.Add(inactive);
        await db.SaveChangesAsync();
        var sender = new FakeTelegramSender();

        var result = await Build(db, sender).BroadcastAsync("New job: Backend Engineer");

        Assert.Equal(new TelegramBroadcastResult(3, 0), result);
        Assert.Equal(new long[] { -30, 10, 20 }, sender.Sent.Select(s => s.ChatId).Order().ToArray());
        Assert.All(sender.Sent, s => Assert.Equal("New job: Backend Engineer", s.Text));
        Assert.DoesNotContain(sender.Sent, s => s.ChatId == 40);
    }

    [Fact]
    public async Task Broadcast_WhenOneChatFails_ContinuesAndCountsFailure()
    {
        using var db = TelegramTestData.BuildDb();
        db.TelegramSubscriptions.AddRange(
            new TelegramSubscription(10),
            new TelegramSubscription(20),
            new TelegramSubscription(30));
        await db.SaveChangesAsync();
        var sender = new FakeTelegramSender();
        sender.FailFor.Add(20);

        var result = await Build(db, sender).BroadcastAsync("hello");

        Assert.Equal(new TelegramBroadcastResult(2, 1), result);
        Assert.Contains(sender.Sent, s => s.ChatId == 10);
        Assert.Contains(sender.Sent, s => s.ChatId == 30);
        Assert.DoesNotContain(sender.Sent, s => s.ChatId == 20);
    }

    [Fact]
    public async Task Broadcast_WithNoSubscriptions_SendsNothing()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();

        var result = await Build(db, sender).BroadcastAsync("hello");

        Assert.Equal(new TelegramBroadcastResult(0, 0), result);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Broadcast_WithBlankText_Throws()
    {
        using var db = TelegramTestData.BuildDb();

        await Assert.ThrowsAsync<ArgumentException>(() => Build(db, new FakeTelegramSender()).BroadcastAsync("  "));
    }
}
