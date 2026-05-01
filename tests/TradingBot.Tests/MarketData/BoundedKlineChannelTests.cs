using FluentAssertions;
using Microsoft.Extensions.Options;
using TradingBot.Exchange.Abstractions;
using TradingBot.MarketData.Abstractions;
using TradingBot.MarketData.Channels;
using TradingBot.MarketData.Configuration;
using Xunit;

namespace TradingBot.Tests.MarketData;

public sealed class BoundedKlineChannelTests
{
    private static IKlineChannel NewChannel(int capacity)
    {
        var opts = Options.Create(new MarketDataOptions { ChannelCapacity = capacity });
        return new BoundedKlineChannel(opts);
    }

    private static KlineEvent Evt(int seq) =>
        new(SymbolId: 1, Symbol: "BTCUSDT", Interval: "5m",
            Account: AccountType.Spot,
            Kline: new KlineData(
                OpenTimeUtc: new DateTime(2026, 4, 27, 12, 0, 0, DateTimeKind.Utc).AddMinutes(seq * 5),
                CloseTimeUtc: new DateTime(2026, 4, 27, 12, 5, 0, DateTimeKind.Utc).AddMinutes(seq * 5),
                Open: 1, High: 1, Low: 1, Close: 1,
                Volume: 1, QuoteVolume: 1, TradeCount: 1, TakerBuyBase: 0, IsClosed: true),
            Source: KlineSource.WebSocket);

    [Fact]
    public async Task Channel_BlocksWriter_WhenFullThenDrainsOnRead()
    {
        var ch = NewChannel(capacity: 2);

        // Fill to capacity.
        await ch.Writer.WriteAsync(Evt(0));
        await ch.Writer.WriteAsync(Evt(1));
        ch.CurrentCount.Should().Be(2);

        // Third write must block (back-pressure to producer).
        var blockedWrite = ch.Writer.WriteAsync(Evt(2)).AsTask();
        await Task.Delay(50);
        blockedWrite.IsCompleted.Should().BeFalse(
            "the writer must block on a full bounded channel — drop policy is Wait");

        // Drain one item; the blocked writer can now finish.
        var first = await ch.Reader.ReadAsync();
        first.Kline.OpenTimeUtc.Should().Be(Evt(0).Kline.OpenTimeUtc);

        await blockedWrite;
        ch.CurrentCount.Should().Be(2);
    }

    [Fact]
    public async Task Channel_PreservesOrderForSingleProducer()
    {
        var ch = NewChannel(capacity: 4);
        for (var i = 0; i < 4; i++) await ch.Writer.WriteAsync(Evt(i));

        for (var i = 0; i < 4; i++)
        {
            var got = await ch.Reader.ReadAsync();
            got.Kline.OpenTimeUtc.Should().Be(Evt(i).Kline.OpenTimeUtc);
        }
    }

    [Fact]
    public async Task Channel_CompletesReader_WhenWriterCompletes()
    {
        var ch = NewChannel(capacity: 2);
        await ch.Writer.WriteAsync(Evt(0));
        ch.Writer.Complete();

        var first = await ch.Reader.ReadAsync();
        first.Kline.OpenTimeUtc.Should().Be(Evt(0).Kline.OpenTimeUtc);

        var canRead = await ch.Reader.WaitToReadAsync();
        canRead.Should().BeFalse("after Complete and drain, the reader should report no more data");
    }
}
