using System.Net;
using System.Net.Sockets;
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

        builder.Services.AddServiceHealthChecks<NotifDbContext>(builder.Configuration);

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

    [Theory]
    [InlineData("127.0.0.1:9092", "127.0.0.1", 9092)]
    [InlineData("broker", "broker", 9092)]
    [InlineData("SASL_SSL://broker:9093", "broker", 9093)]
    [InlineData("[::1]:9094", "::1", 9094)]
    [InlineData("[::1]", "::1", 9092)]
    public void ParseEndpoints_ParsesSingleEntry(string input, string host, int port)
    {
        var endpoints = ServiceHealth.ParseEndpoints(input, 9092);

        var only = Assert.Single(endpoints);
        Assert.Equal(host, only.Host);
        Assert.Equal(port, only.Port);
    }

    [Fact]
    public void ParseEndpoints_ParsesMultipleEntries()
    {
        var endpoints = ServiceHealth.ParseEndpoints("a:1, b:2 ,[::1]:3", 9092);

        Assert.Equal([("a", 1), ("b", 2), ("::1", 3)], endpoints);
    }

    [Theory]
    [InlineData("__SET_VIA_INFISICAL__")]
    [InlineData("<set via infisical>")]
    [InlineData("${KAFKA_BOOTSTRAP_SERVERS}")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsConfigured_RejectsPlaceholders(string? value) =>
        Assert.False(ServiceHealth.IsConfigured(value));

    [Fact]
    public void IsConfigured_AcceptsRealValue() =>
        Assert.True(ServiceHealth.IsConfigured("broker:9092"));

    [Fact]
    public async Task Ready_WithReachableKafka_ReportsKafkaHealthy()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        await using var app = await StartAsync(config: new() { ["KAFKA_BOOTSTRAP_SERVERS"] = $"127.0.0.1:{port}" });

        var (res, body) = await GetJsonAsync(app, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var kafka = body.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "kafka");
        Assert.Equal("healthy", kafka.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ready_WithPlaceholderConfig_DoesNotRegisterKafkaOrSmtp()
    {
        await using var app = await StartAsync(config: new()
        {
            ["KAFKA_BOOTSTRAP_SERVERS"] = "__SET_VIA_INFISICAL__",
            ["SMTP_HOST"] = "__SET_VIA_INFISICAL__"
        });

        var (res, body) = await GetJsonAsync(app, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        var names = body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("kafka", names);
        Assert.DoesNotContain("smtp", names);
    }
}
