using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Notif.Infrastructure.Services;
using Xunit;

namespace Notif.Tests;

/// <summary>
/// PBL6-63: Telegram Bot API client. HTTP is mocked with a stub handler; no real network calls.
/// </summary>
public class TelegramBotClientTests
{
    private const string Token = "123456:AAFakeTokenForTestsOnly";
    private const string BaseUrl = "https://telegram.test.invalid";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<(Uri? Url, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri, body));
            return _respond(request);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException($"Connection refused for {request.RequestUri}");
    }

    private static TelegramOptions Options() => new(Token, "secret_value", BaseUrl);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static TelegramBotClient Client(HttpMessageHandler handler, TelegramOptions? options = null)
        => new(new HttpClient(handler), options ?? Options(), NullLogger<TelegramBotClient>.Instance);

    [Fact]
    public async Task SendMessage_OnSuccess_PostsToBotPathWithChatIdAndText()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"ok\":true,\"result\":{}}"));

        await Client(handler).SendMessageAsync(42, "hello");

        var (url, body) = Assert.Single(handler.Requests);
        Assert.Equal($"{BaseUrl}/bot{Token}/sendMessage", url!.ToString());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(42L, doc.RootElement.GetProperty("chat_id").GetInt64());
        Assert.Equal("hello", doc.RootElement.GetProperty("text").GetString());
        Assert.False(doc.RootElement.TryGetProperty("parse_mode", out _));
    }

    [Fact]
    public async Task SendMessage_OnHttpServerError_ThrowsTelegramApiExceptionWithStatus()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.InternalServerError,
            "{\"ok\":false,\"error_code\":500,\"description\":\"Internal Server Error\"}"));

        var ex = await Assert.ThrowsAsync<TelegramApiException>(() => Client(handler).SendMessageAsync(42, "hi"));

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Internal Server Error", ex.Description);
    }

    [Fact]
    public async Task SendMessage_WhenOkIsFalseOnSuccessStatus_Throws()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"ok\":false,\"description\":\"chat not found\"}"));

        var ex = await Assert.ThrowsAsync<TelegramApiException>(() => Client(handler).SendMessageAsync(42, "hi"));

        Assert.Equal("chat not found", ex.Description);
    }

    [Fact]
    public async Task SendMessage_WhenBotBlockedByUser_ThrowsWithTelegramDescription()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden,
            "{\"ok\":false,\"error_code\":403,\"description\":\"Forbidden: bot was blocked by the user\"}"));

        var ex = await Assert.ThrowsAsync<TelegramApiException>(() => Client(handler).SendMessageAsync(42, "hi"));

        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task SendMessage_WhenResponseIsNotJson_Throws()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadGateway, "<html>bad gateway</html>"));

        var ex = await Assert.ThrowsAsync<TelegramApiException>(() => Client(handler).SendMessageAsync(42, "hi"));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task SendMessage_OnTransportError_ThrowsWithoutLeakingToken()
    {
        var client = Client(new ThrowingHandler());

        var ex = await Assert.ThrowsAsync<TelegramApiException>(() => client.SendMessageAsync(42, "hi"));

        Assert.Equal(0, ex.StatusCode);
        Assert.DoesNotContain(Token, ex.ToString());
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task SendMessage_WhenDisabled_ThrowsWithoutCallingApi()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"ok\":true}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(handler, TelegramOptions.Disabled).SendMessageAsync(42, "hi"));

        Assert.Empty(handler.Requests);
    }
}
