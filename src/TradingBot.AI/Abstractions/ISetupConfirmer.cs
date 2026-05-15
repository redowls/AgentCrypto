using TradingBot.AI.Models;

namespace TradingBot.AI.Abstractions;

/// <summary>
/// §5.4.3 — called only when the rule-based composite confidence is
/// borderline ([0.5, 0.7]). The implementation enforces a 2-second wall-clock
/// timeout; on timeout (or budget-exhaustion) it returns the documented
/// fallback verdict {approve=true, size_adj=0.7, isFallback=true}.
/// </summary>
public interface ISetupConfirmer
{
    Task<SetupConfirmation> ConfirmAsync(
        SetupContext      context,
        CancellationToken cancellationToken);
}
