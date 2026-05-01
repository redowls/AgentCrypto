using TradingBot.Exchange.Abstractions;

namespace TradingBot.Exchange.Binance;

public sealed class BinanceGatewayResolver(
    BinanceSpotGateway spot,
    BinanceFuturesGateway futures) : IBinanceGatewayResolver
{
    public IBinanceGateway Get(AccountType account) => account switch
    {
        AccountType.Spot      => spot,
        AccountType.UmFutures => futures,
        _ => throw new ArgumentOutOfRangeException(nameof(account), account, null),
    };
}
