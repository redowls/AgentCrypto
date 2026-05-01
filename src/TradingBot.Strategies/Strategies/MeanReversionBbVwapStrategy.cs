using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Brackets;
using TradingBot.Strategies.Configuration;

namespace TradingBot.Strategies.Strategies;

/// <summary>
/// §3.2 — Mean Reversion with RSI + Bollinger + VWAP (MR_BB_VWAP).
///
/// Long entry (per the design doc):
///   Close &lt; LowerBB                                 — price stretched below band
///   AND RSI(14) &lt; RsiOversold                        — momentum oversold
///   AND Close &gt; VWAP × (1 − VwapBufferPct)           — still within "value zone"
///   AND ADX(14) &lt; AdxMaxThreshold                    — regime is ranging
///   AND Close &gt; Close[MicroConfirmLookbackBars]      — 3-bar reversal beginning
///
/// Short entry mirrors with the upper band, RSI &gt; RsiOverbought, and
/// Close &lt; VWAP × (1 + VwapBufferPct), Close &lt; Close[3].
///
/// Brackets: SL = entry ± 1.0×ATR, TP = entry ± 1.5×ATR (TP1 = §4.3 default).
/// The doc names a TP1 at VWAP (50% off) and TP2 at +1.5R; the candidate carries
/// the §4.3 TP. The position manager owns the partial-take split.
/// Trail: none (§4.3 — fast in/out).
/// Hard time stop: 8 bars (= 2h on 15m).
/// </summary>
public sealed class MeanReversionBbVwapStrategy : IStrategy
{
    private readonly IOptionsMonitor<MeanReversionBbVwapOptions> _options;
    private readonly ILogger<MeanReversionBbVwapStrategy> _log;

    public MeanReversionBbVwapStrategy(
        IOptionsMonitor<MeanReversionBbVwapOptions> options,
        ILogger<MeanReversionBbVwapStrategy> log)
    {
        _options = options;
        _log = log;
    }

    public string Name => StrategyCodes.MeanReversionBbVwap;
    public string PrimaryTimeframe => _options.CurrentValue.PrimaryTimeframe;
    public string? HigherTimeframe => null;
    public StrategyType StrategyType => StrategyType.MeanReversion;

    /// <summary>§3.4: MR is restricted to the Ranging regime — trend modes whipsaw it.</summary>
    public Regime[] AllowedRegimes => new[] { Regime.Ranging };

