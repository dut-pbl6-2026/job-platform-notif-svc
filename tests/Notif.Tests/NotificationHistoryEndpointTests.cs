using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Notif.Api.Endpoints;
using Notif.Core.Entities;
using Notif.Infrastructure.Data;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// Unit: history authorization and per-caller scoping. Route wiring and the HTTP contract are
/// covered in NotificationApiIntegrationTests.
/// </summary>
public class NotificationHistoryEndpointTests
{
    private const string Token = "hist-secret";

    [Fact]
    public async Task HandleAsync_NoIdentityAndNoToken_Returns401()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync(
            user: null, providedToken: null, expectedToken: Token, 1, 20, db, new FakeUserEmailLookup());

        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task HandleAsync_WrongToken_Returns401EvenWithAnIdentity()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync(
            Caller(Guid.NewGuid()), "wrong", Token, 1, 20, db, new FakeUserEmailLookup());

        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task HandleAsync_ValidTokenWithoutAdminRole_Returns403()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        // The internal token is a machine credential, not an identity: it never substitutes for Admin.
        var result = await NotificationHistoryEndpoint.HandleAsync(
            user: null, providedToken: Token, expectedToken: Token, 1, 20, db, new FakeUserEmailLookup());

        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task HandleAsync_ValidTokenForNonAdminUser_Returns403()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync(
            Caller(Guid.NewGuid()), Token, Token, 1, 20, db, new FakeUserEmailLookup());

        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task HandleAsync_AdminSeesEveryUser()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync(
            Caller(Guid.NewGuid(), "Admin"), null, Token, 1, 20, db, new FakeUserEmailLookup());

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(2, Total(result));
    }

    [Fact]
    public async Task HandleAsync_AdminWithValidTokenSeesEveryUser()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync(
            Caller(Guid.NewGuid(), "Admin"), Token, Token, 1, 20, db, new FakeUserEmailLookup());

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(2, Total(result));
    }

    [Fact]
    public async Task HandleAsync_UserSeesOnlyTheirOwnEntries()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var alice = Guid.NewGuid();
        var lookup = new FakeUserEmailLookup();
        lookup.Users[alice] = "alice@example.com";

        var result = await NotificationHistoryEndpoint.HandleAsync(
            Caller(alice), null, Token, 1, 20, db, lookup);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(1, Total(result));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        Assert.True(doc.RootElement.TryGetProperty("items", out var items), doc.RootElement.GetRawText());
        var item = Assert.Single(items.EnumerateArray());
        Assert.Equal("alice@example.com", item.GetProperty("RecipientEmail").GetString(), ignoreCase: true);
        Assert.DoesNotContain("bob@example.com", doc.RootElement.GetRawText());
    }

    [Fact]
    public async Task HandleAsync_UnknownCaller_ReturnsAnEmptyPage()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync(
            Caller(Guid.NewGuid()), null, Token, 1, 20, db, new FakeUserEmailLookup());

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, Total(result));
    }

    [Fact]
    public async Task HandleAsync_LookupUnavailable_FailsClosedWith503()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var lookup = new FakeUserEmailLookup { Unavailable = true };

        var result = await NotificationHistoryEndpoint.HandleAsync(
            Caller(Guid.NewGuid()), null, Token, 1, 20, db, lookup);

        Assert.Equal(503, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task HandleAsync_PlaceholderTokenIsTreatedAsUnset()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        // No identity, and the configured value is only the env template's public placeholder.
        var result = await NotificationHistoryEndpoint.HandleAsync(
            user: null, providedToken: "__SET_VIA_INFISICAL__", expectedToken: "__SET_VIA_INFISICAL__",
            1, 20, db, new FakeUserEmailLookup());

        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task HandleAsync_ClampsPageAndSize()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync(
            Caller(Guid.NewGuid(), "Admin"), null, Token, 0, 500, db, new FakeUserEmailLookup());

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        Assert.Equal(1, doc.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(100, doc.RootElement.GetProperty("size").GetInt32());
    }

    private static ClaimsPrincipal Caller(Guid userId, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Gateway"));
    }

    private static int Total(IResult result)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        return doc.RootElement.GetProperty("total").GetInt32();
    }

    private static NotifDbContext BuildDb()
    {
        var options = new DbContextOptionsBuilder<NotifDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new NotifDbContext(options);
    }

    private static async Task SeedAsync(NotifDbContext db)
    {
        db.NotificationLogs.Add(new NotificationLog(
            Guid.NewGuid(), Guid.NewGuid(), "application.submitted", "",
            "Alice@Example.com", "subject 1", "application-submitted"));
        db.NotificationLogs.Add(new NotificationLog(
            Guid.NewGuid(), Guid.NewGuid(), "application.status_changed", "accepted",
            "bob@example.com", "subject 2", "application-status-changed"));
        await db.SaveChangesAsync();
    }
}
