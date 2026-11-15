namespace Notif.Core.Interfaces;

/// <summary>Resolves a platform user id to the email address the user registered with (auth-svc).</summary>
public interface IUserEmailLookup
{
    /// <returns>The user's email, or <c>null</c> when auth-svc does not know the user.</returns>
    /// <exception cref="UserEmailLookupUnavailableException">
    /// auth-svc could not give an answer (unreachable, rejected the token, or retries exhausted).
    /// Callers must not fall back to any other address.
    /// </exception>
    Task<string?> FindEmailAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Thrown when the email lookup cannot be completed; the message is safe to log.</summary>
public sealed class UserEmailLookupUnavailableException : Exception
{
    public UserEmailLookupUnavailableException(string message)
        : base(message)
    {
    }

    public UserEmailLookupUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
