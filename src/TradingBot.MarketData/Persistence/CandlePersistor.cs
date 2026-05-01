using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.MarketData.Abstractions;
using TradingBot.MarketData.Caching;
using TradingBot.MarketData.Channels;
using TradingBot.MarketData.Configuration;
using TradingBot.MarketData.Indicators;
using TradingBot.MarketData.Ingestion;

namespace TradingBot.MarketData.Persistence;

/// <summary>
/// Drains the in-process kline channel into:
///   - dbo.Candles (canonical, IsClosed=1 only) via SqlBulkCopy → MERGE.
///   - LiveCandles cache (in-progress bars, IsClosed=0).
///   - Indicator pre-cache (closed bars, post-flush).
///
/// Buffering: closed bars are batched until either <c>FlushMaxRows</c> or
/// <c>FlushMaxAge</c> is hit, whichever first. A small slice of bars per flush
/// keeps SqlBulkCopy efficient while bounding latency to ≤ FlushMaxAge for
/// downstream strategies waiting on indicator snapshots.
///
/// Singleton enforcement: when <see cref="MarketDataOptions.UseSingletonMutex"/>
/// is set, the service tries to acquire a system-wide named mutex on startup.
/// If another process already holds it, the service refuses to run rather
/// than corrupting the canonical table by writing concurrent batches.
/// </summary>
public sealed class CandlePersistor : BackgroundService
{
    private readonly IKlineChannel _channel;
    private readonly IBarCloseChannel _barCloseChannel;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILiveCandleCache _liveCache;
    private readonly IndicatorPreCacheService _indicatorPreCache;
    private readonly IClock _clock;
    private readonly MarketDataOptions _options;
    private readonly ILogger<CandlePersistor> _log;

    private Mutex? _singletonMutex;
    private bool _ownsMutex;

    public CandlePersistor(
        IKlineChannel channel,
        IBarCloseChannel barCloseChannel,
        IServiceScopeFactory scopes,
        ILiveCandleCache liveCache,
        IndicatorPreCacheService indicatorPreCache,
        IClock clock,
        IOptions<MarketDataOptions> options,
        ILogger<CandlePersistor> log)
    {
        _channel = channel;
        _barCloseChannel = barCloseChannel;
        _scopes = scopes;
        _liveCache = liveCache;
        _indicatorPreCache = indicatorPreCache;
        _clock = clock;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TryAcquireSingletonMutex())
        {
            _log.LogError(
                "CandlePersistor singleton mutex '{Mutex}' already held by another process; aborting host.",
                _options.SingletonMutexName);
            // Surface this as a fatal startup error so the host operator notices.
            throw new InvalidOperationException(
                $"Another CandlePersistor instance owns mutex '{_options.SingletonMutexName}'. " +
                "Stop the other process or disable MarketData:UseSingletonMutex.");
        }

        var accumulator = new CandleBatchAccumulator(_options.FlushMaxRows, _options.FlushMaxAge);
        // Track which (symbolId, interval) bars are present in the current
        // batch so the post-flush indicator pass can find them quickly.
        var pendingClosed = new List<(int SymbolId, string Symbol, string Interval, Candle Candle)>();

        _log.LogInformation(
            "CandlePersistor starting (max rows={MaxRows}, max age={MaxAge}, channel cap={ChanCap}).",
            _options.FlushMaxRows, _options.FlushMaxAge, _channel.Capacity);

