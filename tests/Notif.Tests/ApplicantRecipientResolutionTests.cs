using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Data;
using Notif.Infrastructure.Workers;
using SharedKernel.Kafka;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// Unit: applicant recipient resolution outcomes in ApplicationEventsConsumer, with the auth lookup faked.
/// Found sends to the looked-up address; not found skips with a warning and no address; unavailable retries.
/// </summary>
public class ApplicantRecipientResolutionTests
{
    private const string ApplicantEmail = "applicant@example.test";

    private sealed class Harness
    {
        public required RecipientProbeConsumer Consumer { get; init; }

        public required FakeUserEmailLookup Lookup { get; init; }

        public required RecordingEmailSender Sender { get; init; }

        public required CapturingLoggerProvider Logs { get; init; }

        public required IServiceProvider Services { get; init; }
    }

    private static Harness Build()
    {
        var lookup = new FakeUserEmailLookup();
        var sender = new RecordingEmailSender();
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<NotifDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IUserEmailLookup>(lookup);
        services.AddSingleton<INotificationService>(sender);
        var provider = services.BuildServiceProvider();

        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var configuration = new ConfigurationBuilder().Build();
        var consumer = new RecipientProbeConsumer(
            Options.Create(new KafkaOptions()),
            configuration,
            provider,
            loggerFactory.CreateLogger<ApplicationEventsConsumer>());

        return new Harness { Consumer = consumer, Lookup = lookup, Sender = sender, Logs = logs, Services = provider };
    }

    [Fact]
    public async Task Found_SendsToLookedUpAddress()
    {
        var h = Build();
        var applicantId = Guid.NewGuid();
        h.Lookup.Users[applicantId] = ApplicantEmail;

        var outcome = await h.Consumer.HandleAsync(ApplicantEvents.StatusChanged(Guid.NewGuid(), applicantId));

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal(new[] { ApplicantEmail }, h.Sender.Recipients);
        Assert.Equal(new[] { applicantId }, h.Lookup.Requested);
    }

    [Fact]
    public async Task NotFound_SkipsWithWarningWithoutEmail_AndSendsNothing()
    {
        var h = Build();
        var applicantId = Guid.NewGuid();

        var outcome = await h.Consumer.HandleAsync(ApplicantEvents.StatusChanged(Guid.NewGuid(), applicantId));

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Empty(h.Sender.Recipients);
        var warning = Assert.Single(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not found in auth-svc"));
        Assert.Contains(applicantId.ToString(), warning.Message);
        Assert.DoesNotContain("@", warning.Message);
    }

    [Fact]
    public async Task Unavailable_RetriesWithoutSending_AndWritesNoLog()
    {
        var h = Build();
        h.Lookup.Unavailable = true;

        var outcome = await h.Consumer.HandleAsync(ApplicantEvents.StatusChanged(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(MessageOutcome.Retry, outcome);
        Assert.Empty(h.Sender.Recipients);
        using var scope = h.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<NotifDbContext>().NotificationLogs.ToListAsync());
    }

    [Fact]
    public async Task Unavailable_ThenRecovered_DeliversOnRedelivery()
    {
        var h = Build();
        var applicantId = Guid.NewGuid();
        h.Lookup.Users[applicantId] = ApplicantEmail;
        var json = ApplicantEvents.StatusChanged(Guid.NewGuid(), applicantId);
        h.Lookup.Unavailable = true;

        var first = await h.Consumer.HandleAsync(json);
        h.Lookup.Unavailable = false;
        var second = await h.Consumer.HandleAsync(json);

        Assert.Equal(MessageOutcome.Retry, first);
        Assert.Equal(MessageOutcome.Handled, second);
        Assert.Equal(new[] { ApplicantEmail }, h.Sender.Recipients);
    }

    [Fact]
    public async Task Submitted_IsSkipped_WithoutLookupOrSend()
    {
        var h = Build();
        var applicantId = Guid.NewGuid();
        h.Lookup.Users[applicantId] = ApplicantEmail;

        var outcome = await h.Consumer.HandleAsync(ApplicantEvents.Submitted(Guid.NewGuid(), applicantId));

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Empty(h.Lookup.Requested);
        Assert.Empty(h.Sender.Recipients);
    }

    [Fact]
    public async Task StatusChangedWithEmptyApplicantId_IsSkipped_WithoutLookup()
    {
        var h = Build();

        var outcome = await h.Consumer.HandleAsync(ApplicantEvents.StatusChanged(Guid.NewGuid(), Guid.Empty));

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Empty(h.Lookup.Requested);
        Assert.Empty(h.Sender.Recipients);
    }
}
