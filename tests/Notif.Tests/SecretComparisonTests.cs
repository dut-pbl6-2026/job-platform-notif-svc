using Notif.Api.Security;
using Xunit;

namespace Notif.Tests;

/// <summary>Unit: the shared constant-time secret check used by the webhook and the history token.</summary>
public class SecretComparisonTests
{
    private const string Secret = "whsec_test-Value_123";

    [Fact]
    public void Matches_WhenExpectedIsNull_ReturnsFalse()
        => Assert.False(SecretComparison.Matches(Secret, null));

    [Fact]
    public void Matches_WhenExpectedIsEmpty_ReturnsFalseEvenForEmptyProvided()
    {
        Assert.False(SecretComparison.Matches("", ""));
        Assert.False(SecretComparison.Matches(null, ""));
    }

    [Fact]
    public void Matches_WhenProvidedIsMissing_ReturnsFalse()
        => Assert.False(SecretComparison.Matches(null, Secret));

    [Fact]
    public void Matches_WhenValueDiffers_ReturnsFalse()
    {
        Assert.False(SecretComparison.Matches("whsec_test-Value_124", Secret));
        Assert.False(SecretComparison.Matches("whsec_test-Value_12", Secret));
        Assert.False(SecretComparison.Matches(Secret + "x", Secret));
        Assert.False(SecretComparison.Matches(Secret.ToUpperInvariant(), Secret));
    }

    [Fact]
    public void Matches_WhenValueEqual_ReturnsTrue()
        => Assert.True(SecretComparison.Matches(Secret, Secret));

    [Fact]
    public void Matches_ComparesUtf8Bytes_SoNonAsciiSecretsWork()
    {
        const string unicode = "bí-mật-ünïcode";
        Assert.True(SecretComparison.Matches(unicode, unicode));
        Assert.False(SecretComparison.Matches("bi-mat-unicode", unicode));
    }
}
