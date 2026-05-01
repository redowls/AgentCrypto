using TradingBot.Core.Indicators;

namespace TradingBot.Strategies.Selection;

/// <summary>
/// §3.4 selector: given the current regime, returns the strategies allowed to
/// fire and their per-trade size multipliers. The SignalEngine evaluates each
/// returned strategy in turn; the §3.4 table also dictates when nothing should
/// run at all (Compressing / Unknown).
/// </summary>
public interface IStrategySelector
{
    /// <summary>
    /// Returns the active assignments for <paramref name="regime"/> in the order
    /// they should be evaluated (primary first). Empty list ⇒ no strategy is
    /// active in this regime — the SignalEngine should skip the bar.
    /// </summary>
    IReadOnlyList<StrategyAssignment> GetActive(Regime regime);
}
