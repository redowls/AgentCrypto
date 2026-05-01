using Microsoft.Extensions.Configuration;
using TradingBot.Core.Abstractions;

namespace TradingBot.Worker.Configuration;

/// IConfiguration-backed secrets provider. Whatever providers are registered
/// (User Secrets in dev, Key Vault in prod, env vars overriding both) feed
/// this through the standard configuration pipeline.
internal sealed class ConfigurationSecretsProvider(IConfiguration configuration) : ISecretsProvider
{
    public string GetRequired(string key)
    {
        var value = configuration[key];
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"Required secret '{key}' is not configured. " +
                "Set it via 'dotnet user-secrets set' (dev) or Key Vault (prod).");
        }
        return value;
    }

    public string? GetOptional(string key) => configuration[key];

    public bool TryGet(string key, out string value)
    {
        var raw = configuration[key];
        if (string.IsNullOrEmpty(raw))
        {
            value = string.Empty;
            return false;
        }
        value = raw;
        return true;
    }
}
