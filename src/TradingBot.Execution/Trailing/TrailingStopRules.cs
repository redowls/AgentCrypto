using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Strategies.Abstractions;

namespace TradingBot.Execution.Trailing;

/// <summary>
/// §4.4 trailing-stop math, expressed as pure functions so the test harness
/// can drive every strategy through every regime cleanly. Three rules:
///
///   1. <see cref="ComputeTrailedStop"/> — given the current bar's close
///      and ATR, return the candidate new stop price. Direction-aware.
///   2. <see cref="ShouldReplace"/> — only update when the new stop is
///      *better* (raises floor for long, lowers ceiling for short). Avoids
///      churning the bracket on noise.
///   3. <see cref="ShouldPartialTake"/> — Trend strategy only: the first
///      time the price reaches +R × initial-risk, take 50% off (move stop
///      to break-even is left to the next trail tick).
///   4. <see cref="ShouldTimeExit"/> — close the position when N bars elapse
///      without reaching +1R progress.
/// </summary>
public static class TrailingStopRules
{
    public static decimal ComputeTrailedStop(
        decimal closePrice,
        decimal atr14,
        decimal trailMultiplier,
        string positionSide)
    {
        if (closePrice <= 0m) throw new ArgumentOutOfRangeException(nameof(closePrice));
        if (atr14 < 0m)        throw new ArgumentOutOfRangeException(nameof(atr14));
        if (trailMultiplier <= 0m) throw new ArgumentOutOfRangeException(nameof(trailMultiplier));

        return string.Equals(positionSide, PositionSides.Long, StringComparison.Ordinal)
            ? closePrice - trailMultiplier * atr14
            : closePrice + trailMultiplier * atr14;
    }

    public static bool ShouldReplace(decimal currentStop, decimal candidateStop, string positionSide)
    {
        return string.Equals(positionSide, PositionSides.Long, StringComparison.Ordinal)
            ? candidateStop > currentStop
            : candidateStop < currentStop;
    }

    /// True when the position has reached +R × <paramref name="rMultiple"/> progress
    /// from the entry. R is the per-unit risk distance.
    public static bool ShouldPartialTake(
        Position position,
        decimal currentPrice,
        decimal rMultiple)
    {
        var risk = Math.Abs(position.AvgEntryPrice - position.StopLoss);
        if (risk <= 0m) return false;
        var progress = string.Equals(position.Side, PositionSides.Long, StringComparison.Ordinal)
            ? (currentPrice - position.AvgEntryPrice) / risk
            : (position.AvgEntryPrice - currentPrice) / risk;
        return progress >= rMultiple;
    }

    /// True when <paramref name="barsHeld"/> ≥ <paramref name="maxBars"/> AND the
    /// position has not yet reached +1R progress. The "+1R" tripwire matches
    /// the §4.4 specification: trades that haven't worked deserve to be cut.
    public static bool ShouldTimeExit(
        Position position,
        decimal currentPrice,
        int barsHeld,
        int maxBars)
    {
        if (maxBars <= 0) return false;
        if (barsHeld < maxBars) return false;
        var risk = Math.Abs(position.AvgEntryPrice - position.StopLoss);
        if (risk <= 0m) return true;
        var progress = string.Equals(position.Side, PositionSides.Long, StringComparison.Ordinal)
            ? (currentPrice - position.AvgEntryPrice) / risk
            : (position.AvgEntryPrice - currentPrice) / risk;
        return progress < 1m;
    }

    /// Map a strategy code to the (trailMultiplier, primaryTimeframe).
    public static (decimal TrailMult, string Timeframe) DefaultsFor(string strategy) =>
        strategy switch
        {
            StrategyCodes.BreakoutDonchian    => (1.5m, CandleIntervals.OneHour),
            StrategyCodes.MeanReversionBbVwap => (1.0m, CandleIntervals.FifteenMinutes),
            StrategyCodes.TrendEmaAdx         => (2.0m, CandleIntervals.OneHour),
            _ => (1.5m, CandleIntervals.OneHour),
        };
}
