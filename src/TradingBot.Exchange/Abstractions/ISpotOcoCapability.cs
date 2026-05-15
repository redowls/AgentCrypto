namespace TradingBot.Exchange.Abstractions;

/// Spot-only addition to the gateway surface. Implemented by
/// <see cref="TradingBot.Exchange.Binance.BinanceSpotGateway"/>; the futures
/// gateway does NOT implement this — the bracket placer tests <c>is</c>
/// before downcasting and falls back to the emulated futures bracket.
///
/// Binance Spot's OCO submits two child orders atomically (TAKE_PROFIT_LIMIT
/// + STOP_LOSS_LIMIT) keyed by a parent listClientOrderId. Cancelling the
/// list cancels both legs; either leg filling cancels the sibling.
public interface ISpotOcoCapability
{
    Task<SpotOcoResult> PlaceOcoAsync(SpotOcoRequest request, CancellationToken cancellationToken);

    /// Cancel by the parent list client order id. Returns true when the list
    /// was found and cancelled, false when it was already gone.
    Task<bool> CancelOcoAsync(string symbol, string listClientOrderId, CancellationToken cancellationToken);
}

/// Spot OCO is a paired (TP-limit, SL-stop-limit). All prices/qty are
/// expected to be pre-clamped to tick/step by the caller — this struct is the
/// raw wire payload.
public sealed record SpotOcoRequest
{
    public required string  Symbol               { get; init; }
    public required string  Side                 { get; init; }    // SELL for long-exit bracket, BUY for short-exit
    public required decimal Quantity             { get; init; }
    public required decimal TakeProfitPrice      { get; init; }    // limit price for the TP leg
    public required decimal StopTriggerPrice     { get; init; }    // stopPrice for the SL leg
    public required decimal StopLimitPrice       { get; init; }    // limit price once stop triggers
    public required string  ListClientOrderId    { get; init; }    // ≤36 chars
    public required string  TakeProfitClientId   { get; init; }    // ≤36 chars
    public required string  StopClientId         { get; init; }    // ≤36 chars
    public string?          TimeInForce          { get; init; }    // GTC by default
    public string           CorrelationId        { get; init; } = string.Empty;
}

public sealed record SpotOcoResult(
    string  Symbol,
    string  ListClientOrderId,
    long    OrderListId,
    long    TakeProfitExchangeOrderId,
    long    StopExchangeOrderId,
    string  ListStatus);
