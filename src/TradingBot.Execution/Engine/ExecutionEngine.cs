using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Observability;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Filters;
using TradingBot.Execution.Brackets;
using TradingBot.Execution.Channels;
using TradingBot.Execution.Identity;
using TradingBot.Execution.Slippage;
using TradingBot.Execution.State;
using TradingBot.Risk.Abstractions;

namespace TradingBot.Execution.Engine;

/// <summary>
/// §6 — entry-side execution engine. Consumes <see cref="ApprovedIntent"/> events
/// from <see cref="IApprovedIntentChannel"/> and:
///   1. Generates a deterministic clientOrderId per <see cref="ClientOrderIdGenerator"/>.
///   2. Persists an Orders row with <c>Status=PENDING</c> via the idempotent
///      <see cref="IOrderRepository.InsertIfNewAsync"/> — same SignalId never
///      produces two distinct exchange orders (the unique index on ClientOrderId
///      is the hard backstop).
///   3. Transitions PENDING → SUBMITTING and submits via the resolved gateway
///      under the existing Polly resilience pipeline.
///   4. On REST response: NEW/PARTIALLY_FILLED/FILLED → SetExchangeOrderId +
///      transition; REJECTED → log + insert RiskEvent.
///   5. On submit-side failures (network drop, killswitch tripped) → ERROR
///      with notes; reconciliation will probe and recover.
///
/// User-data WS reactions (fills, terminal transitions, bracket-leg handling,
/// position lifecycle) are owned by <see cref="UserDataReactor"/> — keeping
/// that loop separate from the submit loop avoids one path blocking the other.
/// </summary>
public sealed class ExecutionEngine : BackgroundService
{
    private readonly IApprovedIntentChannel _intents;
    private readonly IServiceScopeFactory _scopes;
    private readonly OrderStateMachine _stateMachine;
    private readonly IClock _clock;
    private readonly ITradingMetrics _metrics;
    private readonly ILogger<ExecutionEngine> _log;

    public ExecutionEngine(
        IApprovedIntentChannel intents,
        IServiceScopeFactory scopes,
        OrderStateMachine stateMachine,
        IClock clock,
        ILogger<ExecutionEngine> log,
        ITradingMetrics? metrics = null)
    {
        _intents      = intents;
        _scopes       = scopes;
        _stateMachine = stateMachine;
        _clock        = clock;
        _metrics      = metrics ?? new NullTradingMetrics();
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ExecutionEngine starting (intentChannel cap={Cap}).", _intents.Capacity);

        await foreach (var intent in _intents.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            using var _scope = SignalContext.BeginSignal(intent.Signal.SignalId);
            try
            {
                await SubmitAsync(intent, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "ExecutionEngine.SubmitAsync threw unexpectedly for signal {Sid}",
                    intent.Signal.SignalId);
            }
        }
    }

    internal async Task SubmitAsync(ApprovedIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);

        await using var scope = _scopes.CreateAsyncScope();
        var orders        = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var gateways      = scope.ServiceProvider.GetRequiredService<IBinanceGatewayResolver>();
        var symbols       = scope.ServiceProvider.GetRequiredService<ISymbolRepository>();
        var filters       = scope.ServiceProvider.GetRequiredService<ISymbolFilters>();
        var slippage      = scope.ServiceProvider.GetRequiredService<ISlippageModel>();
        var diagnostics   = scope.ServiceProvider.GetRequiredService<IExecutionDiagnosticsRepository>();
        var riskEvents    = scope.ServiceProvider.GetRequiredService<IRiskEventRepository>();
        var killSwitch    = scope.ServiceProvider.GetRequiredService<IKillSwitch>();

        var corr = intent.Signal.SignalId.ToString();

        // Pre-gate: kill-switch (the risk gate already checks but the engine
        // re-checks because there's a window between approval and submission).
        killSwitch.RefreshFromCache();
        if (killSwitch.IsTripped)
        {
            _log.LogWarning("Kill-switch tripped at submit time corr={Corr} reason={Reason}",
                corr, killSwitch.Reason);
            return;
        }

        var account = ParseAccount(intent.AccountType);
        var clientOrderId = ClientOrderIdGenerator.ForEntry(intent.Signal.Strategy, intent.Signal.SignalId);

