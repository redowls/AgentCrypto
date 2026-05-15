using FluentAssertions;
using TradingBot.Execution.Identity;
using Xunit;

namespace TradingBot.Tests.Execution;

public class ClientOrderIdGeneratorTests
{
    [Theory]
    [InlineData("BREAKOUT_DON",  1L)]
    [InlineData("MR_BB_VWAP",    2_000_000L)]
    [InlineData("TREND_EMA_ADX", 9_000_000_000L)]
    public void Entry_id_is_under_36_chars(string strategy, long signalId)
    {
        var id = ClientOrderIdGenerator.ForEntry(strategy, signalId);
        id.Length.Should().BeLessThanOrEqualTo(ClientOrderIdGenerator.MaxLength);
        id.Should().StartWith("BOT-");
    }

    [Fact]
    public void Entry_id_is_deterministic()
    {
        var a = ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", 12345L);
        var b = ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", 12345L);
        a.Should().Be(b);
    }

    [Fact]
    public void Different_signal_ids_produce_different_entry_ids()
    {
        var a = ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", 1L);
        var b = ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", 2L);
        a.Should().NotBe(b);
    }

    [Fact]
    public void Bracket_legs_have_distinct_ids()
    {
        var sl = ClientOrderIdGenerator.ForBracket("BR", 100L, ClientOrderIdGenerator.BracketLeg.Stop);
        var tp = ClientOrderIdGenerator.ForBracket("BR", 100L, ClientOrderIdGenerator.BracketLeg.TakeProfit);
        sl.Should().NotBe(tp);
        sl.Should().Contain("SL");
        tp.Should().Contain("TP");
    }

    [Fact]
    public void Trailing_replacement_is_unique_per_sequence()
    {
        var s1 = ClientOrderIdGenerator.ForTrailingReplacement(42L, 1);
        var s2 = ClientOrderIdGenerator.ForTrailingReplacement(42L, 2);
        s1.Should().NotBe(s2);
        s1.Length.Should().BeLessThanOrEqualTo(ClientOrderIdGenerator.MaxLength);
    }

    [Fact]
    public void StableSuffix_is_pure_function()
    {
        ClientOrderIdGenerator.StableSuffix("seed").Should().Be(ClientOrderIdGenerator.StableSuffix("seed"));
        ClientOrderIdGenerator.StableSuffix("seed").Length.Should().Be(8);
    }

    [Theory]
    [InlineData("BREAKOUT_DON",  "BD")]
    [InlineData("MR_BB_VWAP",    "MR")]
    [InlineData("TREND_EMA_ADX", "TR")]
    public void Strategy_short_codes_are_canonical(string strategy, string expected)
    {
        ClientOrderIdGenerator.ShortenStrategy(strategy).Should().Be(expected);
    }

    [Fact]
    public void Unknown_strategy_falls_back_to_sanitized_4char_prefix()
    {
        var s = ClientOrderIdGenerator.ShortenStrategy("Funky-Strategy_v2!");
        s.Length.Should().BeLessThanOrEqualTo(4);
        s.Should().MatchRegex("^[a-zA-Z0-9]{1,4}$");
    }

    [Fact]
    public void Entry_id_chars_are_safe_for_binance_regex()
    {
        var id = ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", 9_999_999L);
        id.Should().MatchRegex("^[A-Za-z0-9_-]{1,36}$");
    }
}
