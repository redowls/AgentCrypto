using System.ComponentModel.DataAnnotations;

namespace TradingBot.Worker.Configuration;

public sealed class BinanceOptions
{
    public const string SectionName = "Binance";

    /// True for testnet (default for non-Production environments).
    public bool UseTestnet { get; init; } = true;

    /// Spot vs Futures startup behaviour. Both true is allowed.
    public bool EnableSpot { get; init; } = true;
    public bool EnableUsdmFutures { get; init; } = true;

    [Range(1, 60)]
    public int RecvWindowSeconds { get; init; } = 5;
}
