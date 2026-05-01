using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Skender.Stock.Indicators;
using TradingBot.Core.Domain;
using TradingBot.Core.Indicators;
using TradingBot.MarketData.Indicators;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Configuration;
using TradingBot.Strategies.Indicators;
using TradingBot.Strategies.Selection;
using TradingBot.Strategies.Strategies;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// End-to-end regression: generate a deterministic month of synthetic 1h BTC
/// candles, run the full §6 evaluation pipeline (indicator → regime → selector →
/// strategy.Evaluate) on every closed bar, and verify the emitted signals match
/// the frozen <c>SignalEngineRegression.json</c> fixture committed alongside.
///
/// First run / intentional change: set the env var <c>TRADINGBOT_REGEN_GOLDEN=1</c>
/// to overwrite the fixture instead of asserting against it. The replacement
/// lands in the test source tree (next to this file), so it's reviewable in the
/// PR diff.
///
/// Why this test exists: the per-strategy unit tests exercise individual gates;
/// this one nails down the *aggregate* behaviour across regime transitions
/// (drift up → chop → drift down). A typo in the regime classifier or selector
/// — one that doesn't trip a single-gate test — surfaces here as a count or
/// timestamp mismatch.
/// </summary>
public sealed class SignalEngineRegressionTests
{
    private const string GoldenFileName = "SignalEngineRegression.json";

    /// <summary>~30 days × 24 hours = 720 closed 1h bars.</summary>
    private const int OneHourBars = 720;

    private static readonly DateTime SeriesStart = new(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc);
    private const int BtcSymbolId = 1;
    private const string BtcSymbolCode = "BTCUSDT";

