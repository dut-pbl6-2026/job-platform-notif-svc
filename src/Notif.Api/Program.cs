using Microsoft.EntityFrameworkCore;
using Notif.Api.Endpoints;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Services;
using Notif.Infrastructure.Workers;
using SharedKernel.Kafka;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";
});

static bool IsUnusable(string? v) =>
    string.IsNullOrWhiteSpace(v)
    || v.StartsWith("<set via", StringComparison.Ordinal)
    || v.Contains("${");

var conn = builder.Configuration["DATABASE_URL_NOTIF"]
    ?? builder.Configuration["NOTIF_CONNECTION_STRING"];
if (IsUnusable(conn))
{
    conn = builder.Configuration.GetConnectionString("NotifDb");
}

if (IsUnusable(conn))
{
    if (builder.Environment.IsDevelopment())
    {
        conn = "Host=localhost;Port=5432;Database=job_platform_notif;Username=postgres;Password=postgres";
    }
    else
    {
        throw new InvalidOperationException("Connection string not configured. Set DATABASE_URL_NOTIF env var or ConnectionStrings:NotifDb.");
    }
}

builder.Services.AddDbContext<NotifDbContext>(o => o.UseNpgsql(conn));

builder.Services.AddSingleton<INotificationService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var host = config["SMTP_HOST"];
    if (!string.IsNullOrWhiteSpace(host))
    {
        return new SmtpEmailSender(config, sp.GetRequiredService<ILogger<SmtpEmailSender>>());
    }

    return new LoggerEmailSender(sp.GetRequiredService<ILogger<LoggerEmailSender>>());
});

builder.Services.Configure<KafkaOptions>(o =>
{
    o.BootstrapServers = builder.Configuration["KAFKA_BOOTSTRAP_SERVERS"]
        ?? builder.Configuration["Kafka:BootstrapServers"] ?? "";
    o.SaslUsername = builder.Configuration["KAFKA_SASL_USERNAME"]
        ?? builder.Configuration["Kafka:SaslUsername"] ?? "";
    o.SaslPassword = builder.Configuration["KAFKA_SASL_PASSWORD"]
        ?? builder.Configuration["Kafka:SaslPassword"] ?? "";
    o.SecurityProtocol = builder.Configuration["KAFKA_SECURITY_PROTOCOL"]
        ?? builder.Configuration["Kafka:SecurityProtocol"] ?? "";
    o.SaslMechanism = builder.Configuration["KAFKA_SASL_MECHANISM"]
        ?? builder.Configuration["Kafka:SaslMechanism"] ?? "Plain";
    o.SslCaLocation = builder.Configuration["KAFKA_SSL_CA_LOCATION"]
        ?? builder.Configuration["Kafka:SslCaLocation"] ?? "";
});
builder.Services.AddHostedService<ApplicationEventsConsumer>();

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Notification Service API", Version = "v0.1.0" });
});

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "notification" }))
    .WithTags("Health")
    .ExcludeFromDescription();

app.MapGet("/", () => Results.Ok(new { service = "notification", version = "0.1.0" }))
    .ExcludeFromDescription();

app.MapGet("/api/notifications/history", async (
    HttpContext ctx,
    NotifDbContext db,
    CancellationToken ct,
    int page = 1,
    int size = 20) =>
    await NotificationHistoryEndpoint.HandleAsync(
        ctx.Request.Headers[NotificationHistoryEndpoint.TokenHeader].FirstOrDefault(),
        app.Configuration["NOTIF_HISTORY_TOKEN"],
        page, size, db, ct))
.WithTags("Notifications")
.WithSummary("Paginated email delivery history (SRS NOTIF-01-06)");

// NOTIF-01-06 rows carry recipient PII — loud signal when the internal token is off.
if (string.IsNullOrWhiteSpace(app.Configuration["NOTIF_HISTORY_TOKEN"]))
{
    app.Logger.LogWarning(
        "NOTIF_HISTORY_TOKEN is not set. Notification history accepts requests without an internal token — restrict network access in non-dev environments.");
}

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var db = scope.ServiceProvider.GetRequiredService<NotifDbContext>();
    try
    {
        await db.Database.MigrateAsync();
        logger.LogInformation("Notif DB migrated.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Notif DB migrate failed — shutting down");
        throw;
    }
}

app.Run();

public partial class Program
{
}
