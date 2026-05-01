using FluentAssertions;
using TradingBot.Core.Domain;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.ReferenceData;
using Xunit;

namespace TradingBot.Tests.Exchange;

public sealed class SymbolFiltersTests
{
    [Fact]
    public void Replace_then_TryGet_is_case_insensitive()
    {
        var f = new SymbolFilters();
        f.Replace(AccountType.Spot, new[]
        {
            new Symbol { SymbolCode = "BTCUSDT", TickSize = 0.01m, StepSize = 0.0001m, MinNotional = 5m },
        });

        f.TryGet(AccountType.Spot, "BTCUSDT").Should().NotBeNull();
        f.TryGet(AccountType.Spot, "btcusdt").Should().NotBeNull();
        f.TryGet(AccountType.Spot, "ETHUSDT").Should().BeNull();
    }

    [Fact]
    public void Get_throws_for_unknown_symbol()
    {
        var f = new SymbolFilters();
        f.Replace(AccountType.Spot, Array.Empty<Symbol>());

        Action act = () => f.Get(AccountType.Spot, "XXXUSDT");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Replace_swaps_full_snapshot_atomically()
    {
        var f = new SymbolFilters();

        f.Replace(AccountType.Spot, new[]
        {
            new Symbol { SymbolCode = "BTCUSDT" },
            new Symbol { SymbolCode = "ETHUSDT" },
        });

        f.All(AccountType.Spot).Should().HaveCount(2);

        // Replacing with smaller set drops the omitted symbol.
        f.Replace(AccountType.Spot, new[] { new Symbol { SymbolCode = "BTCUSDT" } });
        f.All(AccountType.Spot).Should().HaveCount(1);
        f.TryGet(AccountType.Spot, "ETHUSDT").Should().BeNull();
    }
}
