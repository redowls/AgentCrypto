namespace TradingBot.Core.Indicators;

/// <summary>
/// Output of <see cref="IRegimeClassifier"/>. <see cref="Confidence"/> is in
/// [0, 1] — interpreted as "how decisively did this snapshot satisfy the rule
/// set". <see cref="Inputs"/> records the inputs that drove the verdict so a
/// reviewer can reconstruct the call from <c>dbo.Regimes.Inputs</c>.
/// </summary>
public sealed record RegimeClassification(
    Regime Regime,
    decimal Confidence,
    IReadOnlyDictionary<string, decimal?> Inputs);
