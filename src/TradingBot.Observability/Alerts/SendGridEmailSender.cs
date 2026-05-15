using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using TradingBot.Observability.Alerts.Configuration;

namespace TradingBot.Observability.Alerts;

/// <summary>
/// Posts HTML email via https://api.sendgrid.com/v3/mail/send. Retries
/// transient failures (HTTP 429 + 5xx + network) up to 3 times. The API key
/// is supplied at construction (loaded via ISecretsProvider) and set on the
/// HttpClient's default Authorization header.
/// </summary>
public sealed class SendGridEmailSender : IEmailSender
{
    private readonly HttpClient _http;
    private readonly SendGridOptions _opts;
    private readonly ResiliencePipeline<HttpResponseMessage> _retry;
    private readonly ILogger<SendGridEmailSender> _log;

    public SendGridEmailSender(
        HttpClient http,
        IOptions<SendGridOptions> opts,
        string apiKey,
        ILogger<SendGridEmailSender> log)
    {
        _http = http;
        _opts = opts.Value;
        _log  = log;
        _http.Timeout = TimeSpan.FromMilliseconds(_opts.RequestTimeoutMs);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

    public async Task SendAsync(string subject, string htmlBody, IEnumerable<string> to, CancellationToken cancellationToken)
    {
        var recipients = to.Select(addr => new { email = addr }).ToArray();
        if (recipients.Length == 0) return;

        var payload = JsonSerializer.Serialize(new
        {
            personalizations = new[] { new { to = recipients } },
            from    = new { email = _opts.From },
            subject = subject,
            content = new[] { new { type = "text/html", value = htmlBody } },
        });

        using var resp = await _retry.ExecuteAsync(async token =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v3/mail/send")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            return await _http.SendAsync(req, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _log.LogWarning("SendGrid send failed status={Status} body={Body}", (int)resp.StatusCode, body);
        }
    }
}
