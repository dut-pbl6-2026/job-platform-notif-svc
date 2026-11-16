using Microsoft.Extensions.Configuration;
using Notif.Infrastructure.Configuration;

namespace Notif.Infrastructure.Services;

/// <summary>
/// auth-svc internal API settings (<c>AUTH_SERVICE_URL</c>, <c>AUTH_INTERNAL_TOKEN_FOR_NOTIF</c>).
/// Values come only from configuration; the token has no default and is never logged.
/// Outside Development both are required and a missing value fails startup.
/// </summary>
public sealed record AuthServiceOptions(string BaseUrl, string InternalToken)
{
    public bool IsConfigured => BaseUrl.Length > 0 && InternalToken.Length > 0;

    public static AuthServiceOptions Load(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var baseUrl = Usable(configuration["AUTH_SERVICE_URL"]);
        var token = Usable(configuration["AUTH_INTERNAL_TOKEN_FOR_NOTIF"]);

        if (baseUrl is null || token is null)
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException(
                    "AUTH_SERVICE_URL and AUTH_INTERNAL_TOKEN_FOR_NOTIF are required outside Development. "
                    + "Recipient lookup cannot run without them.");
            }

            return new AuthServiceOptions(string.Empty, string.Empty);
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("AUTH_SERVICE_URL must be an absolute http or https URL.");
        }

        return new AuthServiceOptions(baseUrl.TrimEnd('/'), token);
    }

    private static string? Usable(string? value) => SecretValue.Usable(value);
}
