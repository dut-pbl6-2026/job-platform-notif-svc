using System.Security.Cryptography;
using System.Text;

namespace Notif.Api.Security;

/// <summary>
/// Shared secret check for the notification API (Telegram webhook secret, internal history token).
/// Constant-time comparison. A missing or empty expected secret never matches, so the caller fails closed.
/// </summary>
public static class SecretComparison
{
    public static bool Matches(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }
}
