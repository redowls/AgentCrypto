namespace TradingBot.AI.Abstractions;

/// <summary>
/// Daily $-cap tracker — counts USD spent on Claude calls since 00:00 UTC.
/// Implementation is process-local (no DB round-trip per call): the meter
/// auto-rolls over when <see cref="IClock"/> crosses a UTC date boundary.
///
/// On a multi-instance deployment the meter is per-process; the cap is
/// effectively (cap × instance_count). For a single-instance bot — the
/// only supported topology in §1 — this is the right tradeoff.
/// </summary>
public interface IAiCostMeter
{
    /// <summary>Returns true when the meter is below the daily cap.</summary>
    bool TryReserve(out decimal remainingBudgetUsd);

    /// <summary>Records actual spend after a call returns. Cache hits should
    /// pass <c>0</c> so the meter reflects real API spend only.</summary>
    void Record(decimal spentUsd);

    decimal SpentTodayUsd { get; }
    decimal DailyCapUsd   { get; }
}
