using System.Diagnostics;
using Binance.Net.Interfaces.Clients;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Exchange.Binance;

public sealed class BinanceExchangePing(IBinanceRestClient restClient) : IExchangePing
{
    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var result = await restClient.SpotApi.ExchangeData
            .PingAsync(cancellationToken)
            .ConfigureAwait(false);
        sw.Stop();

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Binance ping failed: {result.Error?.Code} {result.Error?.Message}");
        }

        return sw.Elapsed;
    }
}
