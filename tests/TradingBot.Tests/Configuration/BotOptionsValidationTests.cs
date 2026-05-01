using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using TradingBot.Worker.Configuration;
using Xunit;

namespace TradingBot.Tests.Configuration;

public class BotOptionsValidationTests
{
    [Fact]
    public void Populated_options_pass_DataAnnotations_validation()
    {
        var opts = new BotOptions { Symbols = ["BTCUSDT", "ETHUSDT"] };
        Validate(opts).Should().BeEmpty();
    }

    [Fact]
    public void Empty_Symbols_list_is_rejected()
    {
        var opts = new BotOptions { Symbols = [] };
        Validate(opts).Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(BotOptions.Symbols));
    }

    [Fact]
    public void RiskPerTradePct_above_10_percent_is_rejected()
    {
        var opts = new BotOptions { Symbols = ["BTCUSDT"], RiskPerTradePct = 0.20m };
        Validate(opts).Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(BotOptions.RiskPerTradePct));
    }

    [Fact]
    public void MaxLeverage_above_5_is_rejected()
    {
        var opts = new BotOptions { Symbols = ["BTCUSDT"], MaxLeverage = 10 };
        Validate(opts).Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(BotOptions.MaxLeverage));
    }

    private static IList<ValidationResult> Validate(object instance)
    {
        var ctx = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, ctx, results, validateAllProperties: true);
        return results;
    }
}
