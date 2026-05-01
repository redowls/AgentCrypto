using Xunit;

namespace TradingBot.Tests.Exchange;

/// <summary>
/// xUnit fact attribute that auto-skips when the BINANCE_TESTNET env var is
/// not set to a truthy value. Pair with valid BINANCE_TESTNET_API_KEY +
/// BINANCE_TESTNET_API_SECRET to run the live smoke tests.
/// </summary>
public sealed class BinanceTestnetFactAttribute : FactAttribute
{
    public BinanceTestnetFactAttribute()
    {
        var flag = Environment.GetEnvironmentVariable("BINANCE_TESTNET");
        if (!IsTruthy(flag))
        {
            Skip = "BINANCE_TESTNET env var not set; skipping live testnet smoke test.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BINANCE_TESTNET_API_KEY")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BINANCE_TESTNET_API_SECRET")))
        {
            Skip = "BINANCE_TESTNET set but API key/secret env vars are missing.";
        }
    }

    private static bool IsTruthy(string? v) =>
        v is not null && (
            v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            v == "1" ||
            v.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
