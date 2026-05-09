namespace TradingBot.Core.Observability;

public interface ITradingMetrics
{
    void IncSignal(string strategy, string symbol, string side);
    void IncOrder(string status, string side, string symbol);
    void IncOrderFilled(string side, string symbol);
    void IncOrderCanceled(string side, string symbol);
    void SetPositionPnl(string symbol, double usd);
    void SetAccountEquity(double usd);
    void SetDrawdown(double pct);
    void IncAiCall(string purpose, string result);
    void AddAiCost(string purpose, double usd);
    void IncWsReconnect(string account, string stream);
    void ObserveStrategyLatency(string strategy, double milliseconds);
    void ObserveOrderFillLatency(string side, string symbol, double milliseconds);
    void SetWsLastEventSeconds(string account, string stream, double secondsSinceLastEvent);
    void IncAlertDeduped(string severity);
}
