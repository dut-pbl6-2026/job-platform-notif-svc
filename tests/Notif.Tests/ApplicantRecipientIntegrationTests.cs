using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Services;
using Notif.Infrastructure.Workers;
using SharedKernel.Kafka;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// Integration: the application-event path hosted in a TestServer host. Configuration and the auth
/// client registration use the production wiring (AddAuthServiceClient); only the auth lookup is faked.
/// Real Postgres, Kafka, auth-svc and SMTP are not used; those belong to the E2E stack.
/// </summary>
public class ApplicantRecipientIntegrationTests
{
    private const string ApplicantEmail = "applicant@example.test";

    private static async Task<(WebApplication App, FakeUserEmailLookup Lookup, RecordingEmailSender Sender, CapturingLoggerProvider Logs)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AUTH_SERVICE_URL"] = "http://auth-svc:5001",
            ["INTERNAL_SERVICE_TOKEN"] = "internal-test-token",
        });

        var lookup = new FakeUserEmailLookup();
        var sender = new RecordingEmailSender();
        var logs = new CapturingLoggerProvider();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        var dbName = Guid.NewGuid().ToString();
        builder.Services.AddDbContext<NotifDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddAuthServiceClient(builder.Configuration, isDevelopment: false);
        builder.Services.AddSingleton<IUserEmailLookup>(lookup);
        builder.Services.AddSingleton<INotificationService>(sender);

        var app = builder.Build();
        await app.StartAsync();
        return (app, lookup, sender, logs);
    }

    private static RecipientProbeConsumer Consumer(WebApplication app)
        => new(
            Microsoft.Extensions.Options.Options.Create(new SharedKernel.Kafka.KafkaOptions()),
            app.Configuration,
            app.Services,
            app.Services.GetRequiredService<ILogger<ApplicationEventsConsumer>>());

    [Fact]
    public async Task KnownApplicant_ReceivesMailAtLookedUpAddress_AndIsLogged()
    {
        var (app, lookup, sender, _) = await StartAsync();
        await using var _host = app;
        var applicantId = Guid.NewGuid();
        lookup.Users[applicantId] = ApplicantEmail;

        var outcome = await Consumer(app).HandleAsync(ApplicantEvents.StatusChanged(Guid.NewGuid(), applicantId));

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(new[] { ApplicantEmail }, sender.Recipients);
        using var scope = app.Services.CreateScope();
        var log = Assert.Single(await scope.ServiceProvider.GetRequiredService<NotifDbContext>().NotificationLogs.ToListAsync());
        Assert.Equal(ApplicantEmail, log.RecipientEmail);
        Assert.Equal("sent", log.Status);
    }

    [Fact]
    public async Task UnknownApplicant_SendsNothing_AndLogsWarningWithoutEmail()
    {
        var (app, _, sender, logs) = await StartAsync();
        await using var _host = app;
        var applicantId = Guid.NewGuid();

        var outcome = await Consumer(app).HandleAsync(ApplicantEvents.StatusChanged(Guid.NewGuid(), applicantId));

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Empty(sender.Recipients);
        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not found in auth-svc"));
        Assert.Contains(applicantId.ToString(), warning.Message);
        Assert.DoesNotContain("@", warning.Message);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("@"));
    }

}
