using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Notif.Core.Interfaces;

namespace Notif.Infrastructure.Services;

/// <summary>
/// Telegram Bot API client (sendMessage over HTTPS). The bot token is part of the request path,
/// so this client is built on a plain <see cref="HttpClient"/> rather than IHttpClientFactory:
/// the factory's logging handler would write the full URL, token included, to the logs.
/// </summary>
public sealed class TelegramBotClient : ITelegramSender
{
    private readonly HttpClient _http;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramBotClient> _logger;

    public TelegramBotClient(HttpClient http, TelegramOptions options, ILogger<TelegramBotClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!_options.IsEnabled)
        {
            throw new InvalidOperationException("Telegram bot is not configured. Set TELEGRAM_BOT_TOKEN.");
        }

        var url = $"{_options.ApiBaseUrl}/bot{_options.BotToken}/sendMessage";
        using var content = JsonContent.Create(new { chat_id = chatId, text });

        HttpResponseMessage response;
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            response = await _http.SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        {
            // Drop the inner exception: its message can contain the request URL.
            throw new TelegramApiException(0, "transport error");
        }

        using (response)
        {
            var (ok, description) = ParseResult(body);
            if (!response.IsSuccessStatusCode || !ok)
            {
                _logger.LogWarning(
                    "Telegram sendMessage failed. Status={StatusCode} Description={Description}.",
                    (int)response.StatusCode, description);
                throw new TelegramApiException((int)response.StatusCode, description);
            }
        }
    }

    private static (bool Ok, string Description) ParseResult(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty
                : string.Empty;
            return (ok, description);
        }
        catch (JsonException)
        {
            return (false, "unparseable response");
        }
    }
}
