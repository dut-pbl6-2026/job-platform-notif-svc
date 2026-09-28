using Microsoft.EntityFrameworkCore;
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

var conn = builder.Configuration["DATABASE_URL_NOTIF"];
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

builder.Services.AddSingleton<IEmailSender>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var host = config["SMTP_HOST"];
    if (!string.IsNullOrWhiteSpace(host))
    {
        return new SmtpEmailSender(config, sp.GetRequiredService<ILogger<SmtpEmailSender>>());
    }

    return (IEmailSender)new LoggerEmailSender(sp.GetRequiredService<ILogger<LoggerEmailSender>>());
});

builder.Services.Configure<KafkaOptions>(o =>
{
    o.BootstrapServers = builder.Configuration["KAFKA_BOOTSTRAP_SERVERS"]
        ?? builder.Configuration["Kafka:BootstrapServers"] ?? "";
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

app.MapGet("/api/notifications/history", async (int page, int size, NotifDbContext db, CancellationToken ct) =>
{
    page = Math.Max(1, page <= 0 ? 1 : page);
    size = Math.Clamp(size <= 0 ? 20 : size, 1, 100);
    var total = await db.EmailLogs.CountAsync(ct);
    var items = await db.EmailLogs
        .AsNoTracking()
        .OrderByDescending(e => e.CreatedAt)
        .Skip((page - 1) * size)
        .Take(size)
        .Select(e => new { e.Id, e.RecipientEmail, e.Subject, e.TemplateName, e.Status, e.SentAt, e.CreatedAt })
        .ToListAsync(ct);
    return Results.Ok(new { items, total, page, size });
})
.WithTags("Notifications")
.WithSummary("Paginated email delivery history (SRS NOTIF-01-06)");

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
