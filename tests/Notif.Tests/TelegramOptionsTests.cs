using Microsoft.Extensions.Configuration;
using Notif.Infrastructure.Services;
using Xunit;

namespace Notif.Tests;

/// <summary>PBL6-63: Telegram config loading (off by default, fail fast on partial config).</summary>
public class TelegramOptionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
    {
        var dict = values.ToDictionary(v => v.Key, v => v.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void Load_WithoutToken_IsDisabledAndDoesNotRequireOtherKeys()
    {
        var options = TelegramOptions.Load(Config());

        Assert.False(options.IsEnabled);
    }

    [Fact]
    public void Load_WithPlaceholderToken_IsDisabled()
    {
        var options = TelegramOptions.Load(Config(("TELEGRAM_BOT_TOKEN", "${TELEGRAM_BOT_TOKEN}")));

        Assert.False(options.IsEnabled);
    }

    [Fact]
    public void Load_WithEnvTemplatePlaceholderToken_IsDisabled()
    {
        var options = TelegramOptions.Load(Config(("TELEGRAM_BOT_TOKEN", "__SET_VIA_INFISICAL__")));

        Assert.False(options.IsEnabled);
    }

    [Fact]
    public void Load_WithTokenButNoSecret_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => TelegramOptions.Load(Config(
            ("TELEGRAM_BOT_TOKEN", "123:abc"),
            ("TELEGRAM_API_BASE_URL", "https://api.telegram.org"))));
    }

    [Fact]
    public void Load_WithSecretContainingInvalidCharacters_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => TelegramOptions.Load(Config(
            ("TELEGRAM_BOT_TOKEN", "123:abc"),
            ("TELEGRAM_WEBHOOK_SECRET", "has spaces!"),
            ("TELEGRAM_API_BASE_URL", "https://api.telegram.org"))));
    }

    [Fact]
    public void Load_WithTokenButNoBaseUrl_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => TelegramOptions.Load(Config(
            ("TELEGRAM_BOT_TOKEN", "123:abc"),
            ("TELEGRAM_WEBHOOK_SECRET", "secret_value"))));
    }

    [Fact]
    public void Load_WithFullConfig_IsEnabledAndTrimsBaseUrl()
    {
        var options = TelegramOptions.Load(Config(
            ("TELEGRAM_BOT_TOKEN", "123:abc"),
            ("TELEGRAM_WEBHOOK_SECRET", "secret_value"),
            ("TELEGRAM_API_BASE_URL", "https://api.telegram.org/")));

        Assert.True(options.IsEnabled);
        Assert.Equal("https://api.telegram.org", options.ApiBaseUrl);
        Assert.Equal("secret_value", options.WebhookSecret);
    }
}
