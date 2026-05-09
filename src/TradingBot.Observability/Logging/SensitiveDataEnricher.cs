using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace TradingBot.Observability.Logging;

public sealed class SensitiveDataEnricher : ILogEventEnricher
{
    private const string Redaction = "***REDACTED***";
    private readonly HashSet<string> _keys;

    public SensitiveDataEnricher(IOptions<SensitiveLoggingOptions> opts)
    {
        _keys = new HashSet<string>(opts.Value.RedactedKeys, StringComparer.OrdinalIgnoreCase);
        if (opts.Value.MaskOrderQuantities)
        {
            _keys.Add("Quantity");
            _keys.Add("Qty");
        }
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        foreach (var key in logEvent.Properties.Keys.ToArray())
        {
            if (_keys.Contains(key))
                logEvent.AddOrUpdateProperty(factory.CreateProperty(key, Redaction));
        }
    }
}
