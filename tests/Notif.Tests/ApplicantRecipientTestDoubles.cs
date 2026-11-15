using System.Net;
using Microsoft.Extensions.Logging;
using Notif.Core.Interfaces;

namespace Notif.Tests;

/// <summary>Auth lookup fake: known ids map to an email; unknown ids return null; can simulate an outage.</summary>
internal sealed class FakeUserEmailLookup : IUserEmailLookup
{
    public Dictionary<Guid, string> Users { get; } = new();

    public bool Unavailable { get; set; }

    public List<Guid> Requested { get; } = new();

    public Task<string?> FindEmailAsync(Guid userId, CancellationToken ct = default)
    {
        Requested.Add(userId);
        if (Unavailable)
        {
            throw new UserEmailLookupUnavailableException("auth-svc unavailable after 3 attempts: HTTP 503.");
        }

        return Task.FromResult(Users.TryGetValue(userId, out var email) ? email : null);
    }
}

/// <summary>Records every email the notification service is asked to send.</summary>
internal sealed class RecordingEmailSender : Notif.Core.Interfaces.INotificationService
{
    public List<string> Recipients { get; } = new();

    public Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        Recipients.Add(to);
        return Task.CompletedTask;
    }
}

/// <summary>Captures formatted log messages so tests can assert what was (not) written.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Category, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        private readonly string _category;

        public Logger(CapturingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _owner.Entries.Add((logLevel, _category, formatter(state, exception)));
        }
    }
}

/// <summary>Scripted HttpMessageHandler: each call returns the next queued response or throws the next exception.</summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Queue<object> _script = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public ScriptedHttpHandler Respond(HttpStatusCode status, string? json = null)
    {
        var response = new HttpResponseMessage(status);
        if (json is not null)
        {
            response.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        _script.Enqueue(response);
        return this;
    }

    public ScriptedHttpHandler Throw(Exception exception)
    {
        _script.Enqueue(exception);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var next = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("No scripted response left.");
        if (next is Exception ex)
        {
            throw ex;
        }

        return Task.FromResult((HttpResponseMessage)next);
    }
}

/// <summary>Exposes the consumer's protected message handler to tests.</summary>
internal sealed class RecipientProbeConsumer : Notif.Infrastructure.Workers.ApplicationEventsConsumer
{
    public RecipientProbeConsumer(
        Microsoft.Extensions.Options.IOptions<SharedKernel.Kafka.KafkaOptions> kafka,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        IServiceProvider services,
        ILogger<Notif.Infrastructure.Workers.ApplicationEventsConsumer> logger)
        : base(kafka, configuration, services, logger)
    {
    }

    public Task<SharedKernel.Kafka.MessageOutcome> HandleAsync(string value, CancellationToken ct = default)
        => HandleMessageAsync(null, value, ct);
}

internal static class ApplicantEvents
{
    public static string StatusChanged(Guid applicationId, Guid applicantId, string newStatus = "accepted")
    {
        var payload = new SharedKernel.Events.ApplicationStatusChangedEvent(
            applicationId, Guid.NewGuid(), "Backend Dev", applicantId,
            "pending", newStatus, Guid.NewGuid(), DateTime.UtcNow);
        return System.Text.Json.JsonSerializer.Serialize(
            SharedKernel.Events.EventEnvelope<SharedKernel.Events.ApplicationStatusChangedEvent>.Create(
                SharedKernel.Events.ApplicationEventTypes.StatusChanged, payload),
            SharedKernel.Kafka.KafkaJson.Options);
    }

    public static string Submitted(Guid applicationId, Guid applicantId)
    {
        var payload = new SharedKernel.Events.ApplicationSubmittedEvent(
            applicationId, Guid.NewGuid(), "Backend Dev", applicantId, DateTime.UtcNow);
        return System.Text.Json.JsonSerializer.Serialize(
            SharedKernel.Events.EventEnvelope<SharedKernel.Events.ApplicationSubmittedEvent>.Create(
                SharedKernel.Events.ApplicationEventTypes.Submitted, payload),
            SharedKernel.Kafka.KafkaJson.Options);
    }
}