    [Fact]
    public void Pipeline_signals_match_frozen_golden_file()
    {
        var actual = RunPipeline();

        var goldenPath = ResolveGoldenPath();

        if (Environment.GetEnvironmentVariable("TRADINGBOT_REGEN_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, JsonSerializer.Serialize(actual, JsonWriteOptions));
            // Fail loudly so CI never silently regenerates.
            Assert.Fail($"Regenerated golden at {goldenPath}. Re-run without TRADINGBOT_REGEN_GOLDEN to assert.");
        }

        File.Exists(goldenPath).Should().BeTrue(
            $"golden fixture missing at {goldenPath}; rerun once with TRADINGBOT_REGEN_GOLDEN=1 to seed it");

        var expectedJson = File.ReadAllText(goldenPath);
        var expected = JsonSerializer.Deserialize<List<RegressionSignalRow>>(expectedJson, JsonReadOptions)
            ?? new List<RegressionSignalRow>();

        actual.Should().BeEquivalentTo(expected, opts => opts
            .WithStrictOrdering()
            .Using<decimal>(c => c.Subject.Should().BeApproximately(c.Expectation, 1e-6m))
            .WhenTypeIs<decimal>());
    }

    // --------------------------------------------------------------------
    // Pipeline
    // --------------------------------------------------------------------

    private static IReadOnlyList<RegressionSignalRow> RunPipeline()
    {
        var candles1h = BuildSynthetic1hCandles();
        var quotes1h  = candles1h.Select(ToQuote).ToList();

        // Aggregate 1h → 4h for the HTF feed (TREND_EMA_ADX needs 4h EMA200).
        var candles4h = Aggregate(candles1h, factor: 4);
        var quotes4h  = candles4h.Select(ToQuote).ToList();

        var classifier = new RegimeClassifier();

        // Strategies share the same options & loggers across bars.
        var breakout = new BreakoutDonchianStrategy(
            new StaticOptionsMonitor<BreakoutDonchianOptions>(new BreakoutDonchianOptions()),
            NullLogger<BreakoutDonchianStrategy>.Instance);
        var meanRev = new MeanReversionBbVwapStrategy(
            new StaticOptionsMonitor<MeanReversionBbVwapOptions>(new MeanReversionBbVwapOptions()),
            NullLogger<MeanReversionBbVwapStrategy>.Instance);
        var trend = new TrendEmaAdxStrategy(
            new StaticOptionsMonitor<TrendEmaAdxOptions>(new TrendEmaAdxOptions()),
            NullLogger<TrendEmaAdxStrategy>.Instance);

        var selector = new StrategySelector(
            new IStrategy[] { breakout, meanRev, trend },
            NullLogger<StrategySelector>.Instance);

        // Pre-compute indicator snapshots over the rolling window. We compute
        // once per bar boundary and cache the prior snapshot for the EMA cross.
        var rows = new List<RegressionSignalRow>();
        IndicatorSnapshot? priorSnap = null;

        // Warm-up: skip until at least 200 closed 1h bars (EMA200) and 200 closed
        // 4h bars are unavailable — but 720/4 = 180 4h bars is still tight.
        // The HTF EMA200 may stay null for the entire window; the trend strategy
        // will then never fire, which is the documented behaviour.
        const int warmupBars = 250;

        for (var i = warmupBars; i < quotes1h.Count; i++)
        {
            var window  = quotes1h.GetRange(0, i + 1);
            var sessionStart = quotes1h[i].Date.Date;
            var snap = IndicatorComputer.Compute(window, sessionStart);

            // HTF snapshot at the most recent 4h bar that has closed at or before
            // the 1h bar's open time. Since 4h bars are aggregated from the same
            // 1h source, the alignment is exact.
            var htfBarsClosed = quotes4h.Where(q => q.Date <= snap.AsOfUtc).ToList();
            IndicatorSnapshot? htfSnap = null;
            if (htfBarsClosed.Count > 0)
            {
                htfSnap = IndicatorComputer.Compute(htfBarsClosed, htfBarsClosed[^1].Date.Date);
            }

            var classification = selector_GetActiveAtRegime(classifier, selector, snap);
            if (classification.assignments.Count == 0)
            {
                priorSnap = snap;
                continue;
            }

            var ctx = BuildContext(candles1h, i, priorSnap, candles4h);

            foreach (var assignment in classification.assignments)
            {
                if (!string.Equals(assignment.Strategy.PrimaryTimeframe, "1h", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Array.IndexOf(assignment.Strategy.AllowedRegimes, classification.regime.Regime) < 0)
                    continue;

                var candidate = assignment.Strategy.Evaluate(snap, htfSnap, classification.regime.Regime, ctx);
                if (candidate is null) continue;

                rows.Add(new RegressionSignalRow(
                    BarOpenTime:  candles1h[i].OpenTime,
                    Strategy:     candidate.StrategyCode,
                    Side:         candidate.Side,
                    Regime:       RegimeCodes.ToCode(classification.regime.Regime),
                    EntryPrice:   Round8(candidate.EntryPrice),
                    StopLoss:     Round8(candidate.StopLoss),
                    TakeProfit:   Round8(candidate.TakeProfit),
                    AtrValue:     Round8(candidate.AtrValue),
                    Confidence:   Round8(candidate.Confidence),
                    SizeMultiplier: assignment.SizeMultiplier));
            }

            priorSnap = snap;
        }

        return rows;
    }

    private static (RegimeClassification regime, IReadOnlyList<StrategyAssignment> assignments)
        selector_GetActiveAtRegime(
            RegimeClassifier classifier, StrategySelector selector, IndicatorSnapshot snap)
    {
        var c = classifier.Classify(snap);
        return (c, selector.GetActive(c.Regime));
    }

    private static MarketContext BuildContext(
        IReadOnlyList<Candle> candles, int idx, IndicatorSnapshot? priorSnap,
        IReadOnlyList<Candle> candles4h)
    {
        var bar = candles[idx];

        decimal? volSma20 = null;
        if (idx >= 19)
        {
            decimal sum = 0m;
            for (var k = idx - 19; k <= idx; k++) sum += candles[k].Volume;
            volSma20 = sum / 20m;
        }

        decimal? close3 = idx >= 3 ? candles[idx - 3].Close : null;

        decimal? htfClose = candles4h
            .Where(c => c.OpenTime <= bar.OpenTime)
            .Select(c => (decimal?)c.Close)
            .LastOrDefault();

        return new MarketContext(
            SymbolId:        BtcSymbolId,
            SymbolCode:      BtcSymbolCode,
            PrimaryInterval: "1h",
            BarOpenTime:     bar.OpenTime,
            BarOpen:         bar.Open,
            BarHigh:         bar.High,
            BarLow:          bar.Low,
            BarClose:        bar.Close,
            BarVolume:       bar.Volume,
            VolumeSma20:     volSma20,
            Close3BarsAgo:   close3,
            PriorSnapshot:   priorSnap,
            HtfBarClose:     htfClose,
            NowUtc:          bar.CloseTime);
    }

    // --------------------------------------------------------------------
    // Synthetic series
    // --------------------------------------------------------------------

    /// <summary>
    /// Three regimes in 720 bars, sized to actually fire signals:
    ///   bars   0..239: aggressive uptrend with periodic volume spikes
    ///                  (TRENDING_UP / breakout zone)
    ///   bars 240..479: range chop with sharp band-piercing dips/peaks
    ///                  (RANGING / mean-reversion zone)
    ///   bars 480..719: aggressive downtrend
    ///                  (TRENDING_DOWN zone)
    ///
    /// Pattern is 100% deterministic — no PRNG — so the golden file is stable
    /// across machines.
    /// </summary>
    private static IReadOnlyList<Candle> BuildSynthetic1hCandles()
    {
        const decimal startPrice = 30_000m;
        var candles = new List<Candle>(OneHourBars);
        decimal price = startPrice;

        for (int i = 0; i < OneHourBars; i++)
        {
            decimal step;
            decimal range;
            decimal volume;
            if (i < 240)
            {
                // Strong uptrend with occasional acceleration bars to trigger
                // Donchian breakouts (every ~25 bars: 2× step, 2× volume).
                var accel = (i % 25 == 0) ? 2m : 1m;
                step   = 60m * accel + (decimal)Math.Sin(i / 9.0) * 10m;
                range  = 200m + (decimal)Math.Cos(i / 11.0) * 50m;
                volume = (100m + (decimal)Math.Abs(Math.Sin(i / 7.0)) * 60m) * accel;
            }
            else if (i < 480)
            {
                // Range chop around the level reached at i=239. Sharp swings
                // every ~20 bars produce RSI < 25 / > 75 + BB pierces.
                var spike = (i % 20 == 0)
                    ? -250m
                    : (i % 20 == 10) ? 250m : 0m;
                step   = (decimal)Math.Sin(i / 5.0) * 30m + spike;
                range  = 150m + (decimal)Math.Cos(i / 7.0) * 40m + Math.Abs(spike) / 2m;
                volume = 80m + (decimal)Math.Abs(Math.Cos(i / 6.0)) * 40m;
            }
            else
            {
                // Strong downtrend mirror of the uptrend.
                var accel = (i % 25 == 0) ? 2m : 1m;
                step   = -55m * accel + (decimal)Math.Sin(i / 8.0) * 10m;
                range  = 220m + (decimal)Math.Cos(i / 10.0) * 60m;
                volume = (110m + (decimal)Math.Abs(Math.Sin(i / 6.0)) * 70m) * accel;
            }

            var openTime  = SeriesStart.AddHours(i);
            var open      = price;
            var close     = price + step;
            var high      = Math.Max(open, close) + range / 2m;
            var low       = Math.Min(open, close) - range / 2m;
            price = close;

            candles.Add(new Candle
            {
                SymbolId    = BtcSymbolId,
                Interval    = "1h",
                OpenTime    = openTime,
                CloseTime   = openTime.AddHours(1),
                Open        = open,
                High        = high,
                Low         = low,
                Close       = close,
                Volume      = volume,
                QuoteVolume = volume * close,
                IsClosed    = true,
            });
        }
        return candles;
    }

    /// <summary>
    /// Aggregate consecutive 1h bars into 4h bars. We assume the input series
    /// is anchored on a 4h boundary (00:00 UTC) — true for our synthetic series.
    /// </summary>
    private static IReadOnlyList<Candle> Aggregate(IReadOnlyList<Candle> ascending, int factor)
    {
        var aggregated = new List<Candle>(ascending.Count / factor + 1);
        for (var i = 0; i + factor <= ascending.Count; i += factor)
        {
            var slice = ascending.Skip(i).Take(factor).ToList();
            var first = slice[0];
            var last  = slice[^1];
            aggregated.Add(new Candle
            {
                SymbolId    = first.SymbolId,
                Interval    = $"{factor}h",
                OpenTime    = first.OpenTime,
                CloseTime   = last.CloseTime,
                Open        = first.Open,
                High        = slice.Max(c => c.High),
                Low         = slice.Min(c => c.Low),
                Close       = last.Close,
                Volume      = slice.Sum(c => c.Volume),
                QuoteVolume = slice.Sum(c => c.QuoteVolume),
                IsClosed    = true,
            });
        }
        return aggregated;
    }

    private static Quote ToQuote(Candle c) => new()
    {
        Date   = DateTime.SpecifyKind(c.OpenTime, DateTimeKind.Utc),
        Open   = c.Open,
        High   = c.High,
        Low    = c.Low,
        Close  = c.Close,
        Volume = c.Volume,
    };

    private static decimal Round8(decimal v) => Math.Round(v, 8, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Resolve the test-source path for the golden fixture. We start from the
    /// test assembly's location (under <c>bin/Debug</c>) and walk upward to the
    /// project root, then dive into the source tree.
    /// </summary>
    private static string ResolveGoldenPath()
    {
        var asmDir = Path.GetDirectoryName(typeof(SignalEngineRegressionTests).Assembly.Location)!;
        // bin/Debug/net8.0 → up three to project root.
        var projectRoot = Path.GetFullPath(Path.Combine(asmDir, "..", "..", ".."));
        return Path.Combine(projectRoot, "Strategies", GoldenFileName);
    }

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Compact, JSON-friendly view of a generated signal. We persist only the
    /// fields that determine downstream behaviour — Reason / wall-clock /
    /// SignalId are excluded so test runs stay deterministic across machines.
    /// </summary>
    public sealed record RegressionSignalRow(
        DateTime BarOpenTime,
        string   Strategy,
        string   Side,
        string   Regime,
        decimal  EntryPrice,
        decimal  StopLoss,
        decimal  TakeProfit,
        decimal  AtrValue,
        decimal  Confidence,
        decimal  SizeMultiplier);
}
