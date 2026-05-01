using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace TradingBot.Worker.Configuration;

internal static class SecretsConfiguration
{
    /// Adds Azure Key Vault as a configuration source when a vault URI is
    /// configured. Order matters: Key Vault is registered AFTER the JSON files
    /// and AFTER environment variables in the host builder, so values from KV
    /// override committed defaults but env vars (highest precedence) still win.
    /// In Development we use User Secrets instead.
    public static IConfigurationBuilder AddSecretsSources(
        this IConfigurationBuilder builder,
        IConfiguration current,
        string environmentName)
    {
        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            // User Secrets are added by WebApplication.CreateBuilder when the
            // UserSecretsId property is set in the .csproj — nothing to do.
            return builder;
        }

        var vaultUri = current[$"{KeyVaultOptions.SectionName}:Uri"];
        if (!string.IsNullOrWhiteSpace(vaultUri))
        {
            builder.AddAzureKeyVault(
                new Uri(vaultUri),
                new DefaultAzureCredential(includeInteractiveCredentials: false));
        }

        return builder;
    }
}
