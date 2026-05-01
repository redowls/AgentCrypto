using TradingBot.Core.Indicators;

namespace TradingBot.Strategies.Abstractions;

/// <summary>
/// Per-bar input that strategies need *in addition to* the indicator snapshot.
/// Carries the just-closed bar's OHLCV plus a couple of trailing aggregates
/// (volume SMA20, close 3 bars ago), the prior-bar indicator snapshot (needed
/// to detect §3.3's EMA9/21 crossover) and the latest HTF bar close (needed by
/// §3.3's HTF alignment) — all values that the §3.1/§3.2/§3.3 rules name
/// explicitly but that the canonical
/// <see cref="TradingBot.Core.Indicators.IndicatorSnapshot"/> does not.
///
/// The SignalEngine populates this from the IndicatorPreCacheService's rolling
/// quote window — keeping strategies pure functions that touch no IO.
/// </summary>
/// <param name="SymbolId">FK to <c>dbo.Symbols.SymbolId</c>; required for persistence.</param>
/// <param name="SymbolCode">Ticker (e.g. <c>BTCUSDT</c>); used in log messages.</param>
/// <param name="PrimaryInterval">Interval of the bar that just closed (e.g. <c>1h</c>).</param>
/// <param name="BarOpenTime">UTC OpenTime of the just-closed bar.</param>
/// <param name="BarOpen">Open price of the bar.</param>
/// <param name="BarHigh">High price of the bar.</param>
/// <param name="BarLow">Low price of the bar.</param>
/// <param name="BarClose">Close price — used as the synthetic entry price.</param>
/// <param name="BarVolume">Volume of the bar — Donchian breakout's volume gate.</param>
/// <param name="VolumeSma20">SMA(20) of bar volumes including the just-closed bar.
/// Null when the rolling window is shorter than 20 bars.</param>
/// <param name="Close3BarsAgo">Close of the bar 3 closes back. Used by §3.2 mean
/// reversion's "Close > Close[3]" micro-confirm. Null when fewer than 4 closed
/// bars are available.</param>
/// <param name="PriorSnapshot">Indicator snapshot one bar back on the same TF.
/// Strategies that need a *change-of-state* read (notably §3.3's EMA9 crossing
/// EMA21) consult this. Null when the prior bar isn't available (cold start).</param>
/// <param name="HtfBarClose">Close of the latest closed bar on the strategy's
/// higher timeframe (§3.3: 4h). Used together with the HTF snapshot's EMA200
/// to test the "Close_HTF &gt; EMA200_HTF" alignment rule. Null when the HTF
/// data isn't available or the strategy is single-TF.</param>
/// <param name="NowUtc">Current wall-clock time (typically the persistor's clock).
/// Strategies may use this for time-based logging, never for I/O.</param>
public sealed record MarketContext(
    int                SymbolId,
    string             SymbolCode,
    string             PrimaryInterval,
    DateTime           BarOpenTime,
    decimal            BarOpen,
    decimal            BarHigh,
    decimal            BarLow,
    decimal            BarClose,
    decimal            BarVolume,
    decimal?           VolumeSma20,
    decimal?           Close3BarsAgo,
    IndicatorSnapshot? PriorSnapshot,
    decimal?           HtfBarClose,
    DateTime           NowUtc)
{
    /// <summary>Convenience: bar range (High − Low). Used by §3.3's
    /// IsExplosiveBar gate.</summary>
    public decimal BarRange => BarHigh - BarLow;
}
