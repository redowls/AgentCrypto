using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;
using TradingBot.Core.Observability;
using TradingBot.Observability.Logging;
using Xunit;

namespace TradingBot.Tests.Observability;

public class CorrelationIdEnricherTests
{
    private static LogEvent MakeEvent() => new(
        DateTimeOffset.UtcNow, LogEventLevel.Information, exception: null,
        new MessageTemplate(Enumerable.Empty<MessageTemplateToken>()),
        Enumerable.Empty<LogEventProperty>());

    private static StubFactory Factory() => new();

    [Fact]
    public void Outside_scope_no_property_is_added()
    {
        var enricher = new CorrelationIdEnricher();
        var ev = MakeEvent();
        enricher.Enrich(ev, Factory());
        ev.Properties.Should().NotContainKey("CorrelationId");
    }

    [Fact]
    public void Inside_scope_property_is_added_with_id()
    {
        var enricher = new CorrelationIdEnricher();
        var id = Guid.NewGuid();
        var ev = MakeEvent();
        using (SignalContext.BeginSignal(id))
        {
            enricher.Enrich(ev, Factory());
        }
        ev.Properties["CorrelationId"].ToString().Trim('"').Should().Be(id.ToString("D"));
    }

    private sealed class StubFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
