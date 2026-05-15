namespace TradingBot.AI.Abstractions;

/// <summary>
/// §5.3 — second-stage local XGBoost classifier (signal → "trade"/"skip").
/// Phase 1 ships only the seam; the inference stub returns 0.5 for every
/// signal (no-op) and the training pipeline is a placeholder that throws
/// <see cref="NotImplementedException"/> with a clear "Phase 2" message.
///
/// When the real model lands, this contract will be the only seam used by
/// the strategy/risk layer — keeping it stable now means the integration
/// site is locked in.
/// </summary>
public interface IXgbSignalFilter
{
    /// <summary>
    /// Probability that the signal is a "trade" (vs "skip"). Range [0, 1].
    /// The §5.3 design caps the model's influence at ±20% of position size —
    /// callers apply that cap, not the model.
    /// </summary>
    Task<double> ScoreAsync(
        IReadOnlyDictionary<string, double> features,
        CancellationToken                   cancellationToken);
}

/// <summary>
/// Phase-2 training pipeline. The MVP ships the seam only; <see cref="TrainAsync"/>
/// throws <see cref="NotImplementedException"/> until the real pipeline lands.
/// </summary>
public interface IXgbTrainingPipeline
{
    Task TrainAsync(
        DateTime          fromUtc,
        DateTime          toUtc,
        CancellationToken cancellationToken);
}
