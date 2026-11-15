using Microsoft.Extensions.Configuration;
using Notif.Infrastructure.Services;
using Xunit;

namespace Notif.Tests;

/// <summary>Unit: AUTH_SERVICE_URL / INTERNAL_SERVICE_TOKEN startup policy.</summary>
public class AuthServiceOptionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
    {
        var dict = values.ToDictionary(v => v.Key, v => v.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Theory]
    [InlineData(null, "token")]
    [InlineData("http://auth-svc:5001", null)]
    [InlineData("", "token")]
    [InlineData("http://auth-svc:5001", "  ")]
    [InlineData("<set via infisical>", "token")]
    [InlineData("http://auth-svc:5001", "${INTERNAL_SERVICE_TOKEN}")]
    public void Load_OutsideDevelopment_WhenEitherValueMissing_Throws(string? url, string? token)
    {
        var config = Config(("AUTH_SERVICE_URL", url), ("INTERNAL_SERVICE_TOKEN", token));

        Assert.Throws<InvalidOperationException>(() => AuthServiceOptions.Load(config, isDevelopment: false));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("http://auth-svc:5001", null)]
    [InlineData(null, "token")]
    public void Load_InDevelopment_WhenMissing_ReturnsUnconfigured(string? url, string? token)
    {
        var options = AuthServiceOptions.Load(
            Config(("AUTH_SERVICE_URL", url), ("INTERNAL_SERVICE_TOKEN", token)), isDevelopment: true);

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void Load_WithBothValues_TrimsTrailingSlash()
    {
        var options = AuthServiceOptions.Load(
            Config(("AUTH_SERVICE_URL", "http://auth-svc:5001/"), ("INTERNAL_SERVICE_TOKEN", " token ")),
            isDevelopment: false);

        Assert.True(options.IsConfigured);
        Assert.Equal("http://auth-svc:5001", options.BaseUrl);
        Assert.Equal("token", options.InternalToken);
    }

    [Theory]
    [InlineData("auth-svc:5001")]
    [InlineData("ftp://auth-svc")]
    [InlineData("/relative/path")]
    public void Load_WithNonHttpUrl_ThrowsEvenInDevelopment(string url)
    {
        var config = Config(("AUTH_SERVICE_URL", url), ("INTERNAL_SERVICE_TOKEN", "token"));

        Assert.Throws<InvalidOperationException>(() => AuthServiceOptions.Load(config, isDevelopment: true));
    }
}
