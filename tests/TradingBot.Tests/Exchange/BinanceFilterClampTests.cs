using FluentAssertions;
using TradingBot.Core.Domain;
using TradingBot.Exchange.Filters;
using Xunit;

namespace TradingBot.Tests.Exchange;

public sealed class BinanceFilterClampTests
{
    [Theory]
    [InlineData("123.456", "0.01", "123.45")]
    [InlineData("123.999", "0.01", "123.99")]
    [InlineData("0.012345", "0.0001", "0.0123")]
    [InlineData("100.0", "0.5", "100.0")]
    [InlineData("100.4", "0.5", "100.0")]
    [InlineData("100.5", "0.5", "100.5")]
    public void ClampPriceToTick_floors_to_nearest_tick(string priceStr, string tickStr, string expectedStr)
    {
        var price = decimal.Parse(priceStr, System.Globalization.CultureInfo.InvariantCulture);
        var tick  = decimal.Parse(tickStr, System.Globalization.CultureInfo.InvariantCulture);
        var expected = decimal.Parse(expectedStr, System.Globalization.CultureInfo.InvariantCulture);

        BinanceFilterClamp.ClampPriceToTick(price, tick).Should().Be(expected);
    }

    [Theory]
    [InlineData("0.123456789", "0.001", "0.123")]
    [InlineData("1.0", "0.5", "1.0")]
    [InlineData("1.4", "0.5", "1.0")]
    [InlineData("0.0", "0.001", "0.0")]
    public void ClampQuantityToStep_floors_to_nearest_step(string qtyStr, string stepStr, string expectedStr)
    {
        var qty  = decimal.Parse(qtyStr, System.Globalization.CultureInfo.InvariantCulture);
        var step = decimal.Parse(stepStr, System.Globalization.CultureInfo.InvariantCulture);
        var expected = decimal.Parse(expectedStr, System.Globalization.CultureInfo.InvariantCulture);

        BinanceFilterClamp.ClampQuantityToStep(qty, step).Should().Be(expected);
    }

    [Fact]
    public void ClampPriceToTick_rejects_non_positive_tick()
    {
        Action act = () => BinanceFilterClamp.ClampPriceToTick(100m, 0m);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EnforceMinNotional_passes_when_above_threshold()
    {
        var qty = BinanceFilterClamp.EnforceMinNotional(0.001m, 60_000m, 5m);
        qty.Should().Be(0.001m);
    }

    [Fact]
    public void EnforceMinNotional_throws_when_below_threshold()
    {
        Action act = () => BinanceFilterClamp.EnforceMinNotional(0.0001m, 100m, 10m);
        act.Should().Throw<MinNotionalViolatedException>()
            .Which.MinNotional.Should().Be(10m);
    }

    [Fact]
    public void ClampForLimit_applies_tick_step_and_minNotional_in_one_call()
    {
        var filter = new Symbol
        {
            SymbolCode = "BTCUSDT",
            TickSize = 0.01m,
            StepSize = 0.0001m,
            MinNotional = 5m,
        };

        var (px, qty) = BinanceFilterClamp.ClampForLimit(60_001.987m, 0.0009999m, filter);

        px.Should().Be(60_001.98m);
        qty.Should().Be(0.0009m);
    }

    [Fact]
    public void ClampForLimit_throws_when_resulting_notional_below_min()
    {
        var filter = new Symbol
        {
            SymbolCode = "TINYUSDT",
            TickSize = 0.01m,
            StepSize = 0.01m,
            MinNotional = 100m,
        };

        Action act = () => BinanceFilterClamp.ClampForLimit(price: 1m, quantity: 1m, filter);
        act.Should().Throw<MinNotionalViolatedException>();
    }
}
