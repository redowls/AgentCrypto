using Microsoft.Extensions.Logging;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Abstractions;

namespace TradingBot.Strategies.Selection;

/// <summary>
/// §3.4 regime → strategy router. The map is data-driven (the constructor scans
/// the registered <see cref="IStrategy"/> set and pairs them with the size table)
/// so adding a fourth strategy doesn't require touching the selector — register
/// it with a new <see cref="StrategyType"/> and update the §3.4 size table here.
///
/// Mapping (table from §3.4):
///   TrendingUp / TrendingDown ⇒ Trend (1.0×, primary), Breakout (1.0×, secondary)
///   Ranging                    ⇒ MeanReversion (1.0×)
///   Volatile                   ⇒ Breakout (0.5×) — half size, fakeouts costly
///   Compressing                ⇒ none
///   Unknown                    ⇒ none (defensive; classifier returns this when
///                                 the snapshot isn't warm enough for any rule)
///
/// The selector is a pure function of the regime — it never reads from I/O. We
/// keep it as a singleton so the bar-close hot path doesn't pay an allocation
/// per call.
/// </summary>
public sealed class StrategySelector : IStrategySelector
{
    private readonly Dictionary<StrategyType, IStrategy> _byType;
    private readonly ILogger<StrategySelector> _log;

    private static readonly IReadOnlyList<StrategyAssignment> Empty = Array.Empty<StrategyAssignment>();

    public StrategySelector(IEnumerable<IStrategy> strategies, ILogger<StrategySelector> log)
    {
        _log = log;
        _byType = strategies.ToDictionary(s => s.StrategyType);

        // Sanity check: every StrategyType referenced by the §3.4 table must have
        // a registered strategy. Missing one means a DI registration was forgotten.
        foreach (var required in new[] { StrategyType.Breakout, StrategyType.MeanReversion, StrategyType.Trend })
        {
            if (!_byType.ContainsKey(required))
            {
                _log.LogWarning(
                    "StrategySelector: no IStrategy registered for {StrategyType} — selector will skip routes that need it",
                    required);
            }
        }
    }

    public IReadOnlyList<StrategyAssignment> GetActive(Regime regime)
    {
        switch (regime)
        {
            case Regime.TrendingUp:
            case Regime.TrendingDown:
            {
                var list = new List<StrategyAssignment>(2);
                AddIfPresent(list, StrategyType.Trend,    1.0m);
                AddIfPresent(list, StrategyType.Breakout, 1.0m);
                return list;
            }

            case Regime.Ranging:
            {
                var list = new List<StrategyAssignment>(1);
                AddIfPresent(list, StrategyType.MeanReversion, 1.0m);
                return list;
            }

            case Regime.Volatile:
            {
                var list = new List<StrategyAssignment>(1);
                AddIfPresent(list, StrategyType.Breakout, 0.5m); // §3.4: half size
                return list;
            }

            case Regime.Compressing:
            case Regime.Unknown:
            default:
                return Empty;
        }
    }

    private void AddIfPresent(List<StrategyAssignment> list, StrategyType type, decimal sizeMult)
    {
        if (_byType.TryGetValue(type, out var strategy))
        {
            list.Add(new StrategyAssignment(strategy, sizeMult));
        }
    }
}
