using TradingBot.Core.Domain;

namespace TradingBot.MarketData.Persistence;

/// <summary>
/// Pure in-memory buffer that decides when to flush. Holds at most
/// <c>maxRows</c> closed candles and flushes whenever either the row cap is
/// reached or the wall-clock age of the oldest buffered row exceeds
/// <c>maxAge</c>. The clock is injected from the caller (consumer loop) so
/// unit tests can drive deterministic time without spinning <c>Thread.Sleep</c>.
///
/// Thread-safety: not thread-safe. The persistor pipes a single channel reader
/// into this accumulator from a single consumer task — no locking needed.
/// </summary>
public sealed class CandleBatchAccumulator
{
    private readonly int _maxRows;
    private readonly TimeSpan _maxAge;
    private readonly List<Candle> _buffer;
    private DateTime? _firstAddedUtc;

    public CandleBatchAccumulator(int maxRows, TimeSpan maxAge)
    {
        if (maxRows <= 0) throw new ArgumentOutOfRangeException(nameof(maxRows));
        if (maxAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxAge));
        _maxRows = maxRows;
        _maxAge = maxAge;
        _buffer = new List<Candle>(capacity: maxRows);
    }

    public int Count => _buffer.Count;

    /// <summary>The wall-clock time the oldest item in the current batch was
    /// added, or null if the buffer is empty.</summary>
    public DateTime? FirstAddedUtc => _firstAddedUtc;

    /// <summary>
    /// Adds a candle. If the resulting buffer reaches <c>maxRows</c>, drains it
    /// and returns the snapshot. Otherwise returns null.
    /// </summary>
    public IReadOnlyList<Candle>? Add(Candle candle, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(candle);

        _buffer.Add(candle);
        _firstAddedUtc ??= nowUtc;

        return _buffer.Count >= _maxRows ? Drain() : null;
    }

    /// <summary>
    /// Returns a snapshot to flush if the oldest buffered row is older than
    /// <c>maxAge</c> at <paramref name="nowUtc"/>; null otherwise. Used by the
    /// consumer loop's tick path to flush partial batches on a timer.
    /// </summary>
    public IReadOnlyList<Candle>? FlushIfDue(DateTime nowUtc)
    {
        if (_buffer.Count == 0 || _firstAddedUtc is null) return null;
        if (nowUtc - _firstAddedUtc.Value < _maxAge) return null;
        return Drain();
    }

    /// <summary>Force-drains regardless of size or age. Used on shutdown.</summary>
    public IReadOnlyList<Candle>? FlushNow() => _buffer.Count == 0 ? null : Drain();

    /// <summary>
    /// Computes the next deadline at which a flush is due, or null if the
    /// buffer is empty. The consumer loop uses this to size its
    /// <c>WaitToReadAsync</c> timeout so the partial-batch latency cap is
    /// honoured even when no further events arrive.
    /// </summary>
    public DateTime? NextFlushDeadlineUtc =>
        _firstAddedUtc is { } start ? start + _maxAge : null;

    private IReadOnlyList<Candle> Drain()
    {
        var snapshot = _buffer.ToArray();
        _buffer.Clear();
        _firstAddedUtc = null;
        return snapshot;
    }
}
