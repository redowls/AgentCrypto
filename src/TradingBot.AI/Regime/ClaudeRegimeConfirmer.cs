using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Configuration;
using TradingBot.AI.Models;
using TradingBot.AI.Prompts;
using TradingBot.Core.Domain;
using TradingBot.Core.Indicators;
using CoreRegime = TradingBot.Core.Indicators.Regime;
using TradingBot.Data.Abstractions;

namespace TradingBot.AI.Regime;

/// <summary>
/// §5.4.2 — calls Claude with the regime-classifier prompt every 4h, compares
/// the verdict against the rule-based output, and persists per the S9 spec:
///   • Always writes a <c>Source = "RULE"</c> row first.
///   • If Claude disagrees AND its confidence &gt; <c>OverrideThreshold</c>,
///     also writes a <c>Source = "CLAUDE_CONFIRMED"</c> row and returns
///     Claude's verdict as final.
///   • Otherwise returns the rule verdict unchanged.
///
/// Budget exhaustion or call failure → return rule verdict (fallback path).
/// </summary>
internal sealed class ClaudeRegimeConfirmer : IRegimeConfirmer
{
    private readonly IClaudeClient        _claude;
    private readonly IServiceScopeFactory _scopes;
    private readonly RegimeConfirmerOptions _opt;
    private readonly ILogger<ClaudeRegimeConfirmer> _log;

    public ClaudeRegimeConfirmer(
        IClaudeClient        claude,
        IServiceScopeFactory scopes,
        IOptions<RegimeConfirmerOptions> options,
        ILogger<ClaudeRegimeConfirmer> log)
    {
        _claude = claude;
        _scopes = scopes;
        _opt    = options.Value;
        _log    = log;
    }

    public async Task<RegimeConfirmation> ConfirmAsync(
        int symbolId, RegimeSnapshot snapshot, CancellationToken cancellationToken)
    {
        // 1. Always persist the rule output first.
        await WriteRegimeAsync(symbolId, snapshot, snapshot.RuleRegime, snapshot.RuleConfidence,
            source: "RULE", reason: null, cancellationToken).ConfigureAwait(false);

        // 2. Ask Claude.
        AiResponse response;
        try
        {
            response = await _claude.SendAsync(
                purpose:        AiPurposes.Regime,
                systemPrompt:   SystemPrompts.Regime,
                userPrompt:     UserPromptRenderer.RegimeReadings(snapshot),
                cache:          CacheControl.Regime,
                jsonSchemaHint: null,
                modelOverride:  null,
                maxOutputTokens: 256,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AiBudgetExceededException || ex is HttpRequestException)
        {
            _log.LogWarning(ex, "Regime confirm fell back to RULE for symbol={SymbolId}", symbolId);
            return Rule(snapshot);
        }

        if (!TryParseRegime(response.Json, out var claudeRegime, out var claudeConf, out var reason))
        {
            _log.LogWarning("Claude regime response unparseable: {Json}", Truncate(response.Json, 256));
            return Rule(snapshot);
        }

        var disagrees = claudeRegime != snapshot.RuleRegime;
        var override_ = disagrees && claudeConf > _opt.OverrideThreshold;

        if (override_)
        {
            await WriteRegimeAsync(symbolId, snapshot, claudeRegime, claudeConf,
                source: "CLAUDE_CONFIRMED", reason, cancellationToken).ConfigureAwait(false);
            _log.LogInformation(
                "Regime override for symbol={SymbolId} rule={Rule}@{RuleConf} → claude={Claude}@{ClaudeConf}",
                symbolId, RegimeCodes.ToCode(snapshot.RuleRegime), snapshot.RuleConfidence,
                RegimeCodes.ToCode(claudeRegime), claudeConf);

            return new RegimeConfirmation(
                FinalRegime:      claudeRegime,
                FinalConfidence:  claudeConf,
                Source:           "CLAUDE_CONFIRMED",
                RuleRegime:       snapshot.RuleRegime,
                RuleConfidence:   snapshot.RuleConfidence,
                ClaudeRegime:     claudeRegime,
                ClaudeConfidence: claudeConf,
                ClaudeReason:     reason);
        }

        // Claude agreed (or disagreed below threshold) — keep rule.
        return new RegimeConfirmation(
            FinalRegime:      snapshot.RuleRegime,
            FinalConfidence:  snapshot.RuleConfidence,
            Source:           "RULE",
            RuleRegime:       snapshot.RuleRegime,
            RuleConfidence:   snapshot.RuleConfidence,
            ClaudeRegime:     claudeRegime,
            ClaudeConfidence: claudeConf,
            ClaudeReason:     reason);
    }

    private static RegimeConfirmation Rule(RegimeSnapshot s) => new(
        FinalRegime:      s.RuleRegime,
        FinalConfidence:  s.RuleConfidence,
        Source:           "RULE",
        RuleRegime:       s.RuleRegime,
        RuleConfidence:   s.RuleConfidence,
        ClaudeRegime:     null,
        ClaudeConfidence: null,
        ClaudeReason:     null);

    private async Task WriteRegimeAsync(
        int symbolId, RegimeSnapshot s, CoreRegime regime, decimal confidence,
        string source, string? reason, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRegimeRepository>();

        var row = new RegimeRecord
        {
            SymbolId   = symbolId,
            Interval   = s.Interval,
            AsOf       = s.AsOfUtc,
            Regime     = RegimeCodes.ToCode(regime),
            Confidence = confidence,
            Source     = source,
            Inputs     = JsonSerializer.Serialize(new
            {
                adx = s.Adx14, plus_di = s.PlusDi14, minus_di = s.MinusDi14,
                atr = s.Atr14, atr50 = s.Atr50Sma, atr_ratio = s.AtrRatio,
                bbw_pct = s.BbWidthPct, bbw_pct_50pctl = s.BbWidthPct50pctl,
                ema9 = s.Ema9, ema21 = s.Ema21, ema50 = s.Ema50, ema200 = s.Ema200,
                slope_pct = s.Last20BarSlopePct,
                rule_regime = RegimeCodes.ToCode(s.RuleRegime),
                rule_confidence = s.RuleConfidence,
                claude_reason = reason,
            }),
        };

        try
        {
            await repo.InsertAsync(row, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Persist regime row failed (symbol={SymbolId}, source={Source})", symbolId, source);
        }
    }

    /// <summary>Permissive parser for the §5.4.2 response shape.</summary>
    internal static bool TryParseRegime(string text, out CoreRegime regime, out decimal confidence, out string? reason)
    {
        regime = CoreRegime.Unknown;
        confidence = 0m;
        reason = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Strip optional fences/labels.
        var json = text.Trim();
        if (json.StartsWith("```"))
        {
            var firstNl = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNl > 0 && lastFence > firstNl)
                json = json.Substring(firstNl + 1, lastFence - firstNl - 1).Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("regime", out var r))
                regime = RegimeCodes.FromCode(r.GetString());
            if (root.TryGetProperty("confidence", out var c))
                confidence = c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : 0m;
            if (root.TryGetProperty("reason", out var rs))
                reason = rs.GetString();

            confidence = Math.Clamp(confidence, 0m, 1m);
            return regime != CoreRegime.Unknown;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
