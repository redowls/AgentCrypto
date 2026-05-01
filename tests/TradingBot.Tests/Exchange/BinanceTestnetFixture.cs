using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Time;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Binance;
using TradingBot.Exchange.DependencyInjection;
using TradingBot.Exchange.Resilience;
using TradingBot.Exchange.WebSocket;
using Xunit;

namespace TradingBot.Tests.Exchange;

/// Spin up a real DI container pointed at Binance Spot Testnet using env-var
/// API keys. We do NOT register the hosted services here (no key permission
/// verifier, no daily refresh) so each test controls its own lifecycle.
public sealed class BinanceTestnetFixture : IAsyncLifetime
{
    public IServiceProvider Services { get; private set; } = null!;
    private ServiceProvider? _root;

    public Task InitializeAsync()
    {
        var apiKey = Environment.GetEnvironmentVariable("BINANCE_TESTNET_API_KEY") ?? "";
        var apiSecret = Environment.GetEnvironmentVariable("BINANCE_TESTNET_API_SECRET") ?? "";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Binance:UseTestnet"] = "true",
                ["Binance:RecvWindowMs"] = "10000",
                ["Binance:RestTimeoutSeconds"] = "10",
                ["Binance:ApiKey"] = apiKey,
                ["Binance:ApiSecret"] = apiSecret,
            }).Build();

        var secrets = new ConfigSecrets(config);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddConsole());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretsProvider>(secrets);

        services.AddBinanceExchange(config, secrets);

        _root = services.BuildServiceProvider();
        Services = _root;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_root is not null) await _root.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class ConfigSecrets : ISecretsProvider
    {
        private readonly IConfiguration _config;
        public ConfigSecrets(IConfiguration config) => _config = config;
        public string GetRequired(string key) => _config[key] ?? throw new InvalidOperationException(key);
        public string? GetOptional(string key) => _config[key];
        public bool TryGet(string key, out string value)
        {
            value = _config[key] ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }
    }
}
