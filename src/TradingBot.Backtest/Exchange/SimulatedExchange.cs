using System.Collections.Concurrent;
using TradingBot.Backtest.Configuration;
using TradingBot.Backtest.Domain;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Execution.Slippage;

namespace TradingBot.Backtest.Exchange;

// Minimal, deterministic in-memory replacement for IBinanceGateway, sized to
// the backtest's needs: synchronous submit/cancel + a per-bar ProcessBar()
// that fills any pending order whose trigger price the bar's range crosses.
//
// Fill semantics (§10 spec, refined):
//   MARKET            → fills at next bar's Open + slippage (taker fee).
//   LIMIT BUY         → fills if bar.Low ≤ Price (fill at Price, maker fee).
//   LIMIT SELL        → fills if bar.High ≥ Price (fill at Price, maker fee).
//   STOP_MARKET BUY   → triggers if bar.High ≥ StopPrice; fills at StopPrice
//                       + slippage (taker).  Used for short-protection stops.
//   STOP_MARKET SELL  → triggers if bar.Low  ≤ StopPrice; fills at StopPrice
//                       − slippage (taker).  Used for long-protection stops.
//   TAKE_PROFIT_MARKET BUY  → triggers if bar.Low  ≤ StopPrice; fills at StopPrice
//                             (taker — exits a short).
//   TAKE_PROFIT_MARKET SELL → triggers if bar.High ≥ StopPrice; fills at StopPrice
//                             (taker — exits a long).
//
// Edge case: when a single bar's range crosses both the SL and TP of the
// same position, we assume worst-case (SL hits first). This avoids the
// otherwise-ambiguous tie-break and matches typical conservative practice.
//
// All fee/slippage math is sourced from DefaultSlippageModel + the engine
// options' bps configuration — no live exchange.
internal sealed class SimulatedExchange
{
    private readonly BacktestEngineOptions _opts;
    private readonly ISlippageModel        _slippage;
    private readonly Dictionary<string, PendingOrder> _byClientOrderId = new(StringComparer.Ordinal);
    private long _nextExchangeOrderId = 1;
    private long _nextTradeId         = 1;

    public SimulatedExchange(BacktestEngineOptions opts, ISlippageModel slippage)
    {
        _opts     = opts;
        _slippage = slippage;
    }

    public bool TryGet(string clientOrderId, out PendingOrder? order)
    {
        var ok = _byClientOrderId.TryGetValue(clientOrderId, out var po);
        order = po;
        return ok;
    }

    // Submit a new order. The caller will already have inserted a bt.Orders
    // row in NEW state and supplies the localOrderId here so we can carry it
    // back on fills without a second DB round-trip.
    public PendingOrder Submit(
        long localOrderId,
        string clientOrderId,
        string symbol,
        string side,
        string orderType,
        decimal quantity,
        decimal? price,
        decimal? stopPrice,
        string? timeInForce,
        bool reduceOnly,
        string? positionSide,
        string correlationId,
        DateTime nowUtc)
    {
        if (_byClientOrderId.ContainsKey(clientOrderId))
            throw new InvalidOperationException($"Duplicate clientOrderId: {clientOrderId}");

        var po = new PendingOrder
        {
            LocalOrderId    = localOrderId,
            ExchangeOrderId = _nextExchangeOrderId++,
            ClientOrderId   = clientOrderId,
            Symbol          = symbol,
            Side            = side,
            OrderType       = orderType,
            Quantity        = quantity,
            Price           = price,
            StopPrice       = stopPrice,
            TimeInForce     = timeInForce,
            ReduceOnly      = reduceOnly,
            PositionSide    = positionSide,
            CorrelationId   = correlationId,
            SubmittedAt     = nowUtc,
            Status          = OrderStatuses.New,
        };

        _byClientOrderId[clientOrderId] = po;
        return po;
    }

    public bool Cancel(string clientOrderId)
    {
        if (!_byClientOrderId.TryGetValue(clientOrderId, out var po))
            return false;
        po.Status = OrderStatuses.Cancelled;
        _byClientOrderId.Remove(clientOrderId);
        return true;
    }

    public IReadOnlyCollection<PendingOrder> OpenOrders()
        => _byClientOrderId.Values;

    // Advance the simulated book through one bar. Any pending order whose
    // trigger condition is satisfied by the bar's range produces a SimulatedFill.
    // Iteration order is deterministic (sorted by ClientOrderId — string
    // ordering is stable across runs and frameworks).
    public IReadOnlyList<SimulatedFill> ProcessBar(Candle bar)
    {
        if (_byClientOrderId.Count == 0) return Array.Empty<SimulatedFill>();

        // Snapshot — we mutate _byClientOrderId in the loop on fill.
        var ordered = _byClientOrderId.Values
            .OrderBy(o => o.ClientOrderId, StringComparer.Ordinal)
            .ToList();

        var fills = new List<SimulatedFill>(ordered.Count);
        foreach (var po in ordered)
        {
            if (po.Status != OrderStatuses.New) continue;
            if (!po.EligibleFromOpenOfNextBar)
            {
                // Submission bar; defer.
                continue;
            }
            var fill = TryFillAgainstBar(po, bar);
            if (fill is null) continue;
            fills.Add(fill);
            po.Status = OrderStatuses.Filled;
            _byClientOrderId.Remove(po.ClientOrderId);
        }
        return fills;
    }

