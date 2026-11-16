using Microsoft.EntityFrameworkCore;
using Notif.Api.Bootstrap;
using Notif.Api.Endpoints;
using Notif.Api.Observability;
using Notif.Api.Security;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Configuration;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Services;
using Notif.Infrastructure.Workers;
using SharedKernel.Kafka;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

// PBL6-49: Serilog (compact JSON in non-Development) + correlation id
builder.AddServiceLogging("notification");

// Monitoring: expose OpenTelemetry metrics on /metrics (Prometheus scrape target).
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter();
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

// Schema migration is a separate deploy step: `migrate` applies pending migrations and exits.
// Checked before the token policy so the one-off job needs only the database settings.
// The web host never migrates on startup.
if (MigrateCommand.IsInvoked(args))
    return await MigrateCommand.RunAsync(args, conn, Console.Out, Console.Error);

// Notification history exposes recipient PII: the internal token is required outside Development.
NotificationHistoryEndpoint.ValidateTokenPolicy(
    builder.Configuration[NotificationHistoryEndpoint.TokenSetting],
    builder.Environment.IsDevelopment());

builder.Services.AddDbContext<NotifDbContext>(o => o.UseNpgsql(conn));
builder.Services.AddServiceHealthChecks<NotifDbContext>(builder.Configuration);

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
// Applicant email lookup via auth-svc internal API. Fails startup outside Development when
// AUTH_SERVICE_URL or AUTH_INTERNAL_TOKEN_FOR_NOTIF is missing; there is no fallback recipient.
builder.Services.AddAuthServiceClient(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddHostedService<ApplicationEventsConsumer>();

// feat(admin) Slice 5: job approval/rejection emails to recruiters.
// Gated (NOTIF_JOB_EVENTS_ENABLED, off by default): JobUpdatedEvent carries neither the
// recruiter's address nor a previous-status transition, so running it by default would
// mail the shared fallback inbox and label ordinary edits of live jobs as approvals.
var jobEventsEnabled = JobEventsConsumer.IsEnabled(builder.Configuration);
if (jobEventsEnabled)
{
    var jobEventsRecipient = builder.Configuration["NOTIF_DEFAULT_RECIPIENT"]
        ?? builder.Configuration["SMTP_FROM"];
    if (IsUnusable(jobEventsRecipient))
    {
        throw new InvalidOperationException(
            "NOTIF_JOB_EVENTS_ENABLED=true requires NOTIF_DEFAULT_RECIPIENT or SMTP_FROM. "
            + "Refusing to start a consumer that cannot deliver notifications.");
    }

    builder.Services.AddHostedService<JobEventsConsumer>();
}

// PBL6-63: Telegram job alert bot. Off unless TELEGRAM_BOT_TOKEN is set; a partial config fails startup.
var telegramOptions = TelegramOptions.Load(builder.Configuration);
builder.Services.AddSingleton(telegramOptions);
builder.Services.AddSingleton<ITelegramSender>(sp => new TelegramBotClient(
    new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
    sp.GetRequiredService<TelegramOptions>(),
    sp.GetRequiredService<ILogger<TelegramBotClient>>()));
builder.Services.AddScoped<TelegramUpdateHandler>();
builder.Services.AddScoped<TelegramBroadcaster>();

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Notification Service API", Version = "v0.1.0" });
});

var app = builder.Build();

app.UseServiceLogging();
app.UseExceptionHandler();

// PBL6-79: history is scoped to the caller. The gateway forwards the validated JWT subject/role
// as X-User-Id/X-User-Role (and strips client-supplied copies); this service has no public ingress.
app.UseMiddleware<GatewayIdentityMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// /health = static liveness, /health/ready = dependency checks (PBL6-49)
app.MapServiceHealth("notification");

// Monitoring: /metrics is meant for the internal scrape network; keep it off in
// Production unless METRICS_ENABLED=true (all services are publicly reachable).
if (app.Configuration.GetValue("METRICS_ENABLED", !app.Environment.IsProduction()))
{
    app.MapPrometheusScrapingEndpoint();
}

app.MapGet("/", () => Results.Ok(new { service = "notification", version = "0.1.0" }))
    .ExcludeFromDescription();

app.MapNotificationEndpoints();

// app-svc: recruiter-composed email and interview invites, token-protected, never routed publicly.
app.MapInternalEmailEndpoint();

if (app.Environment.IsDevelopment()
    && SecretValue.Usable(app.Configuration[NotificationHistoryEndpoint.TokenSetting]) is null)
{
    app.Logger.LogWarning(
        "NOTIF_HISTORY_TOKEN is not set (or is only a template placeholder). History stays closed to machine callers; "
        + "users still see only their own entries.");
}

if (builder.Environment.IsDevelopment()
    && !app.Services.GetRequiredService<AuthServiceOptions>().IsConfigured)
{
    app.Logger.LogWarning(
        "AUTH_SERVICE_URL or AUTH_INTERNAL_TOKEN_FOR_NOTIF is not set. Applicant emails will be retried until they are configured.");
}

if (!jobEventsEnabled)
{
    app.Logger.LogWarning(
        "Job approval/rejection emails are disabled (NOTIF_JOB_EVENTS_ENABLED is not true). "
        + "Enabling requires a recruiter recipient lookup and the job.updated previousStatus/rejectionReason contract.");
}

app.Run();

return 0;

public partial class Program
{
}
