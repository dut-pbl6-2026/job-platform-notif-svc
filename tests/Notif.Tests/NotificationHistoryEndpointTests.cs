using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Notif.Api.Endpoints;
using Notif.Core.Entities;
using Notif.Infrastructure.Data;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// History endpoint contract: internal-token gate (F-2) and pagination defaults.
/// </summary>
public class NotificationHistoryEndpointTests
{
    [Fact]
    public async Task HandleAsync_WhenTokenMismatch_Returns401()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync("wrong", "secret", 1, 20, db);

        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task HandleAsync_WhenTokenDisabled_ReturnsOk()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync(null, "", 1, 20, db);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task HandleAsync_WhenTokenMatches_ReturnsOk()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync("secret", "secret", 1, 20, db);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task HandleAsync_ClampsPageAndSize()
    {
        using var db = BuildDb();
        await SeedAsync(db);

        var result = await NotificationHistoryEndpoint.HandleAsync(null, "", 0, 500, db);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        Assert.Equal(1, doc.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(100, doc.RootElement.GetProperty("size").GetInt32());
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
            "a@example.com", "subject 1", "application-submitted"));
        db.NotificationLogs.Add(new NotificationLog(
            Guid.NewGuid(), Guid.NewGuid(), "application.status_changed", "accepted",
            "b@example.com", "subject 2", "application-status-changed"));
        await db.SaveChangesAsync();
    }
}