        // 1) Persist PENDING (idempotent on ClientOrderId). If a row already
        //    exists we re-load it and short-circuit duplicate submission —
        //    the same SignalId never produces two exchange orders.
        var pending = new Order
        {
            SignalId        = intent.Signal.SignalId,
            SymbolId        = intent.Signal.SymbolId,
            AccountType     = intent.AccountType,
            ClientOrderId   = clientOrderId,
            OrderType       = OrderTypes.Market,
            Side            = intent.Signal.Side,
            PositionSide    = account == AccountType.UmFutures
                ? (string.Equals(intent.Signal.Side, Sides.Buy, StringComparison.Ordinal) ? PositionSides.Long : PositionSides.Short)
                : null,
            Quantity        = intent.Quantity,
            Status          = OrderStatuses.Pending,
            FilledQty       = 0m,
            CommissionPaid  = 0m,
        };
        await orders.InsertIfNewAsync(pending, ct).ConfigureAwait(false);

        // Re-load to discover the canonical state (in case a previous run
        // already submitted this clientOrderId).
        var loaded = await orders.GetByClientOrderIdAsync(clientOrderId, ct).ConfigureAwait(false)
                     ?? pending;
        if (_stateMachine.IsTerminal(loaded.Status))
        {
            _log.LogInformation("Submit short-circuit corr={Corr} cid={Cid} already-terminal={Status}",
                corr, clientOrderId, loaded.Status);
            return;
        }
        if (string.Equals(loaded.Status, OrderStatuses.Submitting, StringComparison.Ordinal) ||
            !string.Equals(loaded.Status, OrderStatuses.Pending, StringComparison.Ordinal))
        {
            // Another worker has it (Submitting / NEW / PARTIALLY_FILLED).
            _log.LogInformation("Submit short-circuit corr={Corr} cid={Cid} concurrent={Status}",
                corr, clientOrderId, loaded.Status);
            return;
        }

        // 2) PENDING → SUBMITTING.
        var transition = _stateMachine.TryTransition(loaded.Status, OrderStatuses.Submitting);
        if (!transition.IsAccepted)
        {
            _log.LogWarning("State machine refused PENDING→SUBMITTING corr={Corr} reason={Reason}",
                corr, transition.Reason);
            return;
        }
        await orders.UpdateStatusOnlyAsync(loaded.OrderId, OrderStatuses.Submitting, null, ct).ConfigureAwait(false);

        // 3) Snap qty to step (defence in depth — risk already clamps).
        var clampedQty = ClampQty(filters, account, intent.SymbolCode, intent.Quantity);
        if (clampedQty <= 0m)
        {
            await TransitionToTerminalAsync(orders, loaded.OrderId, OrderStatuses.Error,
                $"clamped qty<=0 for {intent.SymbolCode}", ct).ConfigureAwait(false);
            return;
        }

        // 4) Submit.
        var request = new OrderRequest
        {
            Account       = account,
            Symbol        = intent.SymbolCode,
            ClientOrderId = clientOrderId,
            Side          = intent.Signal.Side,
            OrderType     = OrderTypes.Market,
            Quantity      = clampedQty,
            ReduceOnly    = false,
            PositionSide  = pending.PositionSide,
            CorrelationId = corr,
        };

        OrderResult result;
        try
        {
            result = await gateways.Get(account).PlaceOrderAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Gateway placeOrder failed corr={Corr} cid={Cid}", corr, clientOrderId);
            await TransitionToTerminalAsync(orders, loaded.OrderId, OrderStatuses.Error,
                $"gateway:{ex.GetType().Name}: {Truncate(ex.Message, 240)}", ct).ConfigureAwait(false);
            return;
        }

        // 5) Apply REST result.
        var ackStatus = NormalizeStatus(result.Status);
        var ack = _stateMachine.TryTransition(OrderStatuses.Submitting, ackStatus);
        if (!ack.IsAccepted)
        {
            _log.LogError("Unexpected ack state corr={Corr} cid={Cid} ack={Ack} reason={Reason}",
                corr, clientOrderId, ackStatus, ack.Reason);
            await TransitionToTerminalAsync(orders, loaded.OrderId, OrderStatuses.Error,
                $"bad-ack:{ackStatus}", ct).ConfigureAwait(false);
            return;
        }

        await orders.SetExchangeOrderIdAsync(loaded.OrderId, result.ExchangeOrderId, ackStatus, ct).ConfigureAwait(false);
        _metrics.IncOrder(ackStatus, intent.Signal.Side, intent.SymbolCode);

        if (string.Equals(ackStatus, OrderStatuses.Rejected, StringComparison.Ordinal))
        {
            await riskEvents.InsertAsync(new RiskEvent
            {
                EventTime = _clock.UtcNow,
                EventType = "ORDER_REJECTED",
                Severity  = "WARN",
                SymbolId  = intent.Signal.SymbolId,
                SignalId  = intent.Signal.SignalId,
                OrderId   = loaded.OrderId,
                Payload   = $"cid={clientOrderId} status={result.Status}",
                Acted     = true,
            }, ct).ConfigureAwait(false);
            _log.LogWarning("Order REJECTED corr={Corr} cid={Cid}", corr, clientOrderId);
            return;
        }

