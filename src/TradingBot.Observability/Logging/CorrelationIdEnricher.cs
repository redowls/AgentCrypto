using Serilog.Core;
using Serilog.Events;

namespace TradingBot.Observability.Logging;

public sealed class CorrelationIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        var id = SignalContext.Current;
        if (id is null) return;
        logEvent.AddPropertyIfAbsent(
            factory.CreateProperty("CorrelationId", id.Value.ToString("D")));
    }
}