        try
        {
            await ConsumeLoopAsync(accumulator, pendingClosed, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        finally
        {
            // Drain whatever is still buffered before we let the host stop.
            await FinalFlushAsync(accumulator, pendingClosed, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ConsumeLoopAsync(
        CandleBatchAccumulator accumulator,
        List<(int SymbolId, string Symbol, string Interval, Candle Candle)> pendingClosed,
        CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;

        while (await WaitForReadOrTimeoutAsync(reader, accumulator, stoppingToken).ConfigureAwait(false))
        {
            // Drain whatever is currently available without blocking.
            while (reader.TryRead(out var evt))
            {
                await HandleEventAsync(evt, accumulator, pendingClosed, stoppingToken).ConfigureAwait(false);
            }

            // After draining, check if a partial batch has aged out.
            var due = accumulator.FlushIfDue(_clock.UtcNow);
            if (due is { Count: > 0 })
            {
                await FlushAsync(due, pendingClosed, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Awaits new data on the channel OR a tick at the next flush deadline,
    /// whichever fires first. Returns false only when the writer has completed
    /// AND no further data can arrive — that's the persistor's clean exit signal.
    /// </summary>
    private async Task<bool> WaitForReadOrTimeoutAsync(
        ChannelReader<KlineEvent> reader,
        CandleBatchAccumulator accumulator,
        CancellationToken stoppingToken)
    {
        var deadline = accumulator.NextFlushDeadlineUtc;
        if (deadline is null)
        {
            // No buffered rows — wait indefinitely (or until shutdown).
            try { return await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false); }
            catch (ChannelClosedException) { return false; }
        }

        var remaining = deadline.Value - _clock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return true; // Already overdue — caller will flush.
        }

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadlineCts.CancelAfter(remaining);

        try
        {
            return await reader.WaitToReadAsync(deadlineCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Deadline hit; caller's flush-if-due path picks it up.
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    private async Task HandleEventAsync(
        KlineEvent evt,
        CandleBatchAccumulator accumulator,
        List<(int SymbolId, string Symbol, string Interval, Candle Candle)> pendingClosed,
        CancellationToken ct)
    {
        var candle = CandleMapper.ToCandle(evt);

        if (!candle.IsClosed)
        {
            // In-progress bar → live cache only. Spec: only IsClosed=1 in canonical.
            try
            {
                await _liveCache.SetAsync(evt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "Live-candle cache write failed for {Symbol}/{Interval}; continuing.",
                    evt.Symbol, evt.Interval);
            }
            return;
        }

        // Closed bar: queue for bulk MERGE + indicator recompute.
        pendingClosed.Add((evt.SymbolId, evt.Symbol, evt.Interval, candle));
        var fullBatch = accumulator.Add(candle, _clock.UtcNow);
        if (fullBatch is { Count: > 0 })
        {
            await FlushAsync(fullBatch, pendingClosed, ct).ConfigureAwait(false);
        }

        // Once a bar closes its corresponding live cache entry is stale; clear
        // it so consumers fall back to the canonical table. Best-effort.
        try { await _liveCache.RemoveAsync(evt.Symbol, evt.Interval, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Live-candle clear failed for {Symbol}/{Interval}; ignoring.", evt.Symbol, evt.Interval);
        }
    }

    private async Task FlushAsync(
        IReadOnlyList<Candle> batch,
        List<(int SymbolId, string Symbol, string Interval, Candle Candle)> pendingClosed,
        CancellationToken ct)
    {
        // BulkUpsert is idempotent (MERGE on natural key) so retries on transient
        // SQL errors are safe. We don't add explicit retry here — the connection
        // factory + DbUp pipeline already retries during migration; runtime SQL
        // failures will surface in the catch below and Quartz gap detector will
        // recover the missed bars on its next pass.
        await using var scope = _scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICandleRepository>();

        try
        {
            var rows = await repo.BulkUpsertAsync(batch, ct).ConfigureAwait(false);
            _log.LogDebug("Persisted batch: candles={Batch} merged={Rows}", batch.Count, rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex,
                "BulkUpsert failed for batch of {Count} candles; gap detector will retry.",
                batch.Count);
            pendingClosed.Clear();
            return;
        }

        // Recompute indicators for each just-committed bar. The persistor's
        // single-consumer guarantee means we serialise per-bar updates per
        // (symbol, interval), which preserves ordering for the rolling window.
        // After the indicator snapshot is fresh in cache, publish a bar-close
        // event so the S6 SignalEngine can evaluate strategies. We publish only
        // *after* the pre-cache call so any consumer that reads the cache sees
        // the snapshot for this bar already.
        foreach (var (symbolId, symbol, interval, candle) in pendingClosed)
        {
            await _indicatorPreCache
                .OnClosedBarAsync(symbolId, symbol, interval, candle, ct)
                .ConfigureAwait(false);

            try
            {
                await _barCloseChannel.Writer
                    .WriteAsync(new BarClosedEvent(symbolId, symbol, interval, candle), ct)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Reader gone — host is shutting down. Drop the notification.
                break;
            }
        }
        pendingClosed.Clear();
    }

    private async Task FinalFlushAsync(
        CandleBatchAccumulator accumulator,
        List<(int SymbolId, string Symbol, string Interval, Candle Candle)> pendingClosed,
        CancellationToken ct)
    {
        var tail = accumulator.FlushNow();
        if (tail is { Count: > 0 })
        {
            _log.LogInformation("CandlePersistor draining final batch of {Count} candles.", tail.Count);
            await FlushAsync(tail, pendingClosed, ct).ConfigureAwait(false);
        }
    }

    private bool TryAcquireSingletonMutex()
    {
        if (!_options.UseSingletonMutex) return true;

        try
        {
            _singletonMutex = new Mutex(initiallyOwned: false, _options.SingletonMutexName);
            // Wait briefly so a fast-restart scenario doesn't false-trip on the
            // outgoing process's still-held handle.
            _ownsMutex = _singletonMutex.WaitOne(TimeSpan.FromSeconds(2));
            return _ownsMutex;
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died without releasing; we still own it now.
            _ownsMutex = true;
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            // "Global\..." mutex needs admin on some Windows policies — if we
            // can't open it, fall back to assuming single-instance and warn.
            _log.LogWarning(ex,
                "Could not access named mutex '{Mutex}'; running without singleton enforcement.",
                _options.SingletonMutexName);
            return true;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_singletonMutex is not null)
        {
            try
            {
                if (_ownsMutex) _singletonMutex.ReleaseMutex();
                _singletonMutex.Dispose();
            }
            catch (ApplicationException)
            {
                // Thread that acquired wasn't this thread — Mutex requires
                // release on the same thread that called WaitOne. Disposing
                // is safe regardless.
                _singletonMutex.Dispose();
            }
            _singletonMutex = null;
        }
    }
}
