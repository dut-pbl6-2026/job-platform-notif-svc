using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Services;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// Contract and unit tests for the auth-svc internal email lookup. The HTTP layer is a scripted
/// handler, so no network is used. The token below is a test value, not a secret.
/// </summary>
public class AuthUserClientTests
{
    private const string BaseUrl = "http://auth-svc:5001";
    private const string Token = "internal-test-token";
    private static readonly TimeSpan[] NoDelay = { TimeSpan.Zero, TimeSpan.Zero };

    private static readonly Guid UserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static AuthUserClient Build(ScriptedHttpHandler handler, AuthServiceOptions? options = null)
        => new(
            new HttpClient(handler),
            options ?? new AuthServiceOptions(BaseUrl, Token),
            NullLogger<AuthUserClient>.Instance,
            NoDelay);

    private static string Body(Guid userId, string email)
        => JsonSerializer.Serialize(new { userId, email });

    [Fact]
    public async Task Contract_ParsesDocumentedResponseShape()
    {
        var handler = new ScriptedHttpHandler().Respond(HttpStatusCode.OK, Body(UserId, "applicant@example.test"));

        var email = await Build(handler).FindEmailAsync(UserId);

        Assert.Equal("applicant@example.test", email);
    }

    [Fact]
    public async Task Contract_AcceptsPascalCaseKeys()
    {
        var json = $"{{\"UserId\":\"{UserId}\",\"Email\":\"applicant@example.test\"}}";
        var handler = new ScriptedHttpHandler().Respond(HttpStatusCode.OK, json);

        var email = await Build(handler).FindEmailAsync(UserId);

        Assert.Equal("applicant@example.test", email);
    }

    [Fact]
    public async Task Request_CallsInternalPathDirectly_WithBearerToken()
    {
        var handler = new ScriptedHttpHandler().Respond(HttpStatusCode.OK, Body(UserId, "a@example.test"));

        await Build(handler).FindEmailAsync(UserId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{BaseUrl}/api/internal/users/{UserId:D}/email", request.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", Token), request.Headers.Authorization);
    }

    [Fact]
    public async Task NotFound_ReturnsNull_WithoutRetry()
    {
        var handler = new ScriptedHttpHandler().Respond(HttpStatusCode.NotFound);

        var email = await Build(handler).FindEmailAsync(UserId);

        Assert.Null(email);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_ThrowsUnavailable_WithoutRetry()
    {
        var handler = new ScriptedHttpHandler().Respond(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<UserEmailLookupUnavailableException>(() => Build(handler).FindEmailAsync(UserId));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ServerErrors_AreRetried_ThenThrowUnavailable()
    {
        var handler = new ScriptedHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<UserEmailLookupUnavailableException>(() => Build(handler).FindEmailAsync(UserId));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task TransientFailure_ThenSuccess_ReturnsEmail()
    {
        var handler = new ScriptedHttpHandler()
            .Throw(new HttpRequestException("connection refused"))
            .Respond(HttpStatusCode.OK, Body(UserId, "retry@example.test"));

        var email = await Build(handler).FindEmailAsync(UserId);

        Assert.Equal("retry@example.test", email);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Timeout_IsTransient()
    {
        var handler = new ScriptedHttpHandler()
            .Throw(new TaskCanceledException("timeout"))
            .Throw(new TaskCanceledException("timeout"))
            .Throw(new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<UserEmailLookupUnavailableException>(() => Build(handler).FindEmailAsync(UserId));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task BlankEmail_IsTreatedAsInvalidResponse_NotAsFound()
    {
        var handler = new ScriptedHttpHandler()
            .Respond(HttpStatusCode.OK, Body(UserId, "  "))
            .Respond(HttpStatusCode.OK, Body(UserId, "  "))
            .Respond(HttpStatusCode.OK, Body(UserId, "  "));

        await Assert.ThrowsAsync<UserEmailLookupUnavailableException>(() => Build(handler).FindEmailAsync(UserId));
    }

    [Fact]
    public async Task ResponseForAnotherUser_IsRejected()
    {
        var handler = new ScriptedHttpHandler()
            .Respond(HttpStatusCode.OK, Body(Guid.NewGuid(), "other@example.test"))
            .Respond(HttpStatusCode.OK, Body(Guid.NewGuid(), "other@example.test"))
            .Respond(HttpStatusCode.OK, Body(Guid.NewGuid(), "other@example.test"));

        await Assert.ThrowsAsync<UserEmailLookupUnavailableException>(() => Build(handler).FindEmailAsync(UserId));
    }

    [Fact]
    public async Task NotConfigured_ThrowsUnavailable_WithoutCallingAuth()
    {
        var handler = new ScriptedHttpHandler();
        var client = Build(handler, new AuthServiceOptions(string.Empty, string.Empty));

        await Assert.ThrowsAsync<UserEmailLookupUnavailableException>(() => client.FindEmailAsync(UserId));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void MaxAttempts_IsRetryDelaysPlusOne()
    {
        Assert.Equal(3, Build(new ScriptedHttpHandler()).MaxAttempts);
    }
}
