using TradingBot.Core.Domain;

namespace TradingBot.Execution.Brackets;

/// <summary>
/// Abstraction over the two §6 bracket placement strategies:
///   • Spot — atomic OCO (TP-limit + SL-stop-limit) via Binance.Net spot OCO.
///   • Futures — emulated bracket: STOP_MARKET (reduceOnly) +
///     TAKE_PROFIT_MARKET (reduceOnly), paired in <c>dbo.BracketLinks</c>.
/// The execution engine calls <see cref="PlaceAsync"/> immediately after a
/// FILLED entry transition. Trailing-stop updates and one-leg-fills go through
/// <see cref="UpdateStopAsync"/> / <see cref="HandleLegFilledAsync"/>.
/// </summary>
public interface IBracketPlacer
{
    Task<BracketPlacement> PlaceAsync(BracketPlacementRequest request, CancellationToken cancellationToken);

    /// Cancel-and-replace the stop leg with a new trigger price. Returns the
    /// new clientOrderIds in <see cref="BracketPlacement"/>; the link row is
    /// updated to point at the new pair (TP leg unchanged for spot OCO; for
    /// futures only the SL leg's order/cid changes).
    Task<BracketPlacement> UpdateStopAsync(BracketUpdateRequest request, CancellationToken cancellationToken);

    /// Reactor hook: one of the bracket legs fired (FILLED). Cancels the
    /// sibling leg using the link's reservation flag to avoid double-cancel
    /// races. No-op when the bracket is already RESOLVED.
    Task HandleLegFilledAsync(BracketLegFilled fill, CancellationToken cancellationToken);
}

/// Per-strategy bracket placer chooser. Resolves at request time based on
/// <see cref="BracketPlacementRequest.AccountType"/>.
public interface IBracketPlacerResolver
{
    IBracketPlacer Resolve(string accountType);
}

public sealed record BracketPlacementRequest(
    long      PositionId,
    long      SignalId,
    string    AccountType,
    string    SymbolCode,
    int       SymbolId,
    string    EntrySide,         // "BUY" or "SELL" (the entry order side)
    decimal   EntryFillPrice,
    decimal   Quantity,
    decimal   StopLossPrice,
    decimal   TakeProfitPrice,
    string    CorrelationId);

public sealed record BracketUpdateRequest(
    long      PositionId,
    long      SignalId,
    int       Sequence,           // monotonically increasing per signal
    string    AccountType,
    string    SymbolCode,
    int       SymbolId,
    string    PositionSide,       // LONG or SHORT — drives direction of the cancel-replace
    decimal   Quantity,
    decimal   NewStopLossPrice,
    decimal   ExistingTakeProfitPrice,
    string    CorrelationId);

public sealed record BracketPlacement(
    long      BracketLinkId,
    string    StopClientOrderId,
    string    TakeProfitClientOrderId,
    long?     StopExchangeOrderId,
    long?     TakeProfitExchangeOrderId,
    string    AccountType);

public sealed record BracketLegFilled(
    string    ClientOrderId,
    string    LegFilled,          // "SL" or "TP"
    long      OrderId,
    long      PositionId,
    string    AccountType,
    string    SymbolCode,
    string    CorrelationId);
