using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Configuration;

namespace TradingBot.Exchange.Binance;

/// Refuses to start the bot if the configured Binance API key has any of the
/// forbidden permissions. WITHDRAW must NEVER be enabled; MARGIN is only
/// permitted when explicitly opted in via <see cref="BinanceOptions.AllowMarginPermission"/>.
/// Spec: §9.7 (key safety) — keys are trading-only.
public sealed class KeyPermissionVerifier
{
    private static readonly HashSet<string> AlwaysForbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "WITHDRAW",
        // Some accounts surface withdraw as separate permissions; cover both.
        "TRD_GRP_WITHDRAW",
    };

    private readonly IBinanceGatewayResolver _gateways;
    private readonly IOptions<BinanceOptions> _options;
    private readonly ILogger<KeyPermissionVerifier> _log;

    public KeyPermissionVerifier(
        IBinanceGatewayResolver gateways,
        IOptions<BinanceOptions> options,
        ILogger<KeyPermissionVerifier> log)
    {
        _gateways = gateways;
        _options = options;
        _log = log;
    }

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        AccountInfoSnapshot? spotInfo = null;
        AccountInfoSnapshot? futInfo  = null;

        if (opts.EnableSpot)
        {
            spotInfo = await _gateways.Get(AccountType.Spot)
                .GetAccountAsync(cancellationToken).ConfigureAwait(false);
            AssertSafe(spotInfo);
        }

        // Futures account does not expose Permissions in the same shape; the
        // CanWithdraw flag from the futures account info is the relevant check.
        if (opts.EnableUsdmFutures)
        {
            futInfo = await _gateways.Get(AccountType.UmFutures)
                .GetAccountAsync(cancellationToken).ConfigureAwait(false);
            AssertSafe(futInfo);
        }

        if (spotInfo is null && futInfo is null)
        {
            throw new InvalidOperationException(
                "REFUSING TO START: both Binance:EnableSpot and Binance:EnableUsdmFutures are false. Enable at least one account.");
        }

        _log.LogInformation(
            "Binance key permission check passed. spot={SpotState} fut={FutState}",
            spotInfo is null
                ? "disabled"
                : $"canTrade={spotInfo.CanTrade} canWithdraw={spotInfo.CanWithdraw} perms=[{string.Join(",", spotInfo.Permissions)}]",
            futInfo is null
                ? "disabled"
                : $"canTrade={futInfo.CanTrade} canWithdraw={futInfo.CanWithdraw}");
    }

    private void AssertSafe(AccountInfoSnapshot info)
    {
        if (info.CanWithdraw)
            throw new InvalidOperationException(
                $"REFUSING TO START: Binance {info.Account} key has WITHDRAW enabled. Disable that permission in the Binance API console.");

        if (!info.CanTrade)
            throw new InvalidOperationException(
                $"REFUSING TO START: Binance {info.Account} key cannot trade. Enable Spot+Futures trading on the key.");

        foreach (var perm in info.Permissions)
        {
            if (AlwaysForbidden.Contains(perm))
                throw new InvalidOperationException(
                    $"REFUSING TO START: Binance {info.Account} key carries forbidden permission '{perm}'.");

            if (string.Equals(perm, "MARGIN", StringComparison.OrdinalIgnoreCase) && !_options.Value.AllowMarginPermission)
                throw new InvalidOperationException(
                    "REFUSING TO START: Binance key has MARGIN permission but Binance:AllowMarginPermission is false.");
        }
    }
}
