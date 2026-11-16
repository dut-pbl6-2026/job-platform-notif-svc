using Notif.Api.Endpoints;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// Unit: NOTIF_HISTORY_TOKEN policy. Fail closed outside Development; a template placeholder counts
/// as missing, and the token is never accepted as an identity on its own.
/// </summary>
public class NotificationHistoryPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("__SET_VIA_INFISICAL__")]
    public void ValidateTokenPolicy_WhenTokenMissingOrPlaceholderOutsideDevelopment_Throws(string? token)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => NotificationHistoryEndpoint.ValidateTokenPolicy(token, isDevelopment: false));

        Assert.Contains("NOTIF_HISTORY_TOKEN", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("__SET_VIA_INFISICAL__")]
    public void ValidateTokenPolicy_WhenTokenMissingOrPlaceholderInDevelopment_DoesNotThrow(string? token)
        => NotificationHistoryEndpoint.ValidateTokenPolicy(token, isDevelopment: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateTokenPolicy_WhenTokenConfigured_DoesNotThrow(bool isDevelopment)
        => NotificationHistoryEndpoint.ValidateTokenPolicy("internal-token-value", isDevelopment);

    [Fact]
    public void HasValidToken_RequiresTheConfiguredValue()
    {
        Assert.True(NotificationHistoryEndpoint.HasValidToken("t0ken", "t0ken"));
        Assert.False(NotificationHistoryEndpoint.HasValidToken(null, "t0ken"));
        Assert.False(NotificationHistoryEndpoint.HasValidToken("wrong", "t0ken"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("__SET_VIA_INFISICAL__")]
    public void HasValidToken_WhenExpectedIsUnsetOrPlaceholder_NeverMatches(string? expected)
    {
        Assert.False(NotificationHistoryEndpoint.HasValidToken(null, expected));
        Assert.False(NotificationHistoryEndpoint.HasValidToken("anything", expected));
        Assert.False(NotificationHistoryEndpoint.HasValidToken(expected, expected));
    }
}
