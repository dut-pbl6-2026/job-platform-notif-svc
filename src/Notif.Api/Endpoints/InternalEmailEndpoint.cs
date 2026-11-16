using System.Security.Cryptography;
using System.Text;
using Notif.Core.Interfaces;

namespace Notif.Api.Endpoints;

/// <summary>
/// Service-to-service email endpoint used by app-svc for recruiter-composed mail and interview
/// invites. Not routed by the gateway. Caller presents <c>Authorization: Bearer &lt;NOTIF_INTERNAL_TOKEN&gt;</c>;
/// the comparison is constant-time and fails closed when the token is not configured.
/// </summary>
public static class InternalEmailEndpoint
{
    public const string TokenKey = "NOTIF_INTERNAL_TOKEN";
    private const string BearerPrefix = "Bearer ";
    private const int MaxHtmlLength = 200_000;
    private const int MaxIcsBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapInternalEmailEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/internal/email", HandleAsync)
            .WithTags("Internal")
            .ExcludeFromDescription();
        return app;
    }

    public static async Task<IResult> HandleAsync(
        InternalEmailRequest request,
        HttpContext http,
        INotificationService sender,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("InternalEmail");
        var expected = config[TokenKey];

        if (string.IsNullOrWhiteSpace(expected))
        {
            logger.LogError("{TokenKey} is not configured. Refusing to send.", TokenKey);
            return Results.Json(new { message = "Unauthorized" }, statusCode: 401);
        }

        if (!IsAuthorized(http.Request.Headers.Authorization.ToString(), expected))
            return Results.Json(new { message = "Unauthorized" }, statusCode: 401);

        if (string.IsNullOrWhiteSpace(request.To)
            || string.IsNullOrWhiteSpace(request.Subject)
            || string.IsNullOrWhiteSpace(request.Html))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["email"] = ["to, subject and html are required."],
            });
        }

        if (request.Html.Length > MaxHtmlLength)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["html"] = [$"html cannot exceed {MaxHtmlLength} characters."],
            });

        EmailAttachment? attachment = null;
        if (!string.IsNullOrWhiteSpace(request.IcsContent))
        {
            var content = Encoding.UTF8.GetBytes(request.IcsContent);
            if (content.Length > MaxIcsBytes)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["icsContent"] = [$"icsContent cannot exceed {MaxIcsBytes} bytes."],
                });
            }

            var fileName = Path.GetFileName(
                string.IsNullOrWhiteSpace(request.IcsFileName) ? "invite.ics" : request.IcsFileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "invite.ics";

            attachment = new EmailAttachment(fileName, "text/calendar", content);
        }

        try
        {
            await sender.SendAsync(
                request.To.Trim(),
                request.Subject.Trim(),
                request.Html,
                string.IsNullOrWhiteSpace(request.Text) ? StripTags(request.Html) : request.Text,
                attachment,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Internal email to {Recipient} failed.", request.To);
            return Results.Problem(detail: "The message could not be sent.", statusCode: 502);
        }

        return Results.Accepted(value: new { sent = true });
    }

    private static bool IsAuthorized(string header, string expected)
    {
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var token = header[BearerPrefix.Length..].Trim();
        if (token.Length == 0)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    /// <summary>Very small fallback for a caller that sends only HTML: the text part must still exist.</summary>
    private static string StripTags(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Trim();
}

/// <summary>Payload from app-svc. <see cref="IcsContent"/> is the raw VCALENDAR text, not base64.</summary>
public sealed record InternalEmailRequest(
    string To,
    string Subject,
    string Html,
    string? Text = null,
    string? IcsFileName = null,
    string? IcsContent = null);
