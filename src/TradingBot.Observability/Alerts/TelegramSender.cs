using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using TradingBot.Observability.Alerts.Configuration;

namespace TradingBot.Observability.Alerts;

/// <summary>
/// Posts alerts to https://api.telegram.org/bot{TOKEN}/sendMessage. Retries
/// transient failures (HTTP 429 + 5xx + network) up to 3 times with
/// exponential backoff. The bot token is supplied at construction (loaded via
/// ISecretsProvider during DI wiring); no token ever appears in log lines.
/// </summary>
public sealed class TelegramSender : ITelegramSender
{
    private readonly HttpClient _http;
    private readonly string _botToken;
    private readonly ResiliencePipeline<HttpResponseMessage> _retry;
    private readonly ILogger<TelegramSender> _log;

    public TelegramSender(
        HttpClient http,
        IOptions<TelegramOptions> opts,
        string botToken,
        ILogger<TelegramSender> log)
    {
        _http = http;
        _botToken = botToken;
        _log = log;
        _http.Timeout = TimeSpan.FromMilliseconds(opts.Value.RequestTimeoutMs);
        _retry = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r => (int)r.StatusCode == 429 || (int)r.StatusCode >= 500),
            })
            .Build();
    }

    public async Task SendAsync(string chatId, string markdownBody, CancellationToken cancellationToken)
    {
        var url = $"/bot{_botToken}/sendMessage";
        var payload = JsonSerializer.Serialize(new
        {
            chat_id    = chatId,
            text       = markdownBody,
            parse_mode = "Markdown",
            disable_web_page_preview = true,
        });

        using var resp = await _retry.ExecuteAsync(async token =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            return await _http.SendAsync(req, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _log.LogWarning("Telegram send failed status={Status} body={Body}", (int)resp.StatusCode, body);
        }
    }
}
