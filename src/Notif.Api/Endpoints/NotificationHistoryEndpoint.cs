using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Notif.Api.Security;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Configuration;
using Notif.Infrastructure.Data;

namespace Notif.Api.Endpoints;

/// <summary>
/// Paginated notification delivery history (SRS NOTIF-01-06). Rows contain recipient PII, so access
/// is scoped to the caller identified by the gateway (<c>X-User-Id</c>/<c>X-User-Role</c> forwarded
/// from the validated JWT, see <see cref="GatewayIdentityMiddleware"/>):
/// <list type="bullet">
/// <item>An authenticated non-admin sees only the entries addressed to their own e-mail.</item>
/// <item>An <c>Admin</c> sees the whole log.</item>
/// <item><c>NOTIF_HISTORY_TOKEN</c> (<c>X-Internal-Token</c>) is a machine-caller credential, never
/// an identity: it is honoured only together with the <c>Admin</c> role. A valid token without
/// <c>Admin</c> is refused (403); no identity and no token is 401.</item>
/// </list>
/// The token is still mandatory outside Development (startup fails without it), and a template
/// placeholder counts as missing.
/// Kept as a static handler (not an inline lambda) so it is unit-testable.
/// </summary>
public static class NotificationHistoryEndpoint
{
    public const string TokenHeader = "X-Internal-Token";

    public const string TokenSetting = "NOTIF_HISTORY_TOKEN";

    private const string AdminRole = "Admin";

    /// <summary>Fails startup when the history token is missing (or only a template placeholder) outside Development.</summary>
    public static void ValidateTokenPolicy(string? expectedToken, bool isDevelopment)
    {
        if (SecretValue.Usable(expectedToken) is null && !isDevelopment)
        {
            throw new InvalidOperationException(
                $"{TokenSetting} is required outside Development: notification history contains recipient PII. "
                + "Refusing to start.");
        }
    }

    /// <summary>
    /// Token check. A configured token must match (constant-time); an unset token or a template
    /// placeholder never matches. The token is not an identity and never grants access by itself.
    /// </summary>
    public static bool HasValidToken(string? providedToken, string? expectedToken) =>
        SecretComparison.Matches(providedToken, SecretValue.Usable(expectedToken));

    /// <summary>Caller id forwarded by the gateway (<c>ClaimTypes.NameIdentifier</c> or <c>sub</c>).</summary>
    public static string? CallerId(ClaimsPrincipal? user) =>
        user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? user?.FindFirst("sub")?.Value;

    /// <summary>True when the forwarded role claim is <c>Admin</c>.</summary>
    public static bool IsAdmin(ClaimsPrincipal? user) =>
        string.Equals(
            user?.FindFirst(ClaimTypes.Role)?.Value ?? user?.FindFirst("role")?.Value,
            AdminRole,
            StringComparison.OrdinalIgnoreCase);

    public static async Task<IResult> HandleAsync(
        ClaimsPrincipal? user,
        string? providedToken,
        string? expectedToken,
        int page,
        int size,
        NotifDbContext db,
        IUserEmailLookup emailLookup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(emailLookup);

        var tokenValid = HasValidToken(providedToken, expectedToken);
        var tokenPresented = !string.IsNullOrWhiteSpace(providedToken);

        // A wrong token is always rejected; a valid one is only ever a second factor for a machine
        // caller, which must also carry the Admin identity. It never substitutes for an identity.
        if (tokenPresented && !tokenValid)
        {
            return Results.Unauthorized();
        }

        var isAdmin = IsAdmin(user);
        if (tokenValid && !isAdmin)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var callerId = CallerId(user);
        if (!isAdmin && callerId is null)
        {
            return Results.Unauthorized();
        }

        page = Math.Max(1, page <= 0 ? 1 : page);
        size = Math.Clamp(size <= 0 ? 20 : size, 1, 100);

        var query = db.NotificationLogs.AsNoTracking();

        if (!isAdmin)
        {
            if (!Guid.TryParse(callerId, out var userId))
            {
                return Results.Unauthorized();
            }

            string? callerEmail;
            try
            {
                callerEmail = await emailLookup.FindEmailAsync(userId, ct);
            }
            catch (UserEmailLookupUnavailableException)
            {
                // Fail closed rather than falling back to the whole log when auth-svc cannot answer.
                return Results.Problem(
                    "Notification history is temporarily unavailable: the caller's address could not be resolved.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(callerEmail))
            {
                return Results.Ok(new { items = Array.Empty<object>(), total = 0, page, size });
            }

            var scopedEmail = callerEmail.Trim().ToLowerInvariant();
            query = query.Where(e => e.RecipientEmail.ToLower() == scopedEmail);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(e => new { e.Id, e.RecipientEmail, e.Subject, e.TemplateName, e.Status, e.SentAt, e.CreatedAt })
            .ToListAsync(ct);
        return Results.Ok(new { items, total, page, size });
    }
}
