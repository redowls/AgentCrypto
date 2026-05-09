using FluentAssertions;
using Microsoft.Extensions.Options;
using Serilog.Events;
using Serilog.Parsing;
using TradingBot.Observability.Logging;
using Xunit;

namespace TradingBot.Tests.Observability;

public class SensitiveDataEnricherTests
{
    private static LogEvent MakeEvent(params (string Name, object Value)[] props) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplate(Enumerable.Empty<MessageTemplateToken>()),
            props.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value))).ToArray());

    private static SensitiveDataEnricher CreateEnricher(SensitiveLoggingOptions opts) =>
        new(Options.Create(opts));

    private sealed class StubFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }

    [Fact]
    public void Listed_key_value_is_redacted()
    {
        var enricher = CreateEnricher(new() { RedactedKeys = ["ApiKey"] });
        var ev = MakeEvent(("ApiKey", "secret-abc"), ("Symbol", "BTCUSDT"));

        enricher.Enrich(ev, new StubFactory());

        ev.Properties["ApiKey"].ToString().Should().Contain("REDACTED");
        ev.Properties["Symbol"].ToString().Should().Contain("BTCUSDT");
    }

    [Fact]
    public void MaskOrderQuantities_true_redacts_Quantity()
    {
        var enricher = CreateEnricher(new() { RedactedKeys = [], MaskOrderQuantities = true });
        var ev = MakeEvent(("Quantity", 0.123));

        enricher.Enrich(ev, new StubFactory());

        ev.Properties["Quantity"].ToString().Should().Contain("REDACTED");
    }

    [Fact]
    public void Match_is_case_insensitive()
    {
        var enricher = CreateEnricher(new() { RedactedKeys = ["ApiKey"] });
        var ev = MakeEvent(("apikey", "v"));

        enricher.Enrich(ev, new StubFactory());

        ev.Properties["apikey"].ToString().Should().Contain("REDACTED");
    }
}
