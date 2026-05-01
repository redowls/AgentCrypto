namespace TradingBot.Core.Indicators;

/// <summary>
/// Pure (no-IO) regime classifier. The async overload exists for the S9 hook —
/// the AI confirmer composes over this same shape — but the rule-based core is
/// the synchronous <see cref="Classify"/> method, so unit tests can call it
/// directly without async ceremony.
/// </summary>
public interface IRegimeClassifier
{
    RegimeClassification Classify(IndicatorSnapshot snapshot);

    Task<RegimeClassification> ClassifyAsync(IndicatorSnapshot snapshot, CancellationToken cancellationToken);
}
