using TradingBot.AI.Abstractions;
using TradingBot.AI.Configuration;
using TradingBot.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace TradingBot.AI.Cost;

/// <summary>
/// In-memory per-process daily $-counter. Auto-rolls over at the next UTC
/// midnight observed by an incoming <see cref="TryReserve"/> or
/// <see cref="Record"/> call. Concurrency is guarded by a single lock — the
/// hot path is exactly one comparison + one read, so contention is irrelevant.
///
/// On a multi-instance topology the cap is per-process; the design doc
/// (single-instance bot) makes this acceptable. A Redis-backed shared counter
/// is a Phase-2 enhancement when we go multi-instance.
/// </summary>
internal sealed class DailyCostMeter : IAiCostMeter
{
    private readonly decimal _capUsd;
    private readonly IClock  _clock;
    private readonly object  _lock = new();

    private DateOnly _windowDayUtc;
    private decimal  _spentToday;

    public DailyCostMeter(IOptions<ClaudeOptions> options, IClock clock)
    {
        _capUsd       = options.Value.DailyCapUsd;
        _clock        = clock;
        _windowDayUtc = DateOnly.FromDateTime(_clock.UtcNow);
        _spentToday   = 0m;
    }

    public decimal SpentTodayUsd { get { lock (_lock) { RollOverIfNewDay(); return _spentToday; } } }

    public decimal DailyCapUsd => _capUsd;

    public bool TryReserve(out decimal remainingBudgetUsd)
    {
        lock (_lock)
        {
            RollOverIfNewDay();
            remainingBudgetUsd = _capUsd - _spentToday;
            return _spentToday < _capUsd;
        }
    }

    public void Record(decimal spentUsd)
    {
        if (spentUsd <= 0m) return;
        lock (_lock)
        {
            RollOverIfNewDay();
            _spentToday += spentUsd;
        }
    }

    private void RollOverIfNewDay()
    {
        var today = DateOnly.FromDateTime(_clock.UtcNow);
        if (today != _windowDayUtc)
        {
            _windowDayUtc = today;
            _spentToday   = 0m;
        }
    }
}
