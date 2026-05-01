using TradingBot.Core.Indicators;

namespace TradingBot.Strategies.Abstractions;

/// <summary>
/// One concrete trading rule per §3.1 / §3.2 / §3.3 of the design doc.
/// Strategies are pure functions of their inputs — no IO, no DB calls inside
/// <see cref="Evaluate"/>. The SignalEngine prepares the inputs once per bar
/// close and runs every eligible strategy in turn.
///
/// Implementations also drive the §4.2 SL/TP math via <c>BracketCalculator</c>
/// and emit a <see cref="TrailSpec"/> when the strategy uses a trailing stop.
/// </summary>
public interface IStrategy
{
    /// <summary>Persistence code (e.g. <c>"BREAKOUT_DON"</c>) — also written to
    /// <c>dbo.Signals.Strategy</c>.</summary>
    string Name { get; }

    /// <summary>Bar interval the strategy primarily evaluates against
    /// (e.g. <c>1h</c> for §3.1 / §3.3, <c>15m</c> for §3.2).</summary>
    string PrimaryTimeframe { get; }

    /// <summary>Optional higher timeframe required for HTF alignment (§3.3:
    /// <c>4h</c> for the EMA200 filter). Null when the strategy is single-TF.</summary>
    string? HigherTimeframe { get; }

    /// <summary>Regimes in which this strategy is allowed to fire (§3.4 table).
    /// The <see cref="StrategySelector"/> uses this both to gate routing and as
    /// a defence-in-depth check inside the strategy itself.</summary>
    Regime[] AllowedRegimes { get; }

    /// <summary>Strategy family — selects bracket multipliers in §4.2.</summary>
    StrategyType StrategyType { get; }

    /// <summary>
    /// Evaluate the strategy at one bar close. Returns the candidate when ALL
    /// gating conditions pass; <c>null</c> otherwise. Implementations log every
    /// gating boolean at DEBUG so a reviewer can reconstruct rejections.
    /// </summary>
    /// <param name="snapshot">Snapshot at the just-closed bar of <see cref="PrimaryTimeframe"/>.</param>
    /// <param name="htfSnapshot">Snapshot at the most recent <see cref="HigherTimeframe"/>
    /// bar — null when the strategy doesn't require HTF or when the HTF window is
    /// still warming up. Strategies that name HTF alignment as required must treat
    /// null as a fail.</param>
    /// <param name="regime">Current regime classification at this bar. The selector
    /// has already filtered to allowed regimes; the strategy may still inspect it
    /// (e.g. choose direction from TrendingUp vs TrendingDown).</param>
    /// <param name="ctx">Bar OHLCV + trailing aggregates the snapshot doesn't cover.</param>
    SignalCandidate? Evaluate(
        IndicatorSnapshot  snapshot,
        IndicatorSnapshot? htfSnapshot,
        Regime             regime,
        MarketContext      ctx);
}
