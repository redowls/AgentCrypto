using Microsoft.Extensions.Options;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Risk.Abstractions;
using TradingBot.Risk.Configuration;
using TradingBot.Risk.Manager;

namespace TradingBot.Backtest.Risk;

// Backtest risk gate. Reuses the live RiskMath (drawdown ladder, vol-adjust,
// raw-quantity sizing) verbatim, but skips collaborators that don't make sense
// in a deterministic single-symbol replay:
//
//   - IKillSwitch       — never tripped during backtest.
//   - ICorrelationService — single-symbol backtests have no cluster to occupy.
//   - IFundingRateProvider — no live funding feed; futures funding can be
//                            modelled in a follow-up if we add multi-symbol
//                            futures backtests.
//   - ISymbolFilters    — TickSize/StepSize clamping applied directly from
//                          the Symbol record passed in by the engine.
//   - IRiskEventRepository — the bt.* schema doesn't track risk events.
//
// Sizing math + drawdown ladder + vol-adjust + risk-fraction apply identically
// to live, so the gate's quantitative behaviour is preserved.
internal sealed class BacktestRiskManager : IRiskManager
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly Symbol _symbol;
    private readonly IBacktestSizingContext _ctx;

    public BacktestRiskManager(
        IOptionsMonitor<RiskOptions> options,
        Symbol symbol,
        IBacktestSizingContext ctx)
    {
        _options = options;
        _symbol  = symbol;
        _ctx     = ctx;
    }

    public Task<RiskDecision> ApproveAsync(
        Signal signal, AccountRiskState account, CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;

        // Gate (a): daily loss limit.
        if (account.DailyPnlPct <= opts.DailyLossLimitPct)
            return Task.FromResult(RiskDecision.Reject(
                RiskRejectReasons.DailyLossLimit,
                $"daily PnL {account.DailyPnlPct:P2} ≤ {opts.DailyLossLimitPct:P2}"));

        // Gate (b): max drawdown halt.
        if (account.DrawdownPct <= opts.MaxDrawdownHaltPct)
            return Task.FromResult(RiskDecision.Reject(
                RiskRejectReasons.MaxDrawdownHalt,
                $"drawdown {account.DrawdownPct:P2} ≤ {opts.MaxDrawdownHaltPct:P2}"));

        // Gate (c): max concurrent positions.
        if (account.OpenPositions >= opts.MaxConcurrentPositions)
            return Task.FromResult(RiskDecision.Reject(
                RiskRejectReasons.MaxConcurrentPositions,
                $"open positions {account.OpenPositions} ≥ {opts.MaxConcurrentPositions}"));

        // Gate (e): drawdown ladder.
        var kFactor = RiskMath.LadderMultiplier(account.DrawdownPct, opts.DrawdownLadder);
        if (kFactor <= 0m)
            return Task.FromResult(RiskDecision.Reject(
                RiskRejectReasons.MaxDrawdownHalt,
                $"drawdown ladder produced kFactor=0 at DD={account.DrawdownPct:P2}"));

        // Gate (f): vol-adjust.
        var atr14    = signal.AtrValue;
        var atr50Sma = _ctx.Atr50SmaAt(signal.SymbolId, signal.Interval, signal.BarOpenTime);
        var volAdj   = RiskMath.VolAdjust(atr14, atr50Sma, opts);

        // Gate (g)-(h): sizing.
        var stopDist = Math.Abs(signal.EntryPrice - signal.StopLoss);
        if (stopDist <= 0m)
            return Task.FromResult(RiskDecision.Reject(
                RiskRejectReasons.LotFilterInfeasible,
                "stop distance is zero"));

        var riskUsd = account.EquityUsd * opts.RiskPerTradeFraction * kFactor * volAdj;
        var rawQty  = RiskMath.RawQuantity(riskUsd, stopDist);

        // Gate (i): step-size + min-notional clamp using the symbol filter cache.
        var qty = ClampToStepAndMinNotional(rawQty, signal.EntryPrice, _symbol);
        if (qty <= 0m)
            return Task.FromResult(RiskDecision.Reject(
                RiskRejectReasons.LotFilterInfeasible,
                "qty rounded to zero after step/notional clamp"));

        // Gate (j): single-symbol cap.
        var notional      = qty * signal.EntryPrice;
        var symbolCapUsd  = account.EquityUsd * opts.SingleSymbolCapFraction;
        if (notional > symbolCapUsd)
        {
            qty       = symbolCapUsd / signal.EntryPrice;
            qty       = ClampToStepAndMinNotional(qty, signal.EntryPrice, _symbol);
            if (qty <= 0m)
                return Task.FromResult(RiskDecision.Reject(
                    RiskRejectReasons.SingleSymbolCapInfeasible,
                    $"clamped to symbol cap {symbolCapUsd:F2} ⇒ qty=0"));
            notional  = qty * signal.EntryPrice;
        }

        // Gate (k): gross exposure cap.
        var grossCapUsd = account.EquityUsd * opts.GrossExposureCapMultiple;
        if (account.GrossExposureUsd + notional > grossCapUsd)
            return Task.FromResult(RiskDecision.Reject(
                RiskRejectReasons.GrossExposure,
                $"gross {account.GrossExposureUsd + notional:F2} > cap {grossCapUsd:F2}"));

        // Gates (d), (l) skipped — see class doc.

        return Task.FromResult(RiskDecision.Approve(
            quantity:   qty,
            riskUsd:    riskUsd,
            notionalUsd: notional,
            kFactor:    kFactor,
            volAdjust:  volAdj));
    }

    private static decimal ClampToStepAndMinNotional(decimal qty, decimal price, Symbol s)
    {
        if (qty <= 0m || s.StepSize <= 0m) return 0m;
        var floored = Math.Floor(qty / s.StepSize) * s.StepSize;
        if (floored * price < s.MinNotional) return 0m;
        return floored;
    }
}

// Bridge for vol-adjust lookup — the live RiskManager calls into the Position
// repository / indicator cache; in backtest the engine pre-computes these and
// satisfies the lookup synchronously.
internal interface IBacktestSizingContext
{
    decimal? Atr50SmaAt(int symbolId, string interval, DateTime barOpenTime);
}
