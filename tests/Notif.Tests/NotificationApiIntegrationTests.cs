using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notif.Api.Endpoints;
using Notif.Api.Security;
using Notif.Core.Entities;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Services;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// Integration: the notification HTTP surface hosted in-process (TestServer + EF InMemory, the same
/// setup as HealthEndpointsTests). Covers history auth, webhook secret rejection and subscribe persistence.
/// Real Postgres, Kafka and Telegram are not used; those belong to the E2E stack.
/// </summary>
public class NotificationApiIntegrationTests
{
    private const string HistoryToken = "hist_test-Token_456";
    private const string WebhookSecret = "whsec_test-Value_123";
    private const string SubscribeUpdate = "{\"update_id\":1,\"message\":{\"message_id\":1,\"chat\":{\"id\":42,\"type\":\"private\"},\"text\":\"/subscribe\"}}";

    private sealed class ApiHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public ApiHost(WebApplication app, FakeTelegramSender sender)
        {
            _app = app;
            Sender = sender;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public FakeTelegramSender Sender { get; }

        public IServiceProvider Services => _app.Services;

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private static async Task<ApiHost> StartAsync(
        string? historyToken,
        bool allowHistoryWithoutToken,
        TelegramOptions? telegram = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var config = new Dictionary<string, string?>();
        if (historyToken is not null)
        {
            config[NotificationHistoryEndpoint.TokenSetting] = historyToken;
        }

        builder.Configuration.AddInMemoryCollection(config);

        var dbName = "NotifApi_" + Guid.NewGuid();
        builder.Services.AddDbContext<NotifDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddSingleton(telegram ?? TelegramOptions.Disabled);
        var sender = new FakeTelegramSender();
        builder.Services.AddSingleton<ITelegramSender>(sender);
        builder.Services.AddScoped<TelegramUpdateHandler>();

        var app = builder.Build();
        app.MapNotificationEndpoints(allowHistoryWithoutToken);
        await app.StartAsync();
        return new ApiHost(app, sender);
    }

    private static TelegramOptions EnabledTelegram() => new("123456:test-bot-token", WebhookSecret, "https://telegram.test");

    private static async Task SeedHistoryAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        db.NotificationLogs.Add(new NotificationLog(
            Guid.NewGuid(), Guid.NewGuid(), "application.submitted", "",
            "recruiter@example.com", "New application", "application-submitted"));
        db.NotificationLogs.Add(new NotificationLog(
            Guid.NewGuid(), Guid.NewGuid(), "application.status_changed", "accepted",
            "applicant@example.com", "Status update", "application-status-changed"));
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage History(string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/notifications/history?page=1&size=20");
        if (token is not null)
        {
            req.Headers.TryAddWithoutValidation(NotificationHistoryEndpoint.TokenHeader, token);
        }

        return req;
    }

    private static HttpRequestMessage Webhook(string body, string? secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/notifications/telegram/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (secret is not null)
        {
            req.Headers.TryAddWithoutValidation(TelegramWebhookEndpoint.SecretHeader, secret);
        }

        return req;
    }

    [Fact]
    public async Task History_WithTokenConfigured_MissingHeader_Returns401()
    {
        await using var api = await StartAsync(HistoryToken, allowHistoryWithoutToken: false);
        await SeedHistoryAsync(api.Services);

        var res = await api.Client.SendAsync(History(null));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.DoesNotContain("recruiter@example.com", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task History_WithWrongToken_Returns401()
    {
        await using var api = await StartAsync(HistoryToken, allowHistoryWithoutToken: false);
        await SeedHistoryAsync(api.Services);

        var res = await api.Client.SendAsync(History("hist_test-Token_457"));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task History_WithoutTokenAndNoDevelopmentOptIn_FailsClosed_Returns401()
    {
        await using var api = await StartAsync(historyToken: null, allowHistoryWithoutToken: false);
        await SeedHistoryAsync(api.Services);

        var res = await api.Client.SendAsync(History(null));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task History_WithoutTokenInDevelopmentOptIn_Returns200()
    {
        await using var api = await StartAsync(historyToken: null, allowHistoryWithoutToken: true);
        await SeedHistoryAsync(api.Services);

        var res = await api.Client.SendAsync(History(null));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task History_WithCorrectToken_ReturnsPagedShape()
    {
        await using var api = await StartAsync(HistoryToken, allowHistoryWithoutToken: false);
        await SeedHistoryAsync(api.Services);

        var res = await api.Client.SendAsync(History(HistoryToken));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(20, root.GetProperty("size").GetInt32());

        var items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var first = items[0];
        foreach (var name in new[] { "id", "recipientEmail", "subject", "templateName", "status", "sentAt", "createdAt" })
        {
            Assert.True(first.TryGetProperty(name, out _), $"history item is missing '{name}'");
        }
    }

    [Fact]
    public async Task Webhook_WithoutSecretHeader_Returns401AndPersistsNothing()
    {
        await using var api = await StartAsync(HistoryToken, false, EnabledTelegram());

        var res = await api.Client.SendAsync(Webhook(SubscribeUpdate, secret: null));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Empty(api.Sender.Sent);
        await AssertNoSubscriptionsAsync(api.Services);
    }

    [Fact]
    public async Task Webhook_WithWrongSecret_Returns401AndPersistsNothing()
    {
        await using var api = await StartAsync(HistoryToken, false, EnabledTelegram());

        var res = await api.Client.SendAsync(Webhook(SubscribeUpdate, "whsec_test-Value_124"));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Empty(api.Sender.Sent);
        await AssertNoSubscriptionsAsync(api.Services);
    }

    [Fact]
    public async Task Webhook_WhenBotNotConfigured_Returns401EvenWithAnySecret()
    {
        await using var api = await StartAsync(HistoryToken, false, telegram: null);

        var res = await api.Client.SendAsync(Webhook(SubscribeUpdate, WebhookSecret));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        await AssertNoSubscriptionsAsync(api.Services);
    }

    [Fact]
    public async Task Webhook_SubscribeWithCorrectSecret_Returns200AndPersistsActiveSubscription()
    {
        await using var api = await StartAsync(HistoryToken, false, EnabledTelegram());

        var res = await api.Client.SendAsync(Webhook(SubscribeUpdate, WebhookSecret));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var row = Assert.Single(db.TelegramSubscriptions);
        Assert.Equal(42, row.ChatId);
        Assert.True(row.IsActive);
        Assert.Contains(api.Sender.Sent, m => m.ChatId == 42);
    }

    [Fact]
    public async Task Webhook_SubscribeTwice_KeepsOneActiveRow()
    {
        await using var api = await StartAsync(HistoryToken, false, EnabledTelegram());

        await api.Client.SendAsync(Webhook(SubscribeUpdate, WebhookSecret));
        var second = await api.Client.SendAsync(Webhook(SubscribeUpdate, WebhookSecret));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        var row = Assert.Single(db.TelegramSubscriptions);
        Assert.True(row.IsActive);
    }

    private static async Task AssertNoSubscriptionsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        Assert.False(await db.TelegramSubscriptions.AnyAsync());
    }
}