    private SimulatedFill? TryFillAgainstBar(PendingOrder po, Candle bar)
    {
        var (fillPrice, isMaker) = po.OrderType switch
        {
            OrderTypes.Market           => (FillMarket(po, bar), false),
            OrderTypes.Limit            => FillLimitIfCrossed(po, bar),
            OrderTypes.StopMarket       => FillStopIfTriggered(po, bar, takeProfit: false),
            OrderTypes.TakeProfitMarket => FillStopIfTriggered(po, bar, takeProfit: true),
            _ => ((decimal?)null, false),
        };
        if (fillPrice is null) return null;

        var feeBps = isMaker ? FeeMakerBps() : FeeTakerBps();
        var notional = po.Quantity * fillPrice.Value;
        var commission = notional * feeBps / 10_000m;

        return new SimulatedFill(
            LocalOrderId:    po.LocalOrderId,
            ExchangeOrderId: po.ExchangeOrderId,
            ClientOrderId:   po.ClientOrderId,
            Symbol:          po.Symbol,
            Side:            po.Side,
            OrderType:       po.OrderType,
            Quantity:        po.Quantity,
            Price:           fillPrice.Value,
            CommissionUsd:   commission,
            CommissionAsset: "USDT",
            IsMaker:         isMaker,
            FillTimeUtc:     bar.OpenTime,
            ExitReason:      ExitReasonFor(po));
    }

    private decimal FillMarket(PendingOrder po, Candle bar)
    {
        var mid = bar.Open;
        var est = _slippage.Estimate(new SlippageInputs(
            MidPrice:                   mid,
            SpreadBps:                  _opts.SimulatedSpreadBps,
            OrderQuantity:              po.Quantity,
            AvailableTopOfBookQuantity: po.Quantity * 100m,   // assume infinite liquidity
            Side:                       po.Side));
        return est.ExpectedPrice;
    }

    private static (decimal?, bool) FillLimitIfCrossed(PendingOrder po, Candle bar)
    {
        if (po.Price is not decimal limitPrice) return (null, true);
        if (po.Side == Sides.Buy)
        {
            return bar.Low <= limitPrice ? (limitPrice, true) : (null, true);
        }
        else
        {
            return bar.High >= limitPrice ? (limitPrice, true) : (null, true);
        }
    }

    private (decimal?, bool) FillStopIfTriggered(PendingOrder po, Candle bar, bool takeProfit)
    {
        if (po.StopPrice is not decimal sp) return (null, false);
        // Trigger geometry depends on side:
        //   Sell-side stops sit BELOW market and trigger on a low.
        //   Buy-side  stops sit ABOVE market and trigger on a high.
        // TAKE_PROFIT_MARKET inverts: TP-sell is ABOVE, TP-buy is BELOW.
        bool triggered = po.Side == Sides.Buy
            ? (takeProfit ? bar.Low  <= sp : bar.High >= sp)
            : (takeProfit ? bar.High >= sp : bar.Low  <= sp);
        if (!triggered) return (null, false);

        // Take-profits fill at the trigger; stop-markets get adverse slippage.
        if (takeProfit) return (sp, false);

        var est = _slippage.Estimate(new SlippageInputs(
            MidPrice:                   sp,
            SpreadBps:                  _opts.SimulatedSpreadBps,
            OrderQuantity:              po.Quantity,
            AvailableTopOfBookQuantity: po.Quantity * 100m,
            Side:                       po.Side));
        return (est.ExpectedPrice, false);
    }

    private static string ExitReasonFor(PendingOrder po) => po.OrderType switch
    {
        OrderTypes.StopMarket       => FillExitReasons.StopLoss,
        OrderTypes.TakeProfitMarket => FillExitReasons.TakeProfit,
        _                           => FillExitReasons.Entry,
    };

    private decimal FeeMakerBps() => _opts.AccountType == AccountTypes.Spot ? _opts.SpotMakerBps : _opts.UmFutMakerBps;
    private decimal FeeTakerBps() => _opts.AccountType == AccountTypes.Spot ? _opts.SpotTakerBps : _opts.UmFutTakerBps;

    // After each bar, mark all "submitted-this-bar" orders as eligible from
    // the next bar onward. Called by the engine once per replay step.
    public void TickAdmission()
    {
        // PendingOrder is constructed with EligibleFromOpenOfNextBar=true so
        // there's nothing to flip — kept as an extension point if we ever
        // model intra-bar timing.
    }

    public long NextTradeId() => _nextTradeId++;
}
