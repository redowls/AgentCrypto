using TradingBot.AI.Abstractions;

namespace TradingBot.AI.XgBoost;

/// <summary>
/// Phase-1 no-op stub. Returns 0.5 for every signal so the call site
/// behaves as if no model exists (size adjustment will resolve to neutral).
/// Replaced in Phase 2 by an XGBoostSharp-backed implementation that loads
/// the JSON model from disk and scores in &lt; 1ms.
/// </summary>
public sealed class NoopXgbSignalFilter : IXgbSignalFilter
{
    public Task<double> ScoreAsync(IReadOnlyDictionary<string, double> features, CancellationToken cancellationToken)
        => Task.FromResult(0.5);
}

/// <summary>
/// Phase-1 stub. The training pipeline will be filled in Phase 2 — see
/// §5.3 in the design doc for the feature set + training procedure.
/// </summary>
public sealed class NoopXgbTrainingPipeline : IXgbTrainingPipeline
{
    public Task TrainAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
        => throw new NotImplementedException(
            "XGBoost training pipeline is a Phase-2 deliverable; see §5.3 in the design doc.");
}
