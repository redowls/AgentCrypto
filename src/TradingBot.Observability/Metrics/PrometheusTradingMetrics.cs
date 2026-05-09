using Prometheus;
using TradingBot.Core.Observability;

namespace TradingBot.Observability.Metrics;

public sealed class PrometheusTradingMetrics : ITradingMetrics
{
    private readonly Counter   _signals;
    private readonly Counter   _orders;
    private readonly Counter   _ordersFilled;
    private readonly Counter   _ordersCanceled;
    private readonly Gauge     _positionPnl;
    private readonly Gauge     _accountEquity;
    private readonly Gauge     _drawdown;
    private readonly Counter   _aiCalls;
    private readonly Counter   _aiCost;
    private readonly Counter   _wsReconnects;
    private readonly Histogram _strategyLatency;
    private readonly Histogram _orderFillLatency;
    private readonly Gauge     _wsLastEvent;
    private readonly Counter   _alertsDeduped;

    /// <summary>Default ctor uses the global default registry; tests pass a fresh registry.</summary>
    public PrometheusTradingMetrics() : this(Prometheus.Metrics.DefaultRegistry) { }

    public PrometheusTradingMetrics(CollectorRegistry registry)
    {
        var f = Prometheus.Metrics.WithCustomRegistry(registry);

        _signals          = f.CreateCounter("tradingbot_signals_total",          "Signals published.",            new CounterConfiguration   { LabelNames = ["strategy", "symbol", "side"] });
        _orders           = f.CreateCounter("tradingbot_orders_total",           "Order state transitions.",      new CounterConfiguration   { LabelNames = ["status", "side", "symbol"] });
        _ordersFilled     = f.CreateCounter("tradingbot_orders_filled_total",    "Orders reaching FILLED.",       new CounterConfiguration   { LabelNames = ["side", "symbol"] });
        _ordersCanceled   = f.CreateCounter("tradingbot_orders_canceled_total",  "Orders reaching CANCELED.",     new CounterConfiguration   { LabelNames = ["side", "symbol"] });
        _positionPnl      = f.CreateGauge  ("tradingbot_position_pnl_usd",       "Per-symbol unrealized PnL.",    new GaugeConfiguration     { LabelNames = ["symbol"] });
        _accountEquity    = f.CreateGauge  ("tradingbot_account_equity_usd",     "Account equity (USD).");
        _drawdown         = f.CreateGauge  ("tradingbot_drawdown_pct",           "Drawdown vs running peak.");
        _aiCalls          = f.CreateCounter("tradingbot_ai_calls_total",         "Claude API calls.",             new CounterConfiguration   { LabelNames = ["purpose", "result"] });
        _aiCost           = f.CreateCounter("tradingbot_ai_cost_usd_total",      "AI USD spent.",                 new CounterConfiguration   { LabelNames = ["purpose"] });
        _wsReconnects     = f.CreateCounter("tradingbot_ws_reconnects_total",    "WebSocket reconnects.",         new CounterConfiguration   { LabelNames = ["account", "stream"] });
        _strategyLatency  = f.CreateHistogram("tradingbot_strategy_latency_ms",  "Strategy.Evaluate wall time.",  new HistogramConfiguration { LabelNames = ["strategy"], Buckets = Histogram.ExponentialBuckets(start: 1, factor: 2, count: 12) });
        _orderFillLatency = f.CreateHistogram("tradingbot_order_fill_latency_ms", "Submit -> first FILLED latency.", new HistogramConfiguration { LabelNames = ["side", "symbol"], Buckets = Histogram.ExponentialBuckets(start: 1, factor: 2, count: 17) });
        _wsLastEvent      = f.CreateGauge  ("tradingbot_ws_last_event_seconds",  "Seconds since last WS event.",  new GaugeConfiguration     { LabelNames = ["account", "stream"] });
        _alertsDeduped    = f.CreateCounter("tradingbot_alerts_deduped_total",   "Alerts collapsed by dedup.",    new CounterConfiguration   { LabelNames = ["severity"] });
    }

    public void IncSignal(string strategy, string symbol, string side)              => _signals.WithLabels(strategy, symbol, side).Inc();
    public void IncOrder(string status, string side, string symbol)                 => _orders.WithLabels(status, side, symbol).Inc();
    public void IncOrderFilled(string side, string symbol)                          => _ordersFilled.WithLabels(side, symbol).Inc();
    public void IncOrderCanceled(string side, string symbol)                        => _ordersCanceled.WithLabels(side, symbol).Inc();
    public void SetPositionPnl(string symbol, double usd)                           => _positionPnl.WithLabels(symbol).Set(usd);
    public void SetAccountEquity(double usd)                                        => _accountEquity.Set(usd);
    public void SetDrawdown(double pct)                                             => _drawdown.Set(pct);
    public void IncAiCall(string purpose, string result)                            => _aiCalls.WithLabels(purpose, result).Inc();
    public void AddAiCost(string purpose, double usd)                               => _aiCost.WithLabels(purpose).Inc(usd);
    public void IncWsReconnect(string account, string stream)                       => _wsReconnects.WithLabels(account, stream).Inc();
    public void ObserveStrategyLatency(string strategy, double milliseconds)        => _strategyLatency.WithLabels(strategy).Observe(milliseconds);
    public void ObserveOrderFillLatency(string side, string symbol, double milliseconds) => _orderFillLatency.WithLabels(side, symbol).Observe(milliseconds);
    public void SetWsLastEventSeconds(string account, string stream, double secondsSinceLastEvent) => _wsLastEvent.WithLabels(account, stream).Set(secondsSinceLastEvent);
    public void IncAlertDeduped(string severity)                                    => _alertsDeduped.WithLabels(severity).Inc();
}
