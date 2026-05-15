using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TradingBot.Observability.Alerts.Configuration;

namespace TradingBot.Observability.Alerts;

/// <summary>
/// In-process dedup cache for alert fingerprints. Repeated fingerprints
/// within <see cref="AlertRoutingOptions.DedupWindow"/> are collapsed.
/// State is volatile — a process restart resets the cache.
/// </summary>
public sealed class AlertDedupCache
{
    private const int PruneThreshold = 10_000;
    private readonly ConcurrentDictionary<string, DateTime> _seen = new();
    private readonly TimeSpan _window;

    public AlertDedupCache(IOptions<AlertRoutingOptions> opts) => _window = opts.Value.DedupWindow;

    /// <summary>
    /// Returns true when the fingerprint was seen within the dedup window.
    /// Updates the timestamp to nowUtc when accepted (i.e. not duplicate).
    /// </summary>
    public bool IsDuplicate(string fingerprint, DateTime nowUtc)
    {
        if (_seen.Count > PruneThreshold) Prune(nowUtc);

        var added = false;
        _seen.AddOrUpdate(fingerprint,
            _ => { added = true; return nowUtc; },
            (_, prev) =>
            {
                if (nowUtc - prev < _window) return prev; // duplicate — keep prev
                added = true;
                return nowUtc;                            // window expired — refresh
            });
        return !added;
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var kv in _seen)
        {
            if (nowUtc - kv.Value >= _window)
                _seen.TryRemove(kv.Key, out _);
        }
    }
}
