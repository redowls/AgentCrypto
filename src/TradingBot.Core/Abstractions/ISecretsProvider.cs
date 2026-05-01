namespace TradingBot.Core.Abstractions;

/// Single seam through which all sensitive values are read.
/// In dev, backed by .NET User Secrets via IConfiguration.
/// In staging/prod, backed by Azure Key Vault (or AWS Secrets Manager) via IConfiguration.
/// Implementations must NEVER log, cache to disk, or echo secret values.
public interface ISecretsProvider
{
    string GetRequired(string key);
    string? GetOptional(string key);
    bool TryGet(string key, out string value);
}
