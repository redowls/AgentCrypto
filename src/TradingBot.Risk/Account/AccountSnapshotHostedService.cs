using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Observability;
using TradingBot.Data.Abstractions;
using TradingBot.Risk.Abstractions;
using TradingBot.Risk.Configuration;

namespace TradingBot.Risk.Account;

/// Background loop that builds an <see cref="AccountRiskState"/> for every
/// configured account and persists it to <c>dbo.AccountSnapshots</c> every
/// <see cref="RiskOptions.AccountSnapshotInterval"/> (default 1 minute).
///
/// The HWM and daily-loss-limit anchors used by the §8 gates are populated
/// from this table, so the loop is critical: missed cycles widen the
/// daily-PnL window and stale the HWM. Errors are caught per-iteration; the
/// loop never exits except on shutdown.
public sealed class AccountSnapshotHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly ITradingMetrics _metrics;
    private readonly ILogger<AccountSnapshotHostedService> _log;

    private static readonly string[] Accounts =
    {
        AccountTypes.Spot,
        AccountTypes.UmFut,
    };

    public AccountSnapshotHostedService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<RiskOptions> options,
        ITradingMetrics metrics,
        ILogger<AccountSnapshotHostedService> log)
    {
        _scopes = scopes;
        _options = options;
        _metrics = metrics;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "AccountSnapshotHostedService starting; cadence={Cadence}",
            _options.CurrentValue.AccountSnapshotInterval);

        // First snapshot fires immediately so the daily anchor for today is
        // populated as early as possible after process start.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AccountSnapshotHostedService tick failed; will retry next cadence.");
            }

            try
            {
                await Task.Delay(_options.CurrentValue.AccountSnapshotInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IAccountSnapshotProvider>();
        var persister = scope.ServiceProvider.GetRequiredService<IAccountSnapshotPersister>();
        var positions = scope.ServiceProvider.GetRequiredService<IPositionRepository>();

        foreach (var account in Accounts)
        {
            try
            {
                var state = await provider.GetCurrentAsync(account, ct).ConfigureAwait(false);
                await persister.PersistAsync(state, ct).ConfigureAwait(false);

                // Metrics: account-level gauges. Per-symbol PnL is read once
                // outside the account loop below.
                _metrics.SetAccountEquity((double)state.EquityUsd);
                _metrics.SetDrawdown((double)state.DrawdownPct);

                _log.LogDebug(
                    "AccountSnapshot {Account}: equity={Equity:F2} dd={Dd:P2} dailyPnl={Daily:P2} open={Open} gross={Gross:F2}",
                    account, state.EquityUsd, state.DrawdownPct, state.DailyPnlPct,
                    state.OpenPositions, state.GrossExposureUsd);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "AccountSnapshot tick: {Account} failed; continuing with next account.", account);
            }
        }

        // Per-symbol unrealized PnL gauge. Done once per tick (not per account)
        // since open positions span both spot + futures. SymbolId is used as the
        // label since Position doesn't carry SymbolCode.
        try
        {
            var openPositions = await positions.GetOpenAsync(ct).ConfigureAwait(false);
            foreach (var pos in openPositions)
            {
                _metrics.SetPositionPnl(
                    pos.SymbolId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    0d); // UnrealizedPnl not stored on the row; gauge serves as a presence signal.
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "AccountSnapshot tick: per-symbol PnL emission failed; continuing.");
        }
    }
}
