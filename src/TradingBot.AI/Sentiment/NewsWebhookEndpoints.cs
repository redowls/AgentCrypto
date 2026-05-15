using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingBot.AI.Configuration;
using TradingBot.AI.Models;

namespace TradingBot.AI.Sentiment;

/// <summary>
/// Maps the n8n-friendly <c>POST /newsfeed/push</c> endpoint that lets
/// external producers ship news headlines into the bot. The endpoint
/// accepts either a single item or a list. Auth is an optional shared
/// secret in <c>X-Webhook-Secret</c> (matched against
/// <see cref="NewsOptions.WebhookSharedSecret"/>); when the option is null
/// the endpoint accepts any caller — fine for local-only deployments
/// behind a private network, dangerous on the public internet.
/// </summary>
public static class NewsWebhookEndpoints
{
    public const string Route = "/newsfeed/push";

    public static IEndpointRouteBuilder MapNewsfeedPush(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, async (HttpContext ctx) =>
        {
            var opts   = ctx.RequestServices.GetRequiredService<IOptions<NewsOptions>>().Value;
            var source = ctx.RequestServices.GetRequiredService<InMemoryWebhookNewsSource>();

            if (!opts.EnableWebhook)
                return Results.NotFound();

            if (!string.IsNullOrEmpty(opts.WebhookSharedSecret))
            {
                var supplied = ctx.Request.Headers["X-Webhook-Secret"].ToString();
                if (!string.Equals(supplied, opts.WebhookSharedSecret, StringComparison.Ordinal))
                    return Results.Unauthorized();
            }

            WebhookNewsPayload? payload;
            try
            {
                payload = await ctx.Request.ReadFromJsonAsync<WebhookNewsPayload>(ctx.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException jex)
            {
                return Results.BadRequest(new { error = "Invalid JSON body", detail = jex.Message });
            }

            if (payload is null) return Results.BadRequest(new { error = "Empty body" });

            var items = payload.Normalize();
            if (items.Count == 0) return Results.BadRequest(new { error = "No items in payload" });

            foreach (var i in items) source.Enqueue(i);
            return Results.Accepted(value: new { accepted = items.Count });
        });

        return endpoints;
    }
}

/// Wire shape accepted at the webhook. A "single item" envelope and a "list of items"
/// envelope are both accepted; the producer (n8n / a curl smoke test) picks whichever
/// is convenient.
public sealed class WebhookNewsPayload
{
    [JsonPropertyName("source")]   public string?                Source { get; set; }
    [JsonPropertyName("ts")]       public DateTime?              Timestamp { get; set; }
    [JsonPropertyName("headline")] public string?                Headline { get; set; }
    [JsonPropertyName("items")]    public WebhookNewsItem[]?     Items { get; set; }

    public List<NewsItem> Normalize()
    {
        var result = new List<NewsItem>(Items?.Length ?? 1);

        if (!string.IsNullOrWhiteSpace(Headline))
        {
            result.Add(new NewsItem(
                TimestampUtc: Timestamp ?? DateTime.UtcNow,
                Source:       string.IsNullOrWhiteSpace(Source) ? "webhook" : Source!,
                Headline:     Headline!.Trim()));
        }

        if (Items is not null)
        {
            foreach (var i in Items)
            {
                if (string.IsNullOrWhiteSpace(i.Headline)) continue;
                result.Add(new NewsItem(
                    TimestampUtc: i.Timestamp ?? DateTime.UtcNow,
                    Source:       string.IsNullOrWhiteSpace(i.Source) ? (Source ?? "webhook") : i.Source!,
                    Headline:     i.Headline!.Trim()));
            }
        }
        return result;
    }
}

public sealed class WebhookNewsItem
{
    [JsonPropertyName("source")]   public string?    Source    { get; set; }
    [JsonPropertyName("ts")]       public DateTime?  Timestamp { get; set; }
    [JsonPropertyName("headline")] public string?    Headline  { get; set; }
}
