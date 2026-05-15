using FluentAssertions;
using TradingBot.Core.Domain.Enums;
using TradingBot.Execution.Slippage;
using Xunit;

namespace TradingBot.Tests.Execution;

public class DefaultSlippageModelTests
{
    private readonly DefaultSlippageModel _sut = new();

    [Fact]
    public void HalfSpread_is_half_of_quoted_spread()
    {
        var est = _sut.Estimate(new SlippageInputs(100m, SpreadBps: 10m, OrderQuantity: 1m, AvailableTopOfBookQuantity: null, Side: Sides.Buy));
        est.HalfSpreadBps.Should().Be(5m);
    }

    [Fact]
    public void Impact_is_zero_when_depth_is_unknown()
    {
        var est = _sut.Estimate(new SlippageInputs(100m, 10m, 1m, null, Sides.Buy));
        est.ImpactBps.Should().Be(0m);
        est.ParticipationPct.Should().BeNull();
    }

    [Fact]
    public void Impact_grows_with_participation_then_saturates_at_full_book()
    {
        var smallParticipation = _sut.Estimate(new SlippageInputs(100m, 0m, OrderQuantity: 1m, AvailableTopOfBookQuantity: 100m, Side: Sides.Buy));
        var halfParticipation  = _sut.Estimate(new SlippageInputs(100m, 0m, OrderQuantity: 50m, AvailableTopOfBookQuantity: 100m, Side: Sides.Buy));
        var fullParticipation  = _sut.Estimate(new SlippageInputs(100m, 0m, OrderQuantity: 200m, AvailableTopOfBookQuantity: 100m, Side: Sides.Buy));

        smallParticipation.ImpactBps.Should().BeLessThan(halfParticipation.ImpactBps);
        halfParticipation.ImpactBps.Should().BeLessThan(fullParticipation.ImpactBps);
        fullParticipation.ImpactBps.Should().BeApproximately(DefaultSlippageModel.DefaultImpactBpsAtFullParticipation, 0.0001m);
        fullParticipation.ParticipationPct.Should().Be(1m);
    }

    [Theory]
    [InlineData("BUY",  1)]
    [InlineData("SELL", -1)]
    public void ExpectedPrice_sign_follows_side(string side, int sign)
    {
        var est = _sut.Estimate(new SlippageInputs(100m, SpreadBps: 20m, OrderQuantity: 1m, AvailableTopOfBookQuantity: null, Side: side));
        var diff = est.ExpectedPrice - 100m;
        Math.Sign(diff).Should().Be(sign);
    }

    [Fact]
    public void ObservedSlippage_is_zero_at_mid()
    {
        DefaultSlippageModel.ObservedSlippageBps(100m, 100m, Sides.Buy).Should().Be(0m);
    }

    [Fact]
    public void ObservedSlippage_is_positive_when_buy_above_mid()
    {
        DefaultSlippageModel.ObservedSlippageBps(100m, 101m, Sides.Buy).Should().Be(100m); // 1% = 100bp
    }

    [Fact]
    public void ObservedSlippage_is_positive_when_sell_below_mid()
    {
        DefaultSlippageModel.ObservedSlippageBps(100m, 99m, Sides.Sell).Should().Be(100m);
    }

    [Fact]
    public void Estimate_throws_on_invalid_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _sut.Estimate(new SlippageInputs(0m, 0m, 1m, null, Sides.Buy)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _sut.Estimate(new SlippageInputs(100m, -1m, 1m, null, Sides.Buy)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _sut.Estimate(new SlippageInputs(100m, 0m, 0m, null, Sides.Buy)));
    }

    [Fact]
    public void Version_string_is_stable()
    {
        _sut.Version.Should().Be(DefaultSlippageModel.ModelVersion);
    }
}
