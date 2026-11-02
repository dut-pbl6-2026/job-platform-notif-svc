using System.Text.RegularExpressions;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Notif.Api.Observability;

/// <summary>
/// PBL6-49: Serilog setup (compact JSON in non-Development), request logging and
/// X-Correlation-Id propagation. Never logs headers or bodies, so Authorization
/// tokens and other secrets stay out of the logs.
/// </summary>
public static partial class ServiceLogging
{
    public const string CorrelationHeader = "X-Correlation-Id";
    public const string CorrelationProperty = "CorrelationId";

    public static WebApplicationBuilder AddServiceLogging(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Host.UseSerilog((ctx, _, cfg) =>
        {
            cfg.MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
                .ReadFrom.Configuration(ctx.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", serviceName);

            if (ctx.HostingEnvironment.IsDevelopment())
            {
                cfg.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Service} {CorrelationId} {Message:lj}{NewLine}{Exception}");
            }
            else
            {
                cfg.WriteTo.Console(new RenderedCompactJsonFormatter());
            }
        });

        return builder;
    }

    /// <summary>Correlation id middleware + Serilog request logging. Add before UseExceptionHandler.</summary>
    public static WebApplication UseServiceLogging(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var id = ResolveCorrelationId(ctx.Request.Headers[CorrelationHeader].FirstOrDefault());
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers[CorrelationHeader] = id;
                return Task.CompletedTask;
            });

            using (LogContext.PushProperty(CorrelationProperty, id))
            {
                await next();
            }
        });

        app.UseSerilogRequestLogging(o =>
        {
            o.GetLevel = (http, _, ex) =>
                ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
                : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
                : LogEventLevel.Information;
        });

        return app;
    }

    /// <summary>Reuses a well-formed inbound id, otherwise generates one (blocks log injection).</summary>
    internal static string ResolveCorrelationId(string? inbound) =>
        !string.IsNullOrEmpty(inbound) && SafeId().IsMatch(inbound)
            ? inbound
            : Guid.NewGuid().ToString("N");

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex SafeId();
}
