using Notif.Api.Endpoints;
using Xunit;

namespace Notif.Tests;

/// <summary>Unit: NOTIF_HISTORY_TOKEN policy. Fail closed outside Development; open only as a Development opt-in.</summary>
public class NotificationHistoryPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateTokenPolicy_WhenTokenMissingOutsideDevelopment_Throws(string? token)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => NotificationHistoryEndpoint.ValidateTokenPolicy(token, isDevelopment: false));

        Assert.Contains("NOTIF_HISTORY_TOKEN", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateTokenPolicy_WhenTokenMissingInDevelopment_DoesNotThrow(string? token)
        => NotificationHistoryEndpoint.ValidateTokenPolicy(token, isDevelopment: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateTokenPolicy_WhenTokenConfigured_DoesNotThrow(bool isDevelopment)
        => NotificationHistoryEndpoint.ValidateTokenPolicy("internal-token-value", isDevelopment);

    [Fact]
    public void IsAuthorized_WhenTokenConfigured_RequiresMatch_EvenIfDevelopmentOptIn()
    {
        Assert.True(NotificationHistoryEndpoint.IsAuthorized("t0ken", "t0ken", allowWithoutToken: true));
        Assert.True(NotificationHistoryEndpoint.IsAuthorized("t0ken", "t0ken", allowWithoutToken: false));
        Assert.False(NotificationHistoryEndpoint.IsAuthorized(null, "t0ken", allowWithoutToken: true));
        Assert.False(NotificationHistoryEndpoint.IsAuthorized("wrong", "t0ken", allowWithoutToken: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void IsAuthorized_WhenTokenNotConfigured_FollowsDevelopmentOptIn(string? expected)
    {
        Assert.True(NotificationHistoryEndpoint.IsAuthorized(null, expected, allowWithoutToken: true));
        Assert.False(NotificationHistoryEndpoint.IsAuthorized(null, expected, allowWithoutToken: false));
        Assert.False(NotificationHistoryEndpoint.IsAuthorized("anything", expected, allowWithoutToken: false));
    }
}
