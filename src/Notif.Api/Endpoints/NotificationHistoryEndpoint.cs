using Microsoft.EntityFrameworkCore;
using Notif.Infrastructure.Data;

namespace Notif.Api.Endpoints;

/// <summary>
/// Paginated notification delivery history (SRS NOTIF-01-06).
/// Rows contain recipient PII, so the endpoint requires an internal token
/// (<c>X-Internal-Token</c> == <c>NOTIF_HISTORY_TOKEN</c>) when configured —
/// same pattern as search-svc index endpoints (<c>SEARCH_INDEX_TOKEN</c>).
/// Kept as a static handler (not an inline lambda) so it is unit-testable.
/// </summary>
public static class NotificationHistoryEndpoint
{
    public const string TokenHeader = "X-Internal-Token";

    public static async Task<IResult> HandleAsync(
        string? providedToken,
        string? expectedToken,
        int page,
        int size,
        NotifDbContext db,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!string.IsNullOrWhiteSpace(expectedToken) && !TokenEquals(providedToken, expectedToken))
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

    private static bool TokenEquals(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided) || provided.Length != expected.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            diff |= provided[i] ^ expected[i];
        }

        return diff == 0;
    }
}
