using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Exchange.ReferenceData;

public sealed class ReferenceDataService : IReferenceDataService
{
    private readonly IBinanceGatewayResolver _gateways;
    private readonly IServiceScopeFactory _scopes;
    private readonly SymbolFilters _filters;
    private readonly ILogger<ReferenceDataService> _log;
    private readonly ConcurrentDictionary<AccountType, DateTime> _lastRefresh = new();

    // ReferenceDataService is a singleton; ISymbolRepository is scoped (DB-backed).
    // Resolve it per refresh call to avoid capturing the scoped repo.
    public ReferenceDataService(
        IBinanceGatewayResolver gateways,
        IServiceScopeFactory scopes,
        SymbolFilters filters,
        ILogger<ReferenceDataService> log)
    {
        _gateways = gateways;
        _scopes = scopes;
        _filters = filters;
        _log = log;
    }

    public async Task<IReadOnlyList<ReferenceDataRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken)
    {
        var spotTask = RefreshAsync(AccountType.Spot, cancellationToken);
        var futTask  = RefreshAsync(AccountType.UmFutures, cancellationToken);
        await Task.WhenAll(spotTask, futTask).ConfigureAwait(false);
        return new[] { await spotTask, await futTask };
    }

    public async Task<ReferenceDataRefreshResult> RefreshAsync(AccountType account, CancellationToken cancellationToken)
    {
        var gateway = _gateways.Get(account);
        var info = await gateway.GetExchangeInfoAsync(cancellationToken).ConfigureAwait(false);

        var exchangeName = ExchangeName(account);
        var rows = info.Symbols.Select(s => new Symbol
        {
            Exchange    = exchangeName,
            SymbolCode  = s.SymbolCode,
            BaseAsset   = s.BaseAsset,
            QuoteAsset  = s.QuoteAsset,
            TickSize    = s.TickSize,
            StepSize    = s.StepSize,
            MinNotional = s.MinNotional,
            IsActive    = s.IsActive,
            UpdatedAt   = info.FetchedAtUtc,
        }).ToList();

        await using var scope = _scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISymbolRepository>();
        var counts = await repo.UpsertExchangeCatalogAsync(exchangeName, rows, cancellationToken).ConfigureAwait(false);

        // After persistence, push the active set into the in-memory filter cache.
        var active = rows.Where(r => r.IsActive).ToList();
        _filters.Replace(account, active);

        _lastRefresh[account] = info.FetchedAtUtc;
        var result = new ReferenceDataRefreshResult(account, counts.Inserted, counts.Updated, counts.Deactivated, info.FetchedAtUtc);

        _log.LogInformation(
            "Reference data refresh for {Account}: total={Total} inserted={Inserted} updated={Updated} deactivated={Deactivated} active={Active}",
            account, rows.Count, counts.Inserted, counts.Updated, counts.Deactivated, active.Count);

        return result;
    }

    public DateTime? LastRefreshUtc(AccountType account) =>
        _lastRefresh.TryGetValue(account, out var v) ? v : null;

    public IReadOnlyList<Symbol> Snapshot(AccountType account) => _filters.All(account);

    private static string ExchangeName(AccountType account) => account switch
    {
        AccountType.Spot      => Exchanges.BinanceSpot,
        AccountType.UmFutures => Exchanges.BinanceUmFut,
        _ => throw new ArgumentOutOfRangeException(nameof(account)),
    };
}
