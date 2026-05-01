using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Brackets;
using TradingBot.Strategies.Configuration;

namespace TradingBot.Strategies.Strategies;

/// <summary>
/// §3.1 — Donchian Breakout with Volume Confirmation (BREAKOUT_DON).
///
/// Long entry (per the design doc):
///   Close &gt; Donchian.Upper(20)[1]                   — break of prior 20-bar high
///   AND Volume ≥ VolumeMultiplier × SMA(Volume, 20)   — volume confirmation
///   AND Close &gt; EMA(Close, 200)                     — macro-trend filter
///   AND ADX(14) &gt; AdxThreshold                       — regime is trending/expanding
///
/// Short entry mirrors with the lower band.
///
/// Brackets: SL = entry − 1.5×ATR, TP = entry + 3×ATR (1:2 RR), with the §4.2
/// volatility adjustment applied to TP. Trail: Chandelier(22, 3×ATR) once
/// +1.5R favorable. Hard time stop: 24 bars.
///
/// Implementation note on the "prior 20-bar high" check: the IndicatorSnapshot
/// already carries Donchian(20) at the bar close — i.e. <c>DonchianUpper</c>
/// reflects the highest of the most-recent 20 *closed* bars. The §3.1 rule
/// reads "Close &gt; upperBand[1]" — the band one bar back. To replicate that
/// without extra plumbing we use the snapshot's <c>DonchianUpper</c> as the
/// "prior" band reference and require strict inequality with bar close (the
/// snapshot is computed on the now-closed bar, so any new break shows up here
/// as Close ≥ DonchianUpper). To prevent same-bar self-confirmation, we
/// additionally require <see cref="MarketContext.BarHigh"/> ≥ DonchianUpper —
/// i.e. the breakout actually occurred during the bar and isn't an artifact
/// of equal-close arithmetic.
/// </summary>
public sealed class BreakoutDonchianStrategy : IStrategy
{
    private readonly IOptionsMonitor<BreakoutDonchianOptions> _options;
    private readonly ILogger<BreakoutDonchianStrategy> _log;

    public BreakoutDonchianStrategy(
        IOptionsMonitor<BreakoutDonchianOptions> options,
        ILogger<BreakoutDonchianStrategy> log)
    {
        _options = options;
        _log = log;
    }

    public string Name => StrategyCodes.BreakoutDonchian;
    public string PrimaryTimeframe => _options.CurrentValue.PrimaryTimeframe;
    public string? HigherTimeframe => null;
    public StrategyType StrategyType => StrategyType.Breakout;

    public Regime[] AllowedRegimes => new[]
    {
        Regime.TrendingUp,
        Regime.TrendingDown,
        Regime.Volatile,
    };

