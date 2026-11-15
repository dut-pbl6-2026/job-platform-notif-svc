using Notif.Api.Security;
using Notif.Infrastructure.Services;

namespace Notif.Api.Endpoints;

/// <summary>
/// Telegram webhook (PBL6-63, SRS TELE-01-05). Telegram sends the secret set via setWebhook
/// in the <c>X-Telegram-Bot-Api-Secret-Token</c> header. The check fails closed: no configured
/// secret, or a missing or wrong header, returns 401 and the update is not processed.
/// Once the secret matches, every update gets 200, so Telegram does not retry ignored updates.
/// Kept as a static handler so it can be unit-tested.
/// </summary>
public static class TelegramWebhookEndpoint
{
    public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    public static async Task<IResult> HandleAsync(
        string? providedSecret,
        string? expectedSecret,
        string body,
        TelegramUpdateHandler handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (!SecretComparison.Matches(providedSecret, expectedSecret))
        {
            return Results.Unauthorized();
        }

        await handler.HandleAsync(body, ct);
        return Results.Ok();
    }
}
