using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Configuration;
using TradingBot.AI.Models;
using TradingBot.AI.Prompts;

namespace TradingBot.AI.Setup;

/// <summary>
/// §5.4.3 — called only when rule confidence is borderline ([0.5, 0.7]).
/// Hard 2-second wall-clock timeout (configurable). On timeout, budget
/// exhaustion, network error, or unparseable response, the documented
/// fallback verdict {approve=true, size_adj=0.7, isFallback=true} is
/// returned so the trade proceeds at reduced size — degraded but functional.
/// </summary>
internal sealed class ClaudeSetupConfirmer : ISetupConfirmer
{
    private readonly IClaudeClient        _claude;
    private readonly SetupConfirmerOptions _opt;
    private readonly ILogger<ClaudeSetupConfirmer> _log;

    public ClaudeSetupConfirmer(
        IClaudeClient        claude,
        IOptions<SetupConfirmerOptions> options,
        ILogger<ClaudeSetupConfirmer> log)
    {
        _claude = claude;
        _opt    = options.Value;
        _log    = log;
    }

    public async Task<SetupConfirmation> ConfirmAsync(SetupContext context, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_opt.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        AiResponse response;
        try
        {
            response = await _claude.SendAsync(
                purpose:        AiPurposes.Confirmation,
                systemPrompt:   SystemPrompts.SetupConfirmer,
                userPrompt:     UserPromptRenderer.SetupReview(context),
                cache:          CacheControl.Confirmation,
                jsonSchemaHint: null,
                modelOverride:  null,
                maxOutputTokens: 384,
                cancellationToken: linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _log.LogWarning("Setup confirm timed out after {Ms}ms — defaulting APPROVE size_adj={Adj}",
                _opt.Timeout.TotalMilliseconds, _opt.FallbackSizeAdj);
            return Fallback();
        }
        catch (Exception ex) when (ex is AiBudgetExceededException || ex is HttpRequestException)
        {
            _log.LogWarning(ex, "Setup confirm fell back to APPROVE size_adj={Adj}", _opt.FallbackSizeAdj);
            return Fallback();
        }

        if (!TryParse(response.Json, out var verdict))
        {
            _log.LogWarning("Setup confirmer response unparseable; falling back. Body={Body}",
                Truncate(response.Json, 256));
            return Fallback();
        }

        return verdict;
    }

    private SetupConfirmation Fallback() => new(
        Approve:    true,
        Confidence: 0.5m,
        Concerns:   Array.Empty<string>(),
        SizeAdj:    _opt.FallbackSizeAdj,
        IsFallback: true);

    /// <summary>Permissive parser for the §5.4.3 schema.</summary>
    internal static bool TryParse(string text, out SetupConfirmation verdict)
    {
        verdict = new SetupConfirmation(true, 0.5m, Array.Empty<string>(), 0.7m, IsFallback: true);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var json = StripFences(text.Trim());

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var approve  = root.TryGetProperty("approve", out var a) && a.ValueKind == JsonValueKind.True;
            var conf     = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                            ? c.GetDecimal() : 0.5m;
            var sizeAdj  = root.TryGetProperty("size_adj", out var sa) && sa.ValueKind == JsonValueKind.Number
                            ? sa.GetDecimal() : 1.0m;
            var concerns = new List<string>();
            if (root.TryGetProperty("concerns", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in cs.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                        concerns.Add(el.GetString()!);
                }
            }

            sizeAdj = Math.Clamp(sizeAdj, 0.5m, 1.0m);
            conf    = Math.Clamp(conf,    0m,   1m);

            verdict = new SetupConfirmation(approve, conf, concerns, sizeAdj, IsFallback: false);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string StripFences(string s)
    {
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
        return firstNl > 0 && lastFence > firstNl ? s.Substring(firstNl + 1, lastFence - firstNl - 1).Trim() : s;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
