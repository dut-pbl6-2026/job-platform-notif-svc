using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Notif.Api.Observability;

/// <summary>
/// PBL6-49: <c>/health</c> stays a static liveness probe (Render healthCheckPath);
/// <c>/health/ready</c> runs short-timeout dependency checks.
/// DB failure = Unhealthy (503). Kafka/SMTP failure = Degraded (still 200) and
/// those checks are only registered when the dependency is configured.
/// </summary>
public static class ServiceHealth
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddServiceHealthChecks<TDb>(
        this IServiceCollection services, IConfiguration config, bool kafka = false, bool smtp = false)
        where TDb : DbContext
    {
        var checks = services.AddHealthChecks();
        checks.AddCheck<DbContextHealthCheck<TDb>>("database", HealthStatus.Unhealthy, [ReadyTag]);

        if (kafka)
        {
            var servers = config["KAFKA_BOOTSTRAP_SERVERS"] ?? config["Kafka:BootstrapServers"];
            if (!string.IsNullOrWhiteSpace(servers))
            {
                checks.Add(new HealthCheckRegistration(
                    "kafka",
                    new TcpHealthCheck(ParseEndpoints(servers, 9092)),
                    HealthStatus.Degraded,
                    [ReadyTag]));
            }
        }

        if (smtp)
        {
            var host = config["SMTP_HOST"];
            if (!string.IsNullOrWhiteSpace(host))
            {
                var port = int.TryParse(config["SMTP_PORT"], out var p) ? p : 587;
                checks.Add(new HealthCheckRegistration(
                    "smtp",
                    new TcpHealthCheck([(host.Trim(), port)]),
                    HealthStatus.Degraded,
                    [ReadyTag]));
            }
        }

        return services;
    }

    /// <summary>Maps <c>/health</c> (unchanged JSON contract) and <c>/health/ready</c>.</summary>
    public static IEndpointRouteBuilder MapServiceHealth(this IEndpointRouteBuilder app, string service)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service }))
            .WithTags("Health")
            .WithName("HealthCheck")
            .ExcludeFromDescription();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(ReadyTag),
            ResponseWriter = (ctx, report) => WriteReport(ctx, report, service)
        }).ExcludeFromDescription();

        return app;
    }

    internal static (string Host, int Port)[] ParseEndpoints(string servers, int defaultPort)
    {
        var result = new List<(string, int)>();
        foreach (var raw in servers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw;
            var scheme = entry.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                entry = entry[(scheme + 3)..];
            }

            var colon = entry.LastIndexOf(':');
            if (colon > 0 && int.TryParse(entry[(colon + 1)..], out var port))
            {
                result.Add((entry[..colon], port));
            }
            else if (entry.Length > 0)
            {
                result.Add((entry, defaultPort));
            }
        }

        return [.. result];
    }

    private static Task WriteReport(HttpContext ctx, HealthReport report, string service)
    {
        ctx.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            service,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString().ToLowerInvariant(),
                description = e.Value.Description,
                durationMs = (long)e.Value.Duration.TotalMilliseconds
            })
        };
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

/// <summary>Opens a connection through the DbContext with a short timeout. Never leaks exception text (connection strings).</summary>
public sealed class DbContextHealthCheck<TDb>(TDb db) : IHealthCheck where TDb : DbContext
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            return await db.Database.CanConnectAsync(cts.Token)
                ? HealthCheckResult.Healthy("database reachable")
                : new HealthCheckResult(context.Registration.FailureStatus, "database unreachable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "database check timed out");
        }
        catch (Exception)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "database unreachable");
        }
    }
}

/// <summary>Plain TCP connect (no auth, no protocol) to any of the endpoints, 2s timeout.</summary>
public sealed class TcpHealthCheck((string Host, int Port)[] endpoints) : IHealthCheck
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        foreach (var (host, port) in endpoints)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cts.Token);
                return HealthCheckResult.Healthy("reachable");
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // try next endpoint
            }
        }

        return new HealthCheckResult(context.Registration.FailureStatus, "unreachable");
    }
}
