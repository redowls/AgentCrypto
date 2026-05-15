using TradingBot.AI.Models;

namespace TradingBot.AI.Abstractions;

/// <summary>
/// §5.4.2 — calls Claude every 4h per active symbol after the rule-based
/// classifier has run. Per S9 spec: if Claude disagrees with confidence
/// &gt; 0.7, persist BOTH and use Claude's verdict; otherwise keep the
/// rule-based output.
///
/// Rows are written to <c>dbo.Regimes</c> with <c>Source = 'RULE'</c> or
/// <c>'CLAUDE_CONFIRMED'</c>. On budget exhaustion the call returns a
/// <see cref="RegimeConfirmation"/> with <c>Source = "RULE"</c> (graceful
/// fallback) and writes nothing extra.
/// </summary>
public interface IRegimeConfirmer
{
    Task<RegimeConfirmation> ConfirmAsync(
        int               symbolId,
        RegimeSnapshot    snapshot,
        CancellationToken cancellationToken);
}
