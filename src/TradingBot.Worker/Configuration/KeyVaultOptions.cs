namespace TradingBot.Worker.Configuration;

public sealed class KeyVaultOptions
{
    public const string SectionName = "KeyVault";

    /// Set to enable Azure Key Vault as a configuration source. Leave empty in
    /// dev (User Secrets is used instead). DefaultAzureCredential is used for
    /// auth so the same code works for managed identity in prod and az-cli in
    /// staging.
    public string? Uri { get; init; }
}
