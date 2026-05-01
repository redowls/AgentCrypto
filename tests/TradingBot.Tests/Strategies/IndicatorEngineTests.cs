using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TradingBot.Core.Domain;
using TradingBot.Core.Indicators;
using TradingBot.Data.Abstractions;
using TradingBot.MarketData.Caching;
using TradingBot.MarketData.Configuration;
using TradingBot.Strategies.Indicators;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// Drives the engine with mocked candle/symbol repos and an in-memory cache:
///  - cache hit ⇒ no candle read
///  - cache miss / different bar ⇒ candles loaded, IndicatorComputer fires
/// </summary>
public sealed class IndicatorEngineTests
{
    private const int    SymbolId    = 42;
    private const string SymbolCode  = "BTCUSDT";
    private const string Interval    = "1h";

    private static readonly DateTime BarOpen = new(2026, 04, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ICandleRepository> _candleRepo = new(MockBehavior.Strict);
    private readonly Mock<ISymbolRepository> _symbolRepo = new(MockBehavior.Strict);
    private readonly InMemoryIndicatorCache _cache = new();

    private IndicatorEngine BuildEngine()
    {
        _symbolRepo.Setup(r => r.GetByIdAsync(SymbolId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Symbol
            {
                SymbolId   = SymbolId,
                Exchange   = "BINANCE_SPOT",
                SymbolCode = SymbolCode,
                BaseAsset  = "BTC",
                QuoteAsset = "USDT",
                IsActive   = true,
            });

        var options = Options.Create(new MarketDataOptions
        {
            Subscriptions = Array.Empty<SubscriptionOptions>(),
            IndicatorWindowSize = 400,
        });

        return new IndicatorEngine(
            _cache, _candleRepo.Object, _symbolRepo.Object, options,
            NullLogger<IndicatorEngine>.Instance);
    }

    [Fact]
    public async Task GetSnapshotAsync_returns_cached_snapshot_when_bar_matches()
    {
        var snap = SyntheticSnapshot(BarOpen);
        await _cache.SetAsync(SymbolCode, Interval, snap, CancellationToken.None);

        var engine = BuildEngine();
        var result = await engine.GetSnapshotAsync(SymbolId, Interval, BarOpen, CancellationToken.None);

        result.Should().NotBeNull();
        result!.AsOfUtc.Should().Be(BarOpen);
        // Cache hit ⇒ candle repo must NOT have been called. MockBehavior.Strict
        // would have already failed if it was, but verify-no-other-calls is the
        // documented assertion.
        _candleRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSnapshotAsync_recomputes_from_candles_when_cache_is_cold()
    {
        // Build 250 closed candles ending at the requested asOf. Skender needs
        // ≥ 200 for EMA200; this is enough to populate every field.
        var candles = BuildSyntheticCandles(BarOpen, count: 250);
        _candleRepo.Setup(r => r.GetRangeAsync(SymbolId, Interval,
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candles);

        var engine = BuildEngine();
        var result = await engine.GetSnapshotAsync(SymbolId, Interval, BarOpen, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Atr14.Should().NotBeNull();
        result.Ema200.Should().NotBeNull();
        _candleRepo.Verify(r => r.GetRangeAsync(SymbolId, Interval,
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSnapshotAsync_recomputes_when_cache_holds_a_different_bar()
    {
        // Cache holds a snapshot from 2 hours earlier — caller asks for the
        // current bar, so the engine must recompute via the candle repo.
        var stale = SyntheticSnapshot(BarOpen.AddHours(-2));
        await _cache.SetAsync(SymbolCode, Interval, stale, CancellationToken.None);

        var candles = BuildSyntheticCandles(BarOpen, count: 250);
        _candleRepo.Setup(r => r.GetRangeAsync(SymbolId, Interval,
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candles);

        var engine = BuildEngine();
        var result = await engine.GetSnapshotAsync(SymbolId, Interval, BarOpen, CancellationToken.None);

        result.Should().NotBeNull();
        _candleRepo.Verify(r => r.GetRangeAsync(SymbolId, Interval,
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetHtfSnapshotAsync_uses_the_passed_higher_timeframe()
    {
        const string Htf = "4h";
        var snap = SyntheticSnapshot(BarOpen);
        await _cache.SetAsync(SymbolCode, Htf, snap, CancellationToken.None);

        var engine = BuildEngine();
        var result = await engine.GetHtfSnapshotAsync(SymbolId, Htf, BarOpen, CancellationToken.None);

        result.Should().NotBeNull();
        result!.AsOfUtc.Should().Be(BarOpen);
    }

    [Fact]
    public async Task GetSnapshotAsync_returns_null_when_symbol_unknown()
    {
        _symbolRepo.Reset();
        _symbolRepo.Setup(r => r.GetByIdAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Symbol?)null);

        var options = Options.Create(new MarketDataOptions { Subscriptions = Array.Empty<SubscriptionOptions>() });
        var engine = new IndicatorEngine(
            _cache, _candleRepo.Object, _symbolRepo.Object, options,
            NullLogger<IndicatorEngine>.Instance);

        var result = await engine.GetSnapshotAsync(99, Interval, BarOpen, CancellationToken.None);
        result.Should().BeNull();
    }

    // -------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------

    private static IndicatorSnapshot SyntheticSnapshot(DateTime asOf) => new(
        AsOfUtc: asOf,
        Atr14: 1m, Ema9: 1m, Ema21: 1m, Ema50: 1m, Ema200: 1m,
        Adx14: 25m, PlusDi14: 20m, MinusDi14: 20m, Rsi14: 50m,
        BbUpper: 1m, BbMid: 1m, BbLower: 1m, BbWidth: 0.05m,
        DonchianUpper: 1m, DonchianLower: 1m, VwapSession: 1m,
        Atr50Sma: 1m, BbWidthSma50: 0.05m, BbWidthPercentileRank: 0.5m,
        BbWidthPrev: 0.05m, AdxPrev: 25m);

    private static IReadOnlyList<Candle> BuildSyntheticCandles(DateTime endOpen, int count)
    {
        // 1h bars ending at endOpen (inclusive). Linear up-ramp with mild noise
        // → enough variance to populate ATR/ADX/RSI without producing a
        // degenerate flat series.
        var candles = new List<Candle>(count);
        for (var i = 0; i < count; i++)
        {
            var openTime = endOpen.AddHours(-(count - 1 - i));
            var price    = 30_000m + i * 5m + (decimal)Math.Sin(i / 7.0) * 50m;
            candles.Add(new Candle
            {
                SymbolId     = SymbolId,
                Interval     = Interval,
                OpenTime     = openTime,
                CloseTime    = openTime.AddHours(1),
                Open         = price,
                High         = price + 10m,
                Low          = price - 10m,
                Close        = price + 1m,
                Volume       = 100m + (decimal)(i % 17),
                QuoteVolume  = 0m,
                TradeCount   = 0,
                TakerBuyBase = 0m,
                IsClosed     = true,
            });
        }
        return candles;
    }
}
