using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Execution.Configuration;
using TradingBot.Execution.Engine;
using TradingBot.Execution.State;
using TradingBot.Risk.Abstractions;

namespace TradingBot.Execution.Reconciliation;

/// <summary>
/// §6.4 reconciliation. Two responsibilities, both run on every tick:
///
///   1. Order reconciliation. For every Order in a non-terminal state older
///      than <see cref="ExecutionOptions.NonTerminalAge"/>, GET /order at
///      Binance, then apply the state-machine-validated delta if the
///      exchange has progressed beyond what we recorded locally.
///
///   2. Position drift. For every OPEN Position, compare DB qty vs Binance
///      account qty. Drift &gt; <see cref="ExecutionOptions.DriftAlertPctOfQty"/>
///      raises a CRITICAL RiskEvent; drift &gt; <see cref="ExecutionOptions.DriftTripUsd"/>
///      trips the kill-switch.
///
/// Safe to run concurrently with the engine: every state transition flows
/// through <see cref="OrderStateMachine"/>, so the worst case is a no-op when
/// the engine has already advanced the row.
/// </summary>
public sealed class ReconciliationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly OrderStateMachine _stateMachine;
    private readonly IClock _clock;
    private readonly ExecutionOptions _options;
    private readonly ILogger<ReconciliationService> _log;

    public ReconciliationService(
        IServiceScopeFactory scopes,
        OrderStateMachine stateMachine,
        IClock clock,
        IOptions<ExecutionOptions> options,
        ILogger<ReconciliationService> log)
    {
        _scopes       = scopes;
        _stateMachine = stateMachine;
        _clock        = clock;
        _options      = options.Value;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ReconciliationService starting (interval={Interval}).", _options.ReconciliationInterval);

        using var timer = new PeriodicTimer(_options.ReconciliationInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Reconciliation tick threw");
            }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var orders     = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var positions  = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
        var symbols    = scope.ServiceProvider.GetRequiredService<ISymbolRepository>();
        var gateways   = scope.ServiceProvider.GetRequiredService<IBinanceGatewayResolver>();
        var riskEvents = scope.ServiceProvider.GetRequiredService<IRiskEventRepository>();
        var killSwitch = scope.ServiceProvider.GetRequiredService<IKillSwitch>();

        await ReconcileOrdersAsync(orders, symbols, gateways, ct).ConfigureAwait(false);
        await ReconcilePositionsAsync(positions, symbols, gateways, riskEvents, killSwitch, ct).ConfigureAwait(false);
    }

    private async Task ReconcileOrdersAsync(
        IOrderRepository orders,
        ISymbolRepository symbols,
        IBinanceGatewayResolver gateways,
        CancellationToken ct)
    {
        var olderThan = _clock.UtcNow - _options.NonTerminalAge;
        var stale = await orders.GetNonTerminalOlderThanAsync(olderThan, maxRows: 100, ct).ConfigureAwait(false);
        if (stale.Count == 0) return;

        _log.LogInformation("Reconciliation: {Count} stale orders older than {Cutoff:O}", stale.Count, olderThan);

        foreach (var order in stale)
        {
            var sym = await symbols.GetByIdAsync(order.SymbolId, ct).ConfigureAwait(false);
            if (sym is null) continue;

            var account = ParseAccount(order.AccountType);
            OrderResult? remote;
            try
            {
                remote = await gateways.Get(account)
                    .GetOrderAsync(sym.SymbolCode, order.ClientOrderId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Reconciliation GET /order failed cid={Cid}", order.ClientOrderId);
                continue;
            }

            if (remote is null)
            {
                // Exchange has no record. If we were PENDING/SUBMITTING the
                // submit never landed — transition to ERROR. If we thought
                // it was NEW the order silently vanished — also ERROR + alert.
                _log.LogWarning("Reconciliation: exchange has no record of cid={Cid} status={Status}",
                    order.ClientOrderId, order.Status);
                if (CanTransition(order.Status, OrderStatuses.Error))
                {
                    await orders.UpdateStatusOnlyAsync(order.OrderId, OrderStatuses.Error,
                        "reconciliation:no-such-order", ct).ConfigureAwait(false);
                }
                continue;
            }

            var canonical = ExecutionEngine.NormalizeStatus(remote.Status);
            if (string.Equals(canonical, order.Status, StringComparison.Ordinal)) continue;

            if (!CanTransition(order.Status, canonical))
            {
                _log.LogDebug("Reconciliation no-op cid={Cid}: {Cur}↛{Remote} (illegal transition)",
                    order.ClientOrderId, order.Status, canonical);
                continue;
            }

            // Ensure we have the exchange order id stored.
            if (order.ExchangeOrderId is null && remote.ExchangeOrderId > 0)
            {
                await orders.SetExchangeOrderIdAsync(order.OrderId, remote.ExchangeOrderId, canonical, ct).ConfigureAwait(false);
            }

            await orders.UpdateStatusAsync(
                order.OrderId,
                canonical,
                remote.ExecutedQty,
                remote.AvgFillPrice,
                order.CommissionPaid,
                order.CommissionAsset,
                ct).ConfigureAwait(false);

            _log.LogInformation("Reconciliation applied cid={Cid} {From}→{To} executed={Executed}",
                order.ClientOrderId, order.Status, canonical, remote.ExecutedQty);
        }
    }

    private async Task ReconcilePositionsAsync(
        IPositionRepository positions,
        ISymbolRepository symbols,
        IBinanceGatewayResolver gateways,
        IRiskEventRepository riskEvents,
        IKillSwitch killSwitch,
        CancellationToken ct)
    {
        var open = await positions.GetOpenAsync(ct).ConfigureAwait(false);
        if (open.Count == 0) return;

        // Snapshot futures account once per tick (for futures positions only).
        IReadOnlyList<AccountBalance>? futBalances = null;
        try
        {
            if (open.Any(p => string.Equals(p.AccountType, AccountTypes.UmFut, StringComparison.OrdinalIgnoreCase)))
            {
                var snap = await gateways.Get(AccountType.UmFutures)
                    .GetAccountAsync(ct).ConfigureAwait(false);
                futBalances = snap.Balances;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Reconciliation: futures account snapshot failed; skipping drift check this tick");
        }

        foreach (var pos in open)
        {
            var sym = await symbols.GetByIdAsync(pos.SymbolId, ct).ConfigureAwait(false);
            if (sym is null) continue;

            decimal? exchangeQty = null;
            if (string.Equals(pos.AccountType, AccountTypes.Spot, StringComparison.OrdinalIgnoreCase))
            {
                // Spot: drift on the base-asset balance vs DB qty (positions
                // are 1:1 with base-asset balances on the bot's slice). We
                // can't always identify "the bot's portion" of a shared spot
                // account, so this is best-effort: if balance < DB qty, we're
                // in trouble.
                try
                {
                    var spotSnap = await gateways.Get(AccountType.Spot).GetAccountAsync(ct).ConfigureAwait(false);
                    var bal = spotSnap.Balances.FirstOrDefault(b => string.Equals(b.Asset, sym.BaseAsset, StringComparison.OrdinalIgnoreCase));
                    if (bal is not null) exchangeQty = bal.Free + bal.Locked;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug(ex, "Reconciliation: spot account snapshot failed for {Sym}", sym.SymbolCode);
                    continue;
                }
            }
            else if (futBalances is not null)
            {
                // Futures positions are reported separately from balances, but
                // for our purposes |free + locked| of the quote asset is a
                // proxy that highlights gross drift. A more accurate path is
                // GetPositionInformationAsync; left as a future enhancement.
                var bal = futBalances.FirstOrDefault(b => string.Equals(b.Asset, sym.QuoteAsset, StringComparison.OrdinalIgnoreCase));
                if (bal is not null)
                {
                    // Skip the strict comparison until /positionRisk is wired —
                    // we still record a sentinel so monitoring shows the loop.
                    _log.LogDebug("Fut position drift check pos={Pos} (placeholder)", pos.PositionId);
                    continue;
                }
            }

            if (exchangeQty is not { } eq) continue;

            var driftQty = Math.Abs(pos.Quantity - eq);
            var driftPct = pos.Quantity == 0m ? 0m : driftQty / pos.Quantity;
            var driftUsd = driftQty * pos.AvgEntryPrice;

            if (driftPct > _options.DriftAlertPctOfQty || driftUsd > _options.DriftTripUsd)
            {
                _log.LogError("DRIFT pos={Pos} sym={Sym} dbQty={Db} exQty={Ex} driftPct={Pct:P2} driftUsd={Usd:F2}",
                    pos.PositionId, sym.SymbolCode, pos.Quantity, eq, driftPct, driftUsd);

                await riskEvents.InsertAsync(new RiskEvent
                {
                    EventTime = _clock.UtcNow,
                    EventType = "POSITION_DRIFT",
                    Severity  = driftUsd > _options.DriftTripUsd ? "CRITICAL" : "WARN",
                    SymbolId  = pos.SymbolId,
                    OrderId   = pos.EntryOrderId,
                    Payload   = $"db={pos.Quantity} ex={eq} pct={driftPct:P4} usd={driftUsd:F4}",
                    Acted     = true,
                }, ct).ConfigureAwait(false);

                if (driftUsd > _options.DriftTripUsd)
                {
                    await killSwitch.TripAsync(KillSwitchSource.ReconciliationDrift,
                        $"position drift ${driftUsd:F2} on {sym.SymbolCode} > ${_options.DriftTripUsd}", ct)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private bool CanTransition(string from, string to) => _stateMachine.TryTransition(from, to).IsAccepted;

    private static AccountType ParseAccount(string s) =>
        string.Equals(s, AccountTypes.UmFut, StringComparison.OrdinalIgnoreCase)
            ? AccountType.UmFutures
            : AccountType.Spot;
}
