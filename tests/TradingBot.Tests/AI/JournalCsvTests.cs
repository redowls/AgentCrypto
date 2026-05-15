using FluentAssertions;
using TradingBot.AI.Journal;
using TradingBot.Core.Domain;
using Xunit;

namespace TradingBot.Tests.AI;

public sealed class JournalCsvTests
{
    [Fact]
    public void BuildCsv_emits_header_and_per_trade_rows_with_signal_context()
    {
        var trades = new List<TradeHistory>
        {
            new()
            {
                TradeHistoryId = 1, PositionId = 100, SymbolId = 1, Strategy = "TREND_EMA_ADX",
                Side = "LONG", EntryTime = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
                ExitTime = new DateTime(2026, 5, 2, 14, 30, 0, DateTimeKind.Utc), HoldingMinutes = 1710,
                EntryPrice = 60_000m, ExitPrice = 62_500m, Quantity = 0.05m,
                GrossPnlUsd = 125m, FeesUsd = 1m, NetPnlUsd = 124m, RMultiple = 1.7m, ExitReason = "TP",
            },
        };
        var signals = new Dictionary<long, Signal>
        {
            [100] = new Signal
            {
                SignalId = 999, Strategy = "TREND_EMA_ADX", Regime = "TRENDING_UP",
                SentimentScore = 0.42m, AiConfidence = 0.78m, Confidence = 0.65m,
            },
        };

        var csv = PostTradeJournalist.BuildCsv(trades, signals);

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("PositionId,Strategy,Symbol,Side,EntryTime,ExitTime,");
        lines[0].Should().EndWith("RuleConfidence");
        lines[1].Should().StartWith("100,TREND_EMA_ADX,1,LONG,2026-05-01T10:00:00Z,2026-05-02T14:30:00Z,1710,");
        lines[1].Should().Contain(",TP,TRENDING_UP,0.42,0.78,0.65");
    }

    [Fact]
    public void IsoWeek_returns_iso_8601_year_and_week()
    {
        // 2026-05-08 is a Friday in ISO week 19 of 2026.
        var (y, w) = PostTradeJournalist.IsoWeek(new DateTime(2026, 5, 8, 0, 0, 0, DateTimeKind.Utc));
        y.Should().Be(2026);
        w.Should().Be(19);
    }
}
