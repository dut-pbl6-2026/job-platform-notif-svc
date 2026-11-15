using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Notif.Core.Interfaces;

namespace Notif.Infrastructure.Services;

/// <summary>
/// Client for auth-svc <c>GET /api/internal/users/{id}/email</c> (called directly on the internal
/// network, not through the gateway). Like <see cref="TelegramBotClient"/> it uses a plain
/// <see cref="HttpClient"/> supplied by the composition root.
/// Transient failures (transport errors, timeouts, 5xx, 408, 429) are retried with bounded backoff.
/// A 401/403 is a configuration fault and is not retried. Both end in <see cref="UserEmailLookupUnavailableException"/>.
/// </summary>
public sealed class AuthUserClient : IUserEmailLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan[] DefaultRetryDelays =
    {
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
    };

    private readonly HttpClient _http;
    private readonly AuthServiceOptions _options;
    private readonly ILogger<AuthUserClient> _logger;
    private readonly TimeSpan[] _retryDelays;

    public AuthUserClient(
        HttpClient http,
        AuthServiceOptions options,
        ILogger<AuthUserClient> logger,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _retryDelays = retryDelays?.ToArray() ?? DefaultRetryDelays;
    }

    /// <summary>Total attempts = retry delays + 1.</summary>
    public int MaxAttempts => _retryDelays.Length + 1;

    public async Task<string?> FindEmailAsync(Guid userId, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            throw new UserEmailLookupUnavailableException("auth-svc is not configured (AUTH_SERVICE_URL / AUTH_INTERNAL_TOKEN_FOR_NOTIF).");
        }

        for (var attempt = 1; ; attempt++)
        {
            var outcome = await SendOnceAsync(userId, ct);
            if (outcome.Kind == AttemptKind.Completed)
            {
                return outcome.Email;
            }

            if (outcome.Kind == AttemptKind.Rejected)
            {
                _logger.LogError(
                    "auth-svc rejected the internal service token (Status={StatusCode}). Check AUTH_INTERNAL_TOKEN_FOR_NOTIF. UserId={UserId}.",
                    (int)outcome.StatusCode, userId);
                throw new UserEmailLookupUnavailableException(
                    $"auth-svc rejected the internal token (status {(int)outcome.StatusCode}).");
            }

            if (attempt > _retryDelays.Length)
            {
                throw new UserEmailLookupUnavailableException(
                    $"auth-svc unavailable after {attempt} attempts: {outcome.Reason}.");
            }

            _logger.LogWarning(
                "auth-svc email lookup attempt {Attempt}/{MaxAttempts} failed ({Reason}). Retrying. UserId={UserId}.",
                attempt, MaxAttempts, outcome.Reason, userId);
            await Task.Delay(_retryDelays[attempt - 1], ct);
        }
    }

    private async Task<Attempt> SendOnceAsync(Guid userId, CancellationToken ct)
    {
        var url = $"{_options.BaseUrl}/api/internal/users/{userId:D}/email";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.InternalToken);

        try
        {
            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("auth-svc has no user for UserId={UserId}.", userId);
                return Attempt.Completed(null);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Attempt.Rejected(response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Attempt.Transient($"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<InternalUserEmailResponse>(JsonOptions, ct);
            if (body is null
                || body.UserId != userId
                || string.IsNullOrWhiteSpace(body.Email))
            {
                return Attempt.Transient("unexpected response body");
            }

            return Attempt.Completed(body.Email.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // HttpClient timeout (cancellation token not cancelled).
            return Attempt.Transient("timeout");
        }
        catch (HttpRequestException)
        {
            // Drop the inner exception: its message can contain the request URL.
            return Attempt.Transient("transport error");
        }
        catch (JsonException)
        {
            return Attempt.Transient("invalid JSON");
        }
    }

    /// <summary>Documented auth-svc response: <c>{ "userId": "&lt;guid&gt;", "email": "&lt;address&gt;" }</c>.</summary>
    internal sealed record InternalUserEmailResponse(Guid UserId, string Email);

    private enum AttemptKind
    {
        Completed,
        Transient,
        Rejected,
    }

    private readonly record struct Attempt(AttemptKind Kind, string? Email, HttpStatusCode StatusCode, string Reason)
    {
        public static Attempt Completed(string? email) => new(AttemptKind.Completed, email, default, string.Empty);

        public static Attempt Transient(string reason) => new(AttemptKind.Transient, null, default, reason);

        public static Attempt Rejected(HttpStatusCode status) => new(AttemptKind.Rejected, null, status, string.Empty);
    }
}