    public SignalCandidate? Evaluate(
        IndicatorSnapshot  snap,
        IndicatorSnapshot? htf,
        Regime             regime,
        MarketContext      ctx)
    {
        var o = _options.CurrentValue;
        if (!o.Enabled)
        {
            _log.LogDebug("BREAKOUT_DON disabled by options for {Symbol}@{Bar}", ctx.SymbolCode, ctx.BarOpenTime);
            return null;
        }

        // Required snapshot fields. Anything null ⇒ warm-up incomplete.
        if (snap.DonchianUpper is not decimal donchHi ||
            snap.DonchianLower is not decimal donchLo ||
            snap.Ema200        is not decimal ema200  ||
            snap.Adx14         is not decimal adx     ||
            snap.Atr14         is not decimal atr)
        {
            _log.LogDebug(
                "BREAKOUT_DON {Symbol}@{Bar}: snapshot incomplete (donchHi={H}, donchLo={L}, ema200={E}, adx={A}, atr={T})",
                ctx.SymbolCode, ctx.BarOpenTime,
                snap.DonchianUpper, snap.DonchianLower, snap.Ema200, snap.Adx14, snap.Atr14);
            return null;
        }

        // Volume gate input: SMA(volume, 20) is provided in MarketContext.
        // We don't allow null here — volume confirmation is non-optional in §3.1.
        if (ctx.VolumeSma20 is not decimal volSma)
        {
            _log.LogDebug("BREAKOUT_DON {Symbol}@{Bar}: volume SMA20 not yet available", ctx.SymbolCode, ctx.BarOpenTime);
            return null;
        }

        // Direction by regime (TrendingUp ⇒ long-bias; TrendingDown ⇒ short-bias;
        // Volatile ⇒ both directions allowed, decided by which band breaks).
        var (longBreak, shortBreak) = ResolveBreakDirection(regime);

        // Long-side gates ----------------------------------------------------
        var longBreakout = ctx.BarHigh >= donchHi && ctx.BarClose > donchHi;
        var longTrend    = ctx.BarClose > ema200;
        var volumeOk     = ctx.BarVolume >= o.VolumeMultiplier * volSma;
        var adxOk        = adx > o.AdxThreshold;

        // Short-side gates ---------------------------------------------------
        var shortBreakout = ctx.BarLow <= donchLo && ctx.BarClose < donchLo;
        var shortTrend    = ctx.BarClose < ema200;

        _log.LogDebug(
            "BREAKOUT_DON {Symbol}@{Bar} gates: regime={Reg} longBreak={LB} shortBreak={SB} longTrend={LT} shortTrend={ST} volOk={V} adxOk={A} (adx={Adx:F2}, vol={Vol}, volSma={VS}, donchU={DU}, donchL={DL}, ema200={E})",
            ctx.SymbolCode, ctx.BarOpenTime, regime,
            longBreakout, shortBreakout, longTrend, shortTrend, volumeOk, adxOk,
            adx, ctx.BarVolume, volSma, donchHi, donchLo, ema200);

        // All-or-nothing entry. The volume + ADX gates are direction-agnostic.
        if (longBreak && longBreakout && longTrend && volumeOk && adxOk)
        {
            return BuildCandidate(BracketSide.Long, ctx.BarClose, atr, snap.Atr50Sma, adx, ctx, o,
                $"Donchian-Up break, close={ctx.BarClose}, donchU={donchHi}, vol={ctx.BarVolume}/{o.VolumeMultiplier:F2}×SMA20={volSma}, ADX={adx:F1}");
        }
        if (shortBreak && shortBreakout && shortTrend && volumeOk && adxOk)
        {
            return BuildCandidate(BracketSide.Short, ctx.BarClose, atr, snap.Atr50Sma, adx, ctx, o,
                $"Donchian-Down break, close={ctx.BarClose}, donchL={donchLo}, vol={ctx.BarVolume}/{o.VolumeMultiplier:F2}×SMA20={volSma}, ADX={adx:F1}");
        }

        return null;
    }

    private static (bool longBreak, bool shortBreak) ResolveBreakDirection(Regime regime) => regime switch
    {
        Regime.TrendingUp   => (true,  false),
        Regime.TrendingDown => (false, true),
        Regime.Volatile     => (true,  true),
        _                   => (false, false), // should never happen — selector filters
    };

    private SignalCandidate BuildCandidate(
        BracketSide side,
        decimal     entry,
        decimal     atr,
        decimal?    atr50,
        decimal     adx,
        MarketContext ctx,
        BreakoutDonchianOptions o,
        string      reason)
    {
        var bracket = BracketCalculator.Compute(
            entry, atr, side, o.AtrSlMultiplier, o.AtrTpMultiplier, atr50);

        // ADX strength → confidence floor. ADX 20 ⇒ 0.5; ADX 40+ ⇒ 1.0.
        var confidence = Clamp01(0.5m + (adx - o.AdxThreshold) / 40m);

        var trail = new TrailSpec(
            Mode:                    TrailMode.Chandelier,
            ChandelierLookback:      o.TrailChandelierLookback,
            ChandelierAtrMultiplier: o.TrailChandelierAtrMultiplier,
            ActivationRMultiple:     o.TrailActivationRMultiple);

        return new SignalCandidate(
            StrategyType: StrategyType.Breakout,
            StrategyCode: StrategyCodes.BreakoutDonchian,
            Side:         side.ToSideCode(),
            EntryPrice:   entry,
            StopLoss:     bracket.StopLoss,
            TakeProfit:   bracket.TakeProfit,
            AtrValue:     atr,
            Confidence:   confidence,
            Reason:       reason,
            Trail:        trail,
            TimeStopBars: o.TimeStopBars);
    }

    private static decimal Clamp01(decimal v) => v < 0m ? 0m : v > 1m ? 1m : v;
}
