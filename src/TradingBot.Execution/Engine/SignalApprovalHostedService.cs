using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Observability;
using TradingBot.Data.Abstractions;
using TradingBot.Execution.Channels;
using TradingBot.Execution.Configuration;
using TradingBot.Risk.Abstractions;
using TradingBot.Strategies.Channels;

namespace TradingBot.Execution.Engine;

/// <summary>
/// Bridges §6 → §7 → §8: pulls every <c>GENERATED</c> signal from
/// <see cref="IGeneratedSignalChannel"/>, asks <see cref="IRiskManager"/> for
/// a sized decision, updates the signal's status, and on APPROVE writes an
/// <see cref="ApprovedIntent"/> to the execution-engine channel.
///
/// Lives in TradingBot.Execution because:
///   • The risk manager is purely synchronous logic — it would feel
///     misplaced as its own hosted service.
///   • The output channel and the engine that drains it are both in this
///     project; co-locating the producer keeps the §8 wiring contained.
/// </summary>
public sealed class SignalApprovalHostedService : BackgroundService
{
    private readonly IGeneratedSignalChannel _generated;
    private readonly IApprovedIntentChannel _approved;
    private readonly IServiceScopeFactory _scopes;
    private readonly ExecutionOptions _options;
    private readonly ILogger<SignalApprovalHostedService> _log;

    public SignalApprovalHostedService(
        IGeneratedSignalChannel generated,
        IApprovedIntentChannel approved,
        IServiceScopeFactory scopes,
        IOptions<ExecutionOptions> options,
        ILogger<SignalApprovalHostedService> log)
    {
        _generated = generated;
        _approved  = approved;
        _scopes    = scopes;
        _options   = options.Value;
        _log       = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("SignalApprovalHostedService starting (intentChannel cap={Cap}).", _approved.Capacity);

        await foreach (var evt in _generated.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            using var _scope = SignalContext.BeginSignal(evt.Signal.SignalId);
            try
            {
                await ProcessAsync(evt, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Approval pipeline failed for signal {SignalId}", evt.Signal.SignalId);
            }
        }
    }

    private async Task ProcessAsync(GeneratedSignalEvent evt, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var risk     = scope.ServiceProvider.GetRequiredService<IRiskManager>();
        var snapshot = scope.ServiceProvider.GetRequiredService<IAccountSnapshotProvider>();
        var signals  = scope.ServiceProvider.GetRequiredService<ISignalRepository>();
        var symbols  = scope.ServiceProvider.GetRequiredService<ISymbolRepository>();

        var account = await ResolveAccountAsync(symbols, evt.Signal.SymbolId, ct).ConfigureAwait(false);
        var state   = await snapshot.GetCurrentAsync(account.AccountType, ct).ConfigureAwait(false);
        var decision = await risk.ApproveAsync(evt.Signal, state, ct).ConfigureAwait(false);

        if (!decision.Approved)
        {
            await signals.UpdateStatusAsync(evt.Signal.SignalId, SignalStatuses.Rejected,
                decision.RejectReason ?? "REJECTED", ct).ConfigureAwait(false);
            return;
        }

        await signals.UpdateStatusAsync(evt.Signal.SignalId, SignalStatuses.Approved,
            decision.Message, ct).ConfigureAwait(false);

        var intent = new ApprovedIntent(
            Signal:      evt.Signal,
            Quantity:    decision.Quantity,
            RiskUsd:     decision.RiskUsd ?? 0m,
            NotionalUsd: decision.NotionalUsd ?? 0m,
            AccountType: account.AccountType,
            SymbolCode:  account.SymbolCode);

        await _approved.Writer.WriteAsync(intent, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Intent queued corr={SignalId} sym={Sym} {Side} qty={Qty} notional={Notional}",
            evt.Signal.SignalId, account.SymbolCode, evt.Signal.Side,
            decision.Quantity, decision.NotionalUsd);
    }

    private async Task<(string AccountType, string SymbolCode)> ResolveAccountAsync(
        ISymbolRepository symbols, int symbolId, CancellationToken ct)
    {
        var sym = await symbols.GetByIdAsync(symbolId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"unknown symbol id {symbolId}");
        // The symbol's exchange row pins the account; signals routed via
        // BinanceUmFut go to the futures account, everything else to spot.
        var acct = string.Equals(sym.Exchange, Exchanges.BinanceUmFut, StringComparison.OrdinalIgnoreCase)
            ? AccountTypes.UmFut
            : _options.DefaultAccountType;
        return (acct, sym.SymbolCode);
    }
}
