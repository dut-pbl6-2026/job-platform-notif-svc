using System.Security.Claims;

namespace Notif.Api.Security;

/// <summary>
/// Builds <see cref="HttpContext.User"/> from the identity headers the gateway adds after it has
/// validated the JWT. The gateway removes any client-supplied <c>X-User-Id</c>/<c>X-User-Role</c>
/// before setting them (job-platform-gateway Program.cs), and notif-svc is never published
/// directly; the headers are therefore as trustworthy as the gateway itself.
/// This service deliberately validates no JWT of its own: the gateway is the only ingress.
/// </summary>
public sealed class GatewayIdentityMiddleware
{
    public const string UserIdHeader = "X-User-Id";
    public const string RoleHeader = "X-User-Role";

    private readonly RequestDelegate _next;

    public GatewayIdentityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? context.User.FindFirst("sub")?.Value
                     ?? context.Request.Headers[UserIdHeader].FirstOrDefault();
        var role = context.User.FindFirst(ClaimTypes.Role)?.Value
                   ?? context.User.FindFirst("role")?.Value
                   ?? context.Request.Headers[RoleHeader].FirstOrDefault();

        if (Guid.TryParse(userId, out var id))
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, id.ToString()),
            };

            if (!string.IsNullOrWhiteSpace(role))
            {
                claims.Add(new Claim(ClaimTypes.Role, role.Trim()));
            }

            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Gateway"));
        }

        await _next(context);
    }
}