    public SignalCandidate? Evaluate(
        IndicatorSnapshot  snap,
        IndicatorSnapshot? htf,
        Regime             regime,
        MarketContext      ctx)
    {
        var o = _options.CurrentValue;
        if (!o.Enabled)
        {
            _log.LogDebug("MR_BB_VWAP disabled by options for {Symbol}@{Bar}", ctx.SymbolCode, ctx.BarOpenTime);
            return null;
        }

        // Required snapshot fields. Any null ⇒ warm-up incomplete; bail.
        if (snap.BbUpper is not decimal bbUp ||
            snap.BbLower is not decimal bbLo ||
            snap.Rsi14   is not decimal rsi  ||
            snap.Adx14   is not decimal adx  ||
            snap.Atr14   is not decimal atr  ||
            snap.VwapSession is not decimal vwap)
        {
            _log.LogDebug(
                "MR_BB_VWAP {Symbol}@{Bar}: snapshot incomplete (bbU={U}, bbL={L}, rsi={R}, adx={A}, atr={T}, vwap={V})",
                ctx.SymbolCode, ctx.BarOpenTime,
                snap.BbUpper, snap.BbLower, snap.Rsi14, snap.Adx14, snap.Atr14, snap.VwapSession);
            return null;
        }

        // ADX cap is direction-agnostic.
        var adxOk = adx < o.AdxMaxThreshold;

        // Long-side gates -----------------------------------------------------
        var longBbStretch = ctx.BarClose < bbLo;
        var longRsiOversold = rsi < o.RsiOversold;
        // VWAP "value zone" for longs: close still within (1 − buffer) × VWAP.
        // Below that, we'd be catching a falling knife.
        var longVwapZone = ctx.BarClose > vwap * (1m - o.VwapBufferPct);
        var longMicroConfirmOk = !o.RequireMicroConfirm
            || (ctx.Close3BarsAgo is decimal c3Long && ctx.BarClose > c3Long);

        // Short-side gates ----------------------------------------------------
        var shortBbStretch = ctx.BarClose > bbUp;
        var shortRsiOverbought = rsi > o.RsiOverbought;
        var shortVwapZone = ctx.BarClose < vwap * (1m + o.VwapBufferPct);
        var shortMicroConfirmOk = !o.RequireMicroConfirm
            || (ctx.Close3BarsAgo is decimal c3Short && ctx.BarClose < c3Short);

        _log.LogDebug(
            "MR_BB_VWAP {Symbol}@{Bar} gates: regime={Reg} longBB={LBB} longRsi={LR} longVwap={LV} longMicro={LM} shortBB={SBB} shortRsi={SR} shortVwap={SV} shortMicro={SM} adxOk={ADX} (rsi={Rsi:F1}, adx={Adx:F1}, close={C}, bbU={U}, bbL={L}, vwap={Vwap})",
            ctx.SymbolCode, ctx.BarOpenTime, regime,
            longBbStretch, longRsiOversold, longVwapZone, longMicroConfirmOk,
            shortBbStretch, shortRsiOverbought, shortVwapZone, shortMicroConfirmOk,
            adxOk, rsi, adx, ctx.BarClose, bbUp, bbLo, vwap);

        if (longBbStretch && longRsiOversold && longVwapZone && longMicroConfirmOk && adxOk)
        {
            return BuildCandidate(BracketSide.Long, ctx.BarClose, atr, snap.Atr50Sma, rsi, ctx, o,
                $"MR-Up: close={ctx.BarClose}<bbL={bbLo}, RSI={rsi:F1}<{o.RsiOversold}, VWAP={vwap}, close[-3]={ctx.Close3BarsAgo}, ADX={adx:F1}<{o.AdxMaxThreshold}");
        }
        if (shortBbStretch && shortRsiOverbought && shortVwapZone && shortMicroConfirmOk && adxOk)
        {
            return BuildCandidate(BracketSide.Short, ctx.BarClose, atr, snap.Atr50Sma, rsi, ctx, o,
                $"MR-Down: close={ctx.BarClose}>bbU={bbUp}, RSI={rsi:F1}>{o.RsiOverbought}, VWAP={vwap}, close[-3]={ctx.Close3BarsAgo}, ADX={adx:F1}<{o.AdxMaxThreshold}");
        }

        return null;
    }

    private SignalCandidate BuildCandidate(
        BracketSide side,
        decimal     entry,
        decimal     atr,
        decimal?    atr50,
        decimal     rsi,
        MarketContext ctx,
        MeanReversionBbVwapOptions o,
        string      reason)
    {
        var bracket = BracketCalculator.Compute(
            entry, atr, side, o.AtrSlMultiplier, o.AtrTpMultiplier, atr50);

        // RSI extremity → confidence floor. RSI 25/75 ⇒ 0.5; 0/100 ⇒ 1.0.
        // We score by absolute distance from 50.
        var distFromMid = Math.Abs(rsi - 50m);
        var confidence = Clamp01(0.5m + (distFromMid - 25m) / 50m);

        return new SignalCandidate(
            StrategyType: StrategyType.MeanReversion,
            StrategyCode: StrategyCodes.MeanReversionBbVwap,
            Side:         side.ToSideCode(),
            EntryPrice:   entry,
            StopLoss:     bracket.StopLoss,
            TakeProfit:   bracket.TakeProfit,
            AtrValue:     atr,
            Confidence:   confidence,
            Reason:       reason,
            Trail:        null,             // §4.3: no trail for MR
            TimeStopBars: o.TimeStopBars);
    }

    private static decimal Clamp01(decimal v) => v < 0m ? 0m : v > 1m ? 1m : v;
}
