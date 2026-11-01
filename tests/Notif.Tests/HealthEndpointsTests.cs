using System.Net;
using System.Text.Json;
using Notif.Api.Observability;
using Notif.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Notif.Tests;

/// <summary>PBL6-49: /health liveness contract, /health/ready dependency checks, correlation id.</summary>
public class HealthEndpointsTests
{
    private const string Service = "svc-under-test";
    private const string UnreachableDb = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1;Command Timeout=1";

    private static async Task<WebApplication> StartAsync(
        bool inMemoryDb = true, Dictionary<string, string?>? config = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddServiceLogging(Service);
        if (config is not null)
        {
            builder.Configuration.AddInMemoryCollection(config);
        }

        if (inMemoryDb)
        {
            var name = "HealthDb_" + Guid.NewGuid();
            builder.Services.AddDbContext<NotifDbContext>(o => o.UseInMemoryDatabase(name));
        }
        else
        {
            builder.Services.AddDbContext<NotifDbContext>(o => o.UseNpgsql(UnreachableDb));
        }

        builder.Services.AddServiceHealthChecks<NotifDbContext>(builder.Configuration, kafka: true, smtp: true);

        var app = builder.Build();
        app.UseServiceLogging();
        app.MapServiceHealth(Service);
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> GetJsonAsync(
        WebApplication app, string path, string? correlationId = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (correlationId is not null)
        {
            req.Headers.TryAddWithoutValidation(ServiceLogging.CorrelationHeader, correlationId);
        }

        var res = await app.GetTestClient().SendAsync(req);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return (res, doc.RootElement.Clone());
    }

    [Fact]
    public async Task Health_ReturnsStaticLivenessContract_EvenWhenDbIsDown()
    {
        await using var app = await StartAsync(inMemoryDb: false);

        var (res, body) = await GetJsonAsync(app, "/health");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal(Service, body.GetProperty("service").GetString());
        Assert.Equal(2, body.EnumerateObject().Count());
    }

    [Fact]
    public async Task Ready_WithReachableDb_Returns200AndSkipsUnconfiguredDependencies()
    {
        await using var app = await StartAsync();

        var (res, body) = await GetJsonAsync(app, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        var names = body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray();
        Assert.Equal("database", Assert.Single(names));
    }

    [Fact]
    public async Task Ready_WithUnreachableDb_Returns503WithoutLeakingConnectionDetails()
    {
        await using var app = await StartAsync(inMemoryDb: false);

        var res = await app.GetTestClient().GetAsync("/health/ready");
        var text = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("unhealthy", text);
        Assert.DoesNotContain("127.0.0.1", text);
        Assert.DoesNotContain("Password", text);
    }

    [Fact]
    public async Task Ready_WithUnreachableKafka_IsDegradedButStill200()
    {
        await using var app = await StartAsync(config: new() { ["KAFKA_BOOTSTRAP_SERVERS"] = "SASL_SSL://127.0.0.1:1" });

        var (res, body) = await GetJsonAsync(app, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        var kafka = body.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "kafka");
        Assert.Equal("degraded", kafka.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ready_WithUnreachableSmtp_IsDegradedButStill200()
    {
        await using var app = await StartAsync(config: new() { ["SMTP_HOST"] = "127.0.0.1", ["SMTP_PORT"] = "1" });

        var (res, body) = await GetJsonAsync(app, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "smtp");
    }

    [Fact]
    public async Task Response_GeneratesCorrelationIdWhenAbsent()
    {
        await using var app = await StartAsync();

        var (res, _) = await GetJsonAsync(app, "/health");

        var id = Assert.Single(res.Headers.GetValues(ServiceLogging.CorrelationHeader));
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public async Task Response_EchoesInboundCorrelationId()
    {
        await using var app = await StartAsync();

        var (res, _) = await GetJsonAsync(app, "/health/ready", correlationId: "trace-123.abc");

        Assert.Equal("trace-123.abc", Assert.Single(res.Headers.GetValues(ServiceLogging.CorrelationHeader)));
    }

    [Fact]
    public async Task Response_ReplacesMalformedInboundCorrelationId()
    {
        await using var app = await StartAsync();

        var (res, _) = await GetJsonAsync(app, "/health", correlationId: "bad id\twith spaces");

        var id = Assert.Single(res.Headers.GetValues(ServiceLogging.CorrelationHeader));
        Assert.Matches("^[0-9a-f]{32}$", id);
    }
}
