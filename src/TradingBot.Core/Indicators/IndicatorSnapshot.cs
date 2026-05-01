namespace TradingBot.Core.Indicators;

/// <summary>
/// Strategy-required indicator set for one (symbol, interval) at one bar close.
/// Field set is fixed by the strategy spec (§3) plus the regime classifier inputs (§3.4):
/// ATR(14), EMA(9/21/50/200), ADX(14) with directional indicators, RSI(14),
/// BB(20,2) with Width, Donchian(20), session VWAP, and the rolling context
/// values (ATR50_SMA, BBW_SMA50, BBW percentile rank, plus prior-bar ADX/BBW)
/// the regime classifier needs to detect "expanding", "rising", and "low percentile".
///
/// All values are nullable because the underlying series may not yet have
/// enough history for the look-back when the bot starts up mid-session.
/// </summary>
public sealed record IndicatorSnapshot(
    DateTime AsOfUtc,
    decimal? Atr14,
    decimal? Ema9,
    decimal? Ema21,
    decimal? Ema50,
    decimal? Ema200,
    decimal? Adx14,
    decimal? PlusDi14,
    decimal? MinusDi14,
    decimal? Rsi14,
    decimal? BbUpper,
    decimal? BbMid,
    decimal? BbLower,
    decimal? BbWidth,
    decimal? DonchianUpper,
    decimal? DonchianLower,
    decimal? VwapSession,
    decimal? Atr50Sma,
    decimal? BbWidthSma50,
    decimal? BbWidthPercentileRank,
    decimal? BbWidthPrev,
    decimal? AdxPrev);
