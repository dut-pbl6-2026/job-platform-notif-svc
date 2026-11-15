using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Notif.Api.Endpoints;
using Notif.Infrastructure.Services;
using Xunit;

namespace Notif.Tests;

/// <summary>PBL6-63: webhook secret validation (fail closed, constant-time compare).</summary>
public class TelegramWebhookEndpointTests
{
    private const string Secret = "whsec_test-Value_123";

    [Fact]
    public void SecretMatches_WhenExpectedIsNull_ReturnsFalse()
        => Assert.False(TelegramWebhookEndpoint.SecretMatches(Secret, null));

    [Fact]
    public void SecretMatches_WhenExpectedIsEmpty_ReturnsFalseEvenForEmptyProvided()
    {
        Assert.False(TelegramWebhookEndpoint.SecretMatches("", ""));
        Assert.False(TelegramWebhookEndpoint.SecretMatches(null, ""));
    }

    [Fact]
    public void SecretMatches_WhenProvidedIsMissing_ReturnsFalse()
        => Assert.False(TelegramWebhookEndpoint.SecretMatches(null, Secret));

    [Fact]
    public void SecretMatches_WhenValueDiffers_ReturnsFalse()
    {
        Assert.False(TelegramWebhookEndpoint.SecretMatches("whsec_test-Value_124", Secret));
        Assert.False(TelegramWebhookEndpoint.SecretMatches("whsec_test-Value_12", Secret));
        Assert.False(TelegramWebhookEndpoint.SecretMatches(Secret + "x", Secret));
    }

    [Fact]
    public void SecretMatches_WhenValueEqual_ReturnsTrue()
        => Assert.True(TelegramWebhookEndpoint.SecretMatches(Secret, Secret));

    [Fact]
    public async Task HandleAsync_WhenSecretWrong_Returns401AndDoesNotProcess()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();
        var handler = new TelegramUpdateHandler(db, sender, NullLogger<TelegramUpdateHandler>.Instance);

        var result = await TelegramWebhookEndpoint.HandleAsync(
            "wrong", Secret, TelegramTestData.Message(42, "/subscribe"), handler);

        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Empty(sender.Sent);
        Assert.False(await db.TelegramSubscriptions.AnyAsync());
    }

    [Fact]
    public async Task HandleAsync_WhenSecretHeaderMissing_Returns401()
    {
        using var db = TelegramTestData.BuildDb();
        var handler = new TelegramUpdateHandler(db, new FakeTelegramSender(), NullLogger<TelegramUpdateHandler>.Instance);

        var result = await TelegramWebhookEndpoint.HandleAsync(
            null, Secret, TelegramTestData.Message(42, "/subscribe"), handler);

        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.False(await db.TelegramSubscriptions.AnyAsync());
    }

    [Fact]
    public async Task HandleAsync_WhenSecretNotConfigured_Returns401EvenWithAnyHeader()
    {
        using var db = TelegramTestData.BuildDb();
        var handler = new TelegramUpdateHandler(db, new FakeTelegramSender(), NullLogger<TelegramUpdateHandler>.Instance);

        var result = await TelegramWebhookEndpoint.HandleAsync(
            "anything", null, TelegramTestData.Message(42, "/subscribe"), handler);

        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.False(await db.TelegramSubscriptions.AnyAsync());
    }

    [Fact]
    public async Task HandleAsync_WhenSecretMatches_Returns200AndProcessesUpdate()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();
        var handler = new TelegramUpdateHandler(db, sender, NullLogger<TelegramUpdateHandler>.Instance);

        var result = await TelegramWebhookEndpoint.HandleAsync(
            Secret, Secret, TelegramTestData.Message(42, "/subscribe"), handler);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.True(await db.TelegramSubscriptions.AnyAsync(s => s.ChatId == 42 && s.IsActive));
    }

    [Fact]
    public async Task HandleAsync_WhenSecretMatchesButUpdateIsNotMessage_Returns200WithoutSideEffects()
    {
        using var db = TelegramTestData.BuildDb();
        var sender = new FakeTelegramSender();
        var handler = new TelegramUpdateHandler(db, sender, NullLogger<TelegramUpdateHandler>.Instance);

        var result = await TelegramWebhookEndpoint.HandleAsync(
            Secret, Secret, "{\"update_id\":2,\"my_chat_member\":{}}", handler);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Empty(sender.Sent);
    }
}
