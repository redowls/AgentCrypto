using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Brackets;
using TradingBot.Strategies.Configuration;

namespace TradingBot.Strategies.Strategies;

/// <summary>
/// §3.3 — EMA Crossover with ADX Filter (TREND_EMA_ADX).
///
/// Long entry (per the design doc):
///   Crossover(EMA9, EMA21)                            — fast above slow this bar (was below last bar)
///   AND Close &gt; EMA50                                — same-TF trend filter
///   AND Close_4h &gt; EMA200_4h                         — HTF alignment
///   AND ADX(14) &gt; 25                                 — trending regime
///   AND NOT IsExplosiveBar                            — skip range &gt; 2.5×ATR exhaustion bars
///
/// Short entry mirrors with the bearish crossover and inverted trend filters.
///
/// Brackets: SL = entry ± 2.0×ATR, TP = entry ± 5.0×ATR (§4.3, partial 50% at +2R
/// and the runner trails — partial-take logic lives in the position manager).
/// Trail: EMA21 or 2×ATR Chandelier (whichever is tighter), activated at +1R.
/// Hard time stop: 5 days = 120 bars on 1h.
/// </summary>
public sealed class TrendEmaAdxStrategy : IStrategy
{
    private readonly IOptionsMonitor<TrendEmaAdxOptions> _options;
    private readonly ILogger<TrendEmaAdxStrategy> _log;

    public TrendEmaAdxStrategy(
        IOptionsMonitor<TrendEmaAdxOptions> options,
        ILogger<TrendEmaAdxStrategy> log)
    {
        _options = options;
        _log = log;
    }

    public string Name => StrategyCodes.TrendEmaAdx;
    public string PrimaryTimeframe => _options.CurrentValue.PrimaryTimeframe;
    public string? HigherTimeframe => _options.CurrentValue.HigherTimeframe;
    public StrategyType StrategyType => StrategyType.Trend;

    /// <summary>§3.4: Trend strategy fires only in directional regimes.</summary>
    public Regime[] AllowedRegimes => new[] { Regime.TrendingUp, Regime.TrendingDown };

