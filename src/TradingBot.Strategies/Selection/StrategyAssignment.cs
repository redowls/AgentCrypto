using TradingBot.Strategies.Abstractions;

namespace TradingBot.Strategies.Selection;

/// <summary>
/// Routing decision for one (regime, strategy) pair: which strategy to run and
/// the §3.4 size multiplier the risk module applies if the signal is approved.
/// Pure data — the selector returns a list of these per regime call.
/// </summary>
/// <param name="Strategy">The eligible strategy implementation.</param>
/// <param name="SizeMultiplier">[0..1] — multiplier applied to the §8 risk-per-trade
/// pct (default per §3.4: full size for trending, full for ranging-MR, 0.5×
/// for volatile-breakout, 0× / not selected for compressing). The selector never
/// emits a strategy with multiplier 0 — it filters those out.</param>
public sealed record StrategyAssignment(IStrategy Strategy, decimal SizeMultiplier);
