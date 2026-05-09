namespace TradingBot.Core.Observability;

public sealed class NullTradingMetrics : ITradingMetrics
{
    public void IncSignal(string strategy, string symbol, string side) { }
    public void IncOrder(string status, string side, string symbol) { }
    public void IncOrderFilled(string side, string symbol) { }
    public void IncOrderCanceled(string side, string symbol) { }
    public void SetPositionPnl(string symbol, double usd) { }
    public void SetAccountEquity(double usd) { }
    public void SetDrawdown(double pct) { }
    public void IncAiCall(string purpose, string result) { }
    public void AddAiCost(string purpose, double usd) { }
    public void IncWsReconnect(string account, string stream) { }
    public void ObserveStrategyLatency(string strategy, double milliseconds) { }
    public void ObserveOrderFillLatency(string side, string symbol, double milliseconds) { }
    public void SetWsLastEventSeconds(string account, string stream, double secondsSinceLastEvent) { }
    public void IncAlertDeduped(string severity) { }
}