        // 6) Diagnostics — log expected vs (so-far) actual slippage. Final
        //    diag rows for partial → full fills are written by the userData
        //    reactor; this initial row covers immediate market fills.
        if (result.AvgFillPrice is { } avgFill && result.ExecutedQty > 0m)
        {
            var refMid = intent.Signal.EntryPrice;
            var estimate = slippage.Estimate(new SlippageInputs(
                MidPrice:                   refMid,
                SpreadBps:                  EstimateSpreadBps(intent),
                OrderQuantity:              result.ExecutedQty,
                AvailableTopOfBookQuantity: null,
                Side:                       intent.Signal.Side));

            await diagnostics.InsertAsync(new ExecutionDiagnostic
            {
                OrderId             = loaded.OrderId,
                FillId              = null,                   // attributed to first WS fill row when it arrives
                SignalId            = intent.Signal.SignalId,
                SymbolId            = intent.Signal.SymbolId,
                Side                = intent.Signal.Side,
                OrderType           = OrderTypes.Market,
                ExpectedPrice       = estimate.ExpectedPrice,
                ActualPrice         = avgFill,
                Quantity            = result.ExecutedQty,
                ExpectedSlippageBps = estimate.SlippageBps,
                ObservedSlippageBps = DefaultSlippageModel.ObservedSlippageBps(refMid, avgFill, intent.Signal.Side),
                SpreadBps           = estimate.HalfSpreadBps * 2m,
                ParticipationPct    = estimate.ParticipationPct,
                ModelVersion        = slippage.Version,
                RecordedAt          = _clock.UtcNow,
            }, ct).ConfigureAwait(false);
        }

        _log.LogInformation(
            "Order ACK corr={Corr} cid={Cid} exId={ExId} status={Status} qty={Qty}/{Filled}",
            corr, clientOrderId, result.ExchangeOrderId, ackStatus, clampedQty, result.ExecutedQty);
    }

    private async Task TransitionToTerminalAsync(IOrderRepository orders, long orderId, string state, string note, CancellationToken ct)
    {
        var transition = _stateMachine.TryTransition(OrderStatuses.Submitting, state);
        if (!transition.IsAccepted)
        {
            _log.LogWarning("State machine rejected → {State} for {OrderId}: {Reason}", state, orderId, transition.Reason);
        }
        await orders.UpdateStatusOnlyAsync(orderId, state, note, ct).ConfigureAwait(false);
    }

    private static decimal ClampQty(ISymbolFilters filters, AccountType account, string symbolCode, decimal qty)
    {
        var f = filters.TryGet(account, symbolCode);
        if (f is null) return qty;
        return BinanceFilterClamp.ClampQuantityToStep(qty, f.StepSize);
    }

    private static AccountType ParseAccount(string s) =>
        string.Equals(s, AccountTypes.UmFut, StringComparison.OrdinalIgnoreCase)
            ? AccountType.UmFutures
            : AccountType.Spot;

    /// Map exchange-supplied status text to canonical OrderStatuses constants.
    /// Binance returns codes like "FILLED", "PARTIALLY_FILLED", "NEW",
    /// "REJECTED", "EXPIRED", "CANCELED" already in upper case.
    internal static string NormalizeStatus(string status) => status.ToUpperInvariant() switch
    {
        "NEW"              => OrderStatuses.New,
        "PARTIALLY_FILLED" => OrderStatuses.PartiallyFilled,
        "FILLED"           => OrderStatuses.Filled,
        "CANCELED"         or "CANCELLED" => OrderStatuses.Cancelled,
        "REJECTED"         => OrderStatuses.Rejected,
        "EXPIRED"          => OrderStatuses.Expired,
        "PENDING_CANCEL"   => OrderStatuses.Canceling,
        _                  => OrderStatuses.Error,
    };

    private static decimal EstimateSpreadBps(ApprovedIntent intent)
    {
        // Without an order-book snapshot we approximate spread from the SL/TP
        // distance ratio — a coarse but stable proxy. Future work: pipe the
        // ILiveCandleCache best-bid/ask in here.
        var entry = intent.Signal.EntryPrice;
        if (entry <= 0m) return 0m;
        var stopDistance = Math.Abs(entry - intent.Signal.StopLoss);
        return stopDistance == 0m ? 0m : Math.Min(50m, stopDistance / entry * 10_000m * 0.05m);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
