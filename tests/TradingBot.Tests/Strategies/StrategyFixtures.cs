using TradingBot.Core.Domain;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Abstractions;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// Shared snapshot/MarketContext factory for the per-strategy golden tests.
/// Helpers build a fully-populated baseline so each test can override only the
/// field it cares about — keeps the per-test fixtures small and readable.
/// </summary>
internal static class StrategyFixtures
{
    public const int    SymbolId   = 42;
    public const string SymbolCode = "BTCUSDT";

    public static readonly DateTime BarOpen = new(2026, 04, 29, 12, 0, 0, DateTimeKind.Utc);

    public static IndicatorSnapshot SnapshotAllPopulated(
        DateTime? asOf            = null,
        decimal?  atr             = 100m,
        decimal?  ema9            = 100m,
        decimal?  ema21           = 100m,
        decimal?  ema50           = 100m,
        decimal?  ema200          = 100m,
        decimal?  adx             = 30m,
        decimal?  plusDi          = 30m,
        decimal?  minusDi         = 15m,
        decimal?  rsi             = 50m,
        decimal?  bbUpper         = 11_000m,
        decimal?  bbMid           = 10_000m,
        decimal?  bbLower         = 9_000m,
        decimal?  bbWidth         = 0.05m,
        decimal?  donchianUpper   = 11_000m,
        decimal?  donchianLower   = 9_000m,
        decimal?  vwap            = 10_000m,
        decimal?  atr50Sma        = 100m,
        decimal?  bbWidthSma50    = 0.05m,
        decimal?  bbWidthPctRank  = 0.5m,
        decimal?  bbWidthPrev     = 0.05m,
        decimal?  adxPrev         = 28m)
        => new(
            AsOfUtc: asOf ?? BarOpen,
            Atr14: atr,
            Ema9: ema9, Ema21: ema21, Ema50: ema50, Ema200: ema200,
            Adx14: adx, PlusDi14: plusDi, MinusDi14: minusDi,
            Rsi14: rsi,
            BbUpper: bbUpper, BbMid: bbMid, BbLower: bbLower, BbWidth: bbWidth,
            DonchianUpper: donchianUpper, DonchianLower: donchianLower,
            VwapSession: vwap,
            Atr50Sma: atr50Sma,
            BbWidthSma50: bbWidthSma50,
            BbWidthPercentileRank: bbWidthPctRank,
            BbWidthPrev: bbWidthPrev,
            AdxPrev: adxPrev);

    public static MarketContext CtxAllPopulated(
        decimal             barOpen        = 10_000m,
        decimal             barHigh        = 11_010m,
        decimal             barLow         = 9_900m,
        decimal             barClose       = 11_005m,
        decimal             barVolume      = 200m,
        decimal?            volumeSma20    = 100m,
        decimal?            close3BarsAgo  = 9_950m,
        IndicatorSnapshot?  priorSnapshot  = null,
        decimal?            htfBarClose    = 10_500m,
        DateTime?           barOpenTime    = null)
        => new(
            SymbolId:        SymbolId,
            SymbolCode:      SymbolCode,
            PrimaryInterval: "1h",
            BarOpenTime:     barOpenTime ?? BarOpen,
            BarOpen:         barOpen,
            BarHigh:         barHigh,
            BarLow:          barLow,
            BarClose:        barClose,
            BarVolume:       barVolume,
            VolumeSma20:     volumeSma20,
            Close3BarsAgo:   close3BarsAgo,
            PriorSnapshot:   priorSnapshot,
            HtfBarClose:     htfBarClose,
            NowUtc:          (barOpenTime ?? BarOpen).AddMilliseconds(50));
}
