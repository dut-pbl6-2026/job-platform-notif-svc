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
/// setup as HealthEndpointsTests). Covers per-caller history scoping, webhook secret rejection and
/// subscribe persistence. Real Postgres, Kafka and Telegram are not used; those belong to the E2E stack.
/// </summary>
public class NotificationApiIntegrationTests
{
    private const string HistoryToken = "hist_test-Token_456";
    private const string WebhookSecret = "whsec_test-Value_123";
    private const string SubscribeUpdate = "{\"update_id\":1,\"message\":{\"message_id\":1,\"chat\":{\"id\":42,\"type\":\"private\"},\"text\":\"/subscribe\"}}";

    private sealed class ApiHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public ApiHost(WebApplication app, FakeTelegramSender sender, FakeUserEmailLookup lookup)
        {
            _app = app;
            Sender = sender;
            Lookup = lookup;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public FakeTelegramSender Sender { get; }

        public FakeUserEmailLookup Lookup { get; }

        public IServiceProvider Services => _app.Services;

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private static async Task<ApiHost> StartAsync(
        string? historyToken,
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
        var lookup = new FakeUserEmailLookup();
        builder.Services.AddSingleton<IUserEmailLookup>(lookup);

        var app = builder.Build();
        app.UseMiddleware<GatewayIdentityMiddleware>();
        app.MapNotificationEndpoints();
        await app.StartAsync();
        return new ApiHost(app, sender, lookup);
    }

    private static TelegramOptions EnabledTelegram() => new("123456:test-bot-token", WebhookSecret, "https://telegram.test");

    private static async Task SeedHistoryAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
        db.NotificationLogs.Add(new NotificationLog(
            Guid.NewGuid(), Guid.NewGuid(), "application.submitted", "",
            "alice@example.com", "New application", "application-submitted"));
        db.NotificationLogs.Add(new NotificationLog(
            Guid.NewGuid(), Guid.NewGuid(), "application.status_changed", "accepted",
            "bob@example.com", "Status update", "application-status-changed"));
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage History(string? token, Guid? userId = null, string? role = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/notifications/history?page=1&size=20");
        if (token is not null)
        {
            req.Headers.TryAddWithoutValidation(NotificationHistoryEndpoint.TokenHeader, token);
        }

        if (userId is not null)
        {
            req.Headers.TryAddWithoutValidation(GatewayIdentityMiddleware.UserIdHeader, userId.Value.ToString());
        }

        if (role is not null)
        {
            req.Headers.TryAddWithoutValidation(GatewayIdentityMiddleware.RoleHeader, role);
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
    public async Task History_NoIdentityAndNoToken_Returns401()
    {
        await using var api = await StartAsync(HistoryToken);
        await SeedHistoryAsync(api.Services);

        var res = await api.Client.SendAsync(History(null));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.DoesNotContain("alice@example.com", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task History_WithWrongToken_Returns401()
    {
        await using var api = await StartAsync(HistoryToken);
        await SeedHistoryAsync(api.Services);

        var res = await api.Client.SendAsync(History("hist_test-Token_457"));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task History_WithTokenButNoAdminRole_Returns403()
    {
        await using var api = await StartAsync(HistoryToken);
        await SeedHistoryAsync(api.Services);

        var res = await api.Client.SendAsync(History(HistoryToken));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task History_UserSeesOnlyTheirOwnEntries()
    {
        await using var api = await StartAsync(HistoryToken);
        await SeedHistoryAsync(api.Services);
        var alice = Guid.NewGuid();
        api.Lookup.Users[alice] = "alice@example.com";

        var res = await api.Client.SendAsync(History(null, alice));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("alice@example.com", item.GetProperty("recipientEmail").GetString());
        Assert.DoesNotContain("bob@example.com", body);
    }

    [Fact]
    public async Task History_AdminSeesEveryUser()
    {
        await using var api = await StartAsync(HistoryToken);
        await SeedHistoryAsync(api.Services);

        var res = await api.Client.SendAsync(History(null, Guid.NewGuid(), "Admin"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task History_AdminWithTheInternalTokenSeesEveryUser()
    {
        await using var api = await StartAsync(HistoryToken);
        await SeedHistoryAsync(api.Services);

        var res = await api.Client.SendAsync(History(HistoryToken, Guid.NewGuid(), "Admin"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var first = items[0];
        foreach (var name in new[] { "id", "recipientEmail", "subject", "templateName", "status", "sentAt", "createdAt" })
        {
            Assert.True(first.TryGetProperty(name, out _), $"history item is missing '{name}'");
        }
    }

    [Fact]
    public async Task History_PlaceholderConfiguredTokenIsTreatedAsUnset()
    {
        // The env template's public placeholder must not open the machine path.
        await using var api = await StartAsync("__SET_VIA_INFISICAL__");
        await SeedHistoryAsync(api.Services);

        var noIdentity = await api.Client.SendAsync(History(null));
        var placeholder = await api.Client.SendAsync(History("__SET_VIA_INFISICAL__"));

        Assert.Equal(HttpStatusCode.Unauthorized, noIdentity.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, placeholder.StatusCode);
    }

    [Fact]
    public async Task History_LookupUnavailable_Returns503()
    {
        await using var api = await StartAsync(HistoryToken);
        await SeedHistoryAsync(api.Services);
        api.Lookup.Unavailable = true;

        var res = await api.Client.SendAsync(History(null, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task Webhook_WithoutSecretHeader_Returns401AndPersistsNothing()
    {
        await using var api = await StartAsync(HistoryToken, EnabledTelegram());

        var res = await api.Client.SendAsync(Webhook(SubscribeUpdate, secret: null));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Empty(api.Sender.Sent);
        await AssertNoSubscriptionsAsync(api.Services);
    }

    [Fact]
    public async Task Webhook_WithWrongSecret_Returns401AndPersistsNothing()
    {
        await using var api = await StartAsync(HistoryToken, EnabledTelegram());

        var res = await api.Client.SendAsync(Webhook(SubscribeUpdate, "whsec_test-Value_124"));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Empty(api.Sender.Sent);
        await AssertNoSubscriptionsAsync(api.Services);
    }

    [Fact]
    public async Task Webhook_WhenBotNotConfigured_Returns401EvenWithAnySecret()
    {
        await using var api = await StartAsync(HistoryToken);

        var res = await api.Client.SendAsync(Webhook(SubscribeUpdate, WebhookSecret));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        await AssertNoSubscriptionsAsync(api.Services);
    }

    [Fact]
    public async Task Webhook_SubscribeWithCorrectSecret_Returns200AndPersistsActiveSubscription()
    {
        await using var api = await StartAsync(HistoryToken, EnabledTelegram());

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
        await using var api = await StartAsync(HistoryToken, EnabledTelegram());

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
