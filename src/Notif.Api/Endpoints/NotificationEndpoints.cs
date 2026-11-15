using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Services;

namespace Notif.Api.Endpoints;

/// <summary>Routes for the notification API. Kept outside Program.cs so integration tests can host them.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(
        this IEndpointRouteBuilder app,
        bool allowHistoryWithoutToken)
    {
        app.MapGet("/api/notifications/history", async (
            HttpContext ctx,
            IConfiguration configuration,
            NotifDbContext db,
            CancellationToken ct,
            int page = 1,
            int size = 20) =>
            await NotificationHistoryEndpoint.HandleAsync(
                ctx.Request.Headers[NotificationHistoryEndpoint.TokenHeader].FirstOrDefault(),
                configuration[NotificationHistoryEndpoint.TokenSetting],
                allowHistoryWithoutToken,
                page, size, db, ct))
        .WithTags("Notifications")
        .WithSummary("Paginated email delivery history (SRS NOTIF-01-06)");

        // PBL6-63 / TELE-01-05: Telegram webhook. Authenticated by the webhook secret header, not by JWT.
        app.MapPost("/api/notifications/telegram/webhook", async (
            HttpContext ctx,
            TelegramOptions telegramOptions,
            TelegramUpdateHandler handler,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            return await TelegramWebhookEndpoint.HandleAsync(
                ctx.Request.Headers[TelegramWebhookEndpoint.SecretHeader].FirstOrDefault(),
                telegramOptions.IsEnabled ? telegramOptions.WebhookSecret : null,
                body,
                handler,
                ct);
        })
        .WithTags("Notifications")
        .WithSummary("Telegram bot webhook (TELE-01-05). Requires the X-Telegram-Bot-Api-Secret-Token header.");

        return app;
    }
}
