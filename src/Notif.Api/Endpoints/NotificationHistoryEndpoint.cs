using Microsoft.EntityFrameworkCore;
using Notif.Api.Security;
using Notif.Infrastructure.Data;

namespace Notif.Api.Endpoints;

/// <summary>
/// Paginated notification delivery history (SRS NOTIF-01-06).
/// Rows contain recipient PII, so the endpoint requires the internal token
/// (<c>X-Internal-Token</c> == <c>NOTIF_HISTORY_TOKEN</c>). The token is mandatory outside
/// Development (startup fails without it). Anonymous access exists only as an explicit Development
/// opt-in, passed in as <c>allowWithoutToken</c>.
/// Kept as a static handler (not an inline lambda) so it is unit-testable.
/// </summary>
public static class NotificationHistoryEndpoint
{
    public const string TokenHeader = "X-Internal-Token";

    public const string TokenSetting = "NOTIF_HISTORY_TOKEN";

    /// <summary>Fails startup when the history token is missing outside Development.</summary>
    public static void ValidateTokenPolicy(string? expectedToken, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(expectedToken) && !isDevelopment)
        {
            throw new InvalidOperationException(
                $"{TokenSetting} is required outside Development: notification history contains recipient PII. "
                + "Refusing to start.");
        }
    }

    /// <summary>
    /// Token gate. A configured token must match (constant-time). With no token configured the
    /// request is allowed only when <paramref name="allowWithoutToken"/> is set (Development opt-in).
    /// </summary>
    public static bool IsAuthorized(string? providedToken, string? expectedToken, bool allowWithoutToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            return allowWithoutToken;
        }

        return SecretComparison.Matches(providedToken, expectedToken);
    }

    public static async Task<IResult> HandleAsync(
        string? providedToken,
        string? expectedToken,
        bool allowWithoutToken,
        int page,
        int size,
        NotifDbContext db,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!IsAuthorized(providedToken, expectedToken, allowWithoutToken))
        {
            return Results.Unauthorized();
        }

        page = Math.Max(1, page <= 0 ? 1 : page);
        size = Math.Clamp(size <= 0 ? 20 : size, 1, 100);
        var total = await db.NotificationLogs.CountAsync(ct);
        var items = await db.NotificationLogs
            .AsNoTracking()
            .OrderByDescending(e => e.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(e => new { e.Id, e.RecipientEmail, e.Subject, e.TemplateName, e.Status, e.SentAt, e.CreatedAt })
            .ToListAsync(ct);
        return Results.Ok(new { items, total, page, size });
    }
}
