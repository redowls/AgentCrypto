using TradingBot.Core.Domain.Enums;

namespace TradingBot.Execution.Slippage;

/// <summary>
/// §6.2 default slippage model:
///
///   slippageBps = halfSpreadBps + impactCoeff × √(participation) × 10_000
///
/// where <c>halfSpreadBps = SpreadBps / 2</c> (we cross the inside) and
/// <c>participation = orderQty / topOfBookQty</c> (clamped to [0, 1]). The
/// √-of-participation form is the textbook square-root market-impact rule;
/// it converges to halfSpread for tiny orders and saturates as the order
/// approaches the inside-quote depth.
///
/// The impact coefficient defaults to 5 bp at full participation — a
/// deliberate over-estimate vs. observed live slippage on the spot major
/// pairs (~1bp at 10% participation), so the backtest is conservative.
/// </summary>
public sealed class DefaultSlippageModel : ISlippageModel
{
    public const string ModelVersion = "v1";
    public const decimal DefaultImpactBpsAtFullParticipation = 5m;

    private readonly decimal _impactCoeffBps;

    public DefaultSlippageModel(decimal? impactCoeffBps = null)
    {
        _impactCoeffBps = impactCoeffBps ?? DefaultImpactBpsAtFullParticipation;
        if (_impactCoeffBps < 0m)
            throw new ArgumentOutOfRangeException(nameof(impactCoeffBps), impactCoeffBps,
                "impact coefficient must be non-negative");
    }

    public string Version => ModelVersion;

    public SlippageEstimate Estimate(SlippageInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.MidPrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(inputs), "MidPrice must be positive");
        if (inputs.SpreadBps < 0m)
            throw new ArgumentOutOfRangeException(nameof(inputs), "SpreadBps must be non-negative");
        if (inputs.OrderQuantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(inputs), "OrderQuantity must be positive");

        var halfSpreadBps = inputs.SpreadBps / 2m;

        decimal? participation = null;
        var impactBps = 0m;
        if (inputs.AvailableTopOfBookQuantity is { } depth && depth > 0m)
        {
            var raw = inputs.OrderQuantity / depth;
            participation = raw > 1m ? 1m : raw;
            // SqrtDecimal: convert to double for the sqrt then back. Loss is
            // sub-bp-irrelevant for slippage scoring.
            var sqrt = (decimal)Math.Sqrt((double)participation.Value);
            impactBps = _impactCoeffBps * sqrt;
        }

        var slippageBps = halfSpreadBps + impactBps;
        var sign = string.Equals(inputs.Side, Sides.Buy, StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
        var expectedPrice = inputs.MidPrice * (1m + sign * slippageBps / 10_000m);

        return new SlippageEstimate(
            ExpectedPrice:    expectedPrice,
            SlippageBps:      slippageBps,
            HalfSpreadBps:    halfSpreadBps,
            ImpactBps:        impactBps,
            ParticipationPct: participation);
    }

    /// Convert observed (actual) fill price to slippage in bps relative to
    /// <paramref name="referenceMid"/>. Sign convention matches
    /// <see cref="SlippageEstimate.SlippageBps"/>: positive = worse than mid.
    public static decimal ObservedSlippageBps(decimal referenceMid, decimal fillPrice, string side)
    {
        if (referenceMid <= 0m) throw new ArgumentOutOfRangeException(nameof(referenceMid));
        var sign = string.Equals(side, Sides.Buy, StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
        return sign * (fillPrice - referenceMid) / referenceMid * 10_000m;
    }
}