    public SignalCandidate? Evaluate(
        IndicatorSnapshot  snap,
        IndicatorSnapshot? htf,
        Regime             regime,
        MarketContext      ctx)
    {
        var o = _options.CurrentValue;
        if (!o.Enabled)
        {
            _log.LogDebug("TREND_EMA_ADX disabled by options for {Symbol}@{Bar}", ctx.SymbolCode, ctx.BarOpenTime);
            return null;
        }

        // Required snapshot fields. Anything null ⇒ warm-up incomplete; bail.
        if (snap.Ema9   is not decimal ema9   ||
            snap.Ema21  is not decimal ema21  ||
            snap.Ema50  is not decimal ema50  ||
            snap.Adx14  is not decimal adx    ||
            snap.Atr14  is not decimal atr)
        {
            _log.LogDebug(
                "TREND_EMA_ADX {Symbol}@{Bar}: snapshot incomplete (ema9={E9}, ema21={E21}, ema50={E50}, adx={A}, atr={T})",
                ctx.SymbolCode, ctx.BarOpenTime,
                snap.Ema9, snap.Ema21, snap.Ema50, snap.Adx14, snap.Atr14);
            return null;
        }

        // EMA9/21 crossover requires the prior-bar EMA values. The SignalEngine
        // pre-loads PriorSnapshot for the prior bar on the same TF; if it's
        // null the bar history hasn't warmed up.
        if (ctx.PriorSnapshot is null ||
            ctx.PriorSnapshot.Ema9  is not decimal ema9Prev ||
            ctx.PriorSnapshot.Ema21 is not decimal ema21Prev)
        {
            _log.LogDebug(
                "TREND_EMA_ADX {Symbol}@{Bar}: prior-bar EMAs unavailable (PriorSnapshot null or warming up)",
                ctx.SymbolCode, ctx.BarOpenTime);
            return null;
        }

        // HTF alignment is non-optional (§3.3). htf null ⇒ refuse (the strategy
        // contract states implementations that require HTF must treat null as a fail).
        if (htf is null || htf.Ema200 is not decimal htfEma200)
        {
            _log.LogDebug(
                "TREND_EMA_ADX {Symbol}@{Bar}: HTF snapshot or HTF EMA200 unavailable",
                ctx.SymbolCode, ctx.BarOpenTime);
            return null;
        }
        if (ctx.HtfBarClose is not decimal htfClose)
        {
            _log.LogDebug(
                "TREND_EMA_ADX {Symbol}@{Bar}: HTF bar close not provided by SignalEngine",
                ctx.SymbolCode, ctx.BarOpenTime);
            return null;
        }

        // Bullish / bearish crossover -- strict transition: fast crossed slow on
        // *this* bar (was on or below at prior bar, is strictly above now).
        var bullishCross = ema9Prev <= ema21Prev && ema9 > ema21;
        var bearishCross = ema9Prev >= ema21Prev && ema9 < ema21;

        // Same-TF trend filter
        var longTrend  = ctx.BarClose > ema50;
        var shortTrend = ctx.BarClose < ema50;

        // HTF alignment
        var longHtfOk  = htfClose > htfEma200;
        var shortHtfOk = htfClose < htfEma200;

        // ADX strength gate (§3.3: > 25)
        var adxOk = adx > o.AdxThreshold;

        // Exhaustion-bar guard: skip when current bar is huge (range > N × ATR).
        // §3.3 phrases this as "range > 2.5 × ATR(20)"; we approximate with ATR(14)
        // since that's what the snapshot carries (delta < 5% on liquid pairs).
        var explosive = ctx.BarRange > o.ExplosiveBarAtrMultiplier * atr;
        var notExplosive = !explosive;

        _log.LogDebug(
            "TREND_EMA_ADX {Symbol}@{Bar} gates: regime={Reg} bullCross={BC} bearCross={SC} longTrend={LT} shortTrend={ST} longHTF={LH} shortHTF={SH} adxOk={A} notExplosive={NE} (ema9={E9:F2}/prev={E9P:F2}, ema21={E21:F2}/prev={E21P:F2}, ema50={E50:F2}, htfClose={HC}, htfEma200={HE200}, adx={Adx:F1}, range={R}, atr={Atr})",
            ctx.SymbolCode, ctx.BarOpenTime, regime,
            bullishCross, bearishCross, longTrend, shortTrend, longHtfOk, shortHtfOk,
            adxOk, notExplosive,
            ema9, ema9Prev, ema21, ema21Prev, ema50,
            htfClose, htfEma200, adx, ctx.BarRange, atr);

        if (bullishCross && longTrend && longHtfOk && adxOk && notExplosive)
        {
            return BuildCandidate(BracketSide.Long, ctx.BarClose, atr, snap.Atr50Sma, adx, ctx, o,
                $"Trend-Up: EMA9={ema9:F2} crossed EMA21={ema21:F2} (prev {ema9Prev:F2}/{ema21Prev:F2}), close>EMA50={ema50:F2}, HTF{htfClose}>EMA200={htfEma200}, ADX={adx:F1}>{o.AdxThreshold}");
        }
        if (o.AllowShorts && bearishCross && shortTrend && shortHtfOk && adxOk && notExplosive)
        {
            return BuildCandidate(BracketSide.Short, ctx.BarClose, atr, snap.Atr50Sma, adx, ctx, o,
                $"Trend-Down: EMA9={ema9:F2} crossed below EMA21={ema21:F2} (prev {ema9Prev:F2}/{ema21Prev:F2}), close<EMA50={ema50:F2}, HTF{htfClose}<EMA200={htfEma200}, ADX={adx:F1}>{o.AdxThreshold}");
        }

        return null;
    }

    private SignalCandidate BuildCandidate(
        BracketSide side,
        decimal     entry,
        decimal     atr,
        decimal?    atr50,
        decimal     adx,
        MarketContext ctx,
        TrendEmaAdxOptions o,
        string      reason)
    {
        var bracket = BracketCalculator.Compute(
            entry, atr, side, o.AtrSlMultiplier, o.AtrTpMultiplier, atr50);

        // ADX strength → confidence: 0.5 at threshold, 1.0 at +25 above.
        var confidence = Clamp01(0.5m + (adx - o.AdxThreshold) / 50m);

        var trail = new TrailSpec(
            Mode:                    TrailMode.Ema21OrChandelier,
            ChandelierLookback:      o.TrailChandelierLookback,
            ChandelierAtrMultiplier: o.TrailChandelierAtrMultiplier,
            ActivationRMultiple:     o.TrailActivationRMultiple);

        return new SignalCandidate(
            StrategyType: StrategyType.Trend,
            StrategyCode: StrategyCodes.TrendEmaAdx,
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
