using Microsoft.Extensions.Logging;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Filters;
using TradingBot.Execution.Identity;

namespace TradingBot.Execution.Brackets;

/// <summary>
/// Futures bracket: STOP_MARKET (reduceOnly) + TAKE_PROFIT_MARKET (reduceOnly),
/// paired in <c>dbo.BracketLinks</c>. When one leg fills the userData reactor
/// invokes <see cref="HandleLegFilledAsync"/>, which CASes
/// <see cref="IBracketLinkRepository.TryReserveSiblingCancelAsync"/> before
/// issuing the cancel — preventing two reactors from racing on the same row.
/// </summary>
public sealed class FuturesEmulatedBracketPlacer : IBracketPlacer
{
    private readonly IBinanceGatewayResolver _gateways;
    private readonly ISymbolFilters _filters;
    private readonly IOrderRepository _orders;
    private readonly IBracketLinkRepository _links;
    private readonly IClock _clock;
    private readonly ILogger<FuturesEmulatedBracketPlacer> _log;

    public FuturesEmulatedBracketPlacer(
        IBinanceGatewayResolver gateways,
        ISymbolFilters filters,
        IOrderRepository orders,
        IBracketLinkRepository links,
        IClock clock,
        ILogger<FuturesEmulatedBracketPlacer> log)
    {
        _gateways = gateways;
        _filters  = filters;
        _orders   = orders;
        _links    = links;
        _clock    = clock;
        _log      = log;
    }

    public async Task<BracketPlacement> PlaceAsync(BracketPlacementRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var account  = ParseAccount(req.AccountType);
        var gateway  = _gateways.Get(account);

        // Exit side is the opposite of the entry side.
        var exitSide = string.Equals(req.EntrySide, Sides.Buy, StringComparison.Ordinal) ? Sides.Sell : Sides.Buy;
        var positionSide = string.Equals(req.EntrySide, Sides.Buy, StringComparison.Ordinal)
            ? PositionSides.Long
            : PositionSides.Short;

        var slCid = ClientOrderIdGenerator.ForBracket("BR", req.SignalId, ClientOrderIdGenerator.BracketLeg.Stop);
        var tpCid = ClientOrderIdGenerator.ForBracket("BR", req.SignalId, ClientOrderIdGenerator.BracketLeg.TakeProfit);

        var (slPrice, tpPrice, qty) = ClampBracketPrices(account, req.SymbolCode,
            req.StopLossPrice, req.TakeProfitPrice, req.Quantity);

        var slOrderRow = await PersistPendingAsync(req, slCid, OrderTypes.StopMarket,
            exitSide, positionSide, qty, stopPrice: slPrice, ct).ConfigureAwait(false);
        var tpOrderRow = await PersistPendingAsync(req, tpCid, OrderTypes.TakeProfitMarket,
            exitSide, positionSide, qty, stopPrice: tpPrice, ct).ConfigureAwait(false);

        // Submit both legs. If the second submit fails after the first
        // succeeds we surface the exception; reconciliation will discover
        // the orphan SL on its next sweep and either complete the pairing
        // or cancel the orphan. Bracket placement is *eventually consistent*
        // by design — the engine's own state machine prevents double-fill.
        var slResult = await gateway.PlaceOrderAsync(new OrderRequest
        {
            Account       = account,
            Symbol        = req.SymbolCode,
            ClientOrderId = slCid,
            Side          = exitSide,
            OrderType     = OrderTypes.StopMarket,
            Quantity      = qty,
            StopPrice     = slPrice,
            ReduceOnly    = true,
            PositionSide  = positionSide,
            CorrelationId = req.CorrelationId,
        }, ct).ConfigureAwait(false);
        await _orders.SetExchangeOrderIdAsync(slOrderRow.OrderId, slResult.ExchangeOrderId,
            slResult.Status, ct).ConfigureAwait(false);

        var tpResult = await gateway.PlaceOrderAsync(new OrderRequest
        {
            Account       = account,
            Symbol        = req.SymbolCode,
            ClientOrderId = tpCid,
            Side          = exitSide,
            OrderType     = OrderTypes.TakeProfitMarket,
            Quantity      = qty,
            StopPrice     = tpPrice,
            ReduceOnly    = true,
            PositionSide  = positionSide,
            CorrelationId = req.CorrelationId,
        }, ct).ConfigureAwait(false);
        await _orders.SetExchangeOrderIdAsync(tpOrderRow.OrderId, tpResult.ExchangeOrderId,
            tpResult.Status, ct).ConfigureAwait(false);

        var linkId = await _links.InsertAsync(new BracketLink
        {
            PositionId        = req.PositionId,
            StopOrderId       = slOrderRow.OrderId,
            TakeProfitOrderId = tpOrderRow.OrderId,
            StopClientOrderId = slCid,
            TpClientOrderId   = tpCid,
            AccountType       = req.AccountType,
            SymbolId          = req.SymbolId,
            Status            = "ACTIVE",
            CreatedAt         = _clock.UtcNow,
        }, ct).ConfigureAwait(false);

        _log.LogInformation(
            "Futures bracket placed corr={Corr} pos={Pos} sl={SlCid} tp={TpCid} link={Link}",
            req.CorrelationId, req.PositionId, slCid, tpCid, linkId);

        return new BracketPlacement(linkId, slCid, tpCid,
            slResult.ExchangeOrderId, tpResult.ExchangeOrderId, req.AccountType);
    }

    public async Task<BracketPlacement> UpdateStopAsync(BracketUpdateRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var account = ParseAccount(req.AccountType);
        var gateway = _gateways.Get(account);

        var existing = await _links.GetActiveByPositionAsync(req.PositionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"no active bracket link for position {req.PositionId}");

        var newSlCid = ClientOrderIdGenerator.ForTrailingReplacement(req.SignalId, req.Sequence);
        var (slPrice, _, qty) = ClampBracketPrices(account, req.SymbolCode,
            req.NewStopLossPrice, req.ExistingTakeProfitPrice, req.Quantity);

        var exitSide = string.Equals(req.PositionSide, PositionSides.Long, StringComparison.Ordinal)
            ? Sides.Sell : Sides.Buy;

        // 1) Place the NEW SL FIRST so we are never unprotected.
        var newSlRow = await PersistPendingAsync(new BracketPlacementRequest(
            PositionId: req.PositionId, SignalId: req.SignalId, AccountType: req.AccountType,
            SymbolCode: req.SymbolCode, SymbolId: req.SymbolId, EntrySide: exitSide,
            EntryFillPrice: 0m, Quantity: req.Quantity, StopLossPrice: req.NewStopLossPrice,
            TakeProfitPrice: req.ExistingTakeProfitPrice, CorrelationId: req.CorrelationId),
            newSlCid, OrderTypes.StopMarket, exitSide, req.PositionSide, qty, slPrice, ct).ConfigureAwait(false);

        var slResult = await gateway.PlaceOrderAsync(new OrderRequest
        {
            Account       = account,
            Symbol        = req.SymbolCode,
            ClientOrderId = newSlCid,
            Side          = exitSide,
            OrderType     = OrderTypes.StopMarket,
            Quantity      = qty,
            StopPrice     = slPrice,
            ReduceOnly    = true,
            PositionSide  = req.PositionSide,
            CorrelationId = req.CorrelationId,
        }, ct).ConfigureAwait(false);
        await _orders.SetExchangeOrderIdAsync(newSlRow.OrderId, slResult.ExchangeOrderId,
            slResult.Status, ct).ConfigureAwait(false);

        // 2) Cancel the OLD SL (now redundant). If cancel fails, the
        //    reconciliation sweep will retry — at worst we sit with two SL
        //    orders briefly; reduceOnly ensures we never over-exit.
        try
        {
            await gateway.CancelOrderAsync(req.SymbolCode, existing.StopClientOrderId, ct).ConfigureAwait(false);
            await _orders.UpdateStatusOnlyAsync(existing.StopOrderId, OrderStatuses.Cancelled,
                $"replaced-by:{newSlCid}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Old SL cancel after replace failed; reconciliation will retry. cid={Cid}",
                existing.StopClientOrderId);
        }

        // 3) Mark the prior link RESOLVED and write a fresh ACTIVE row
        //    pointing at the new SL paired with the unchanged TP.
        await _links.MarkResolvedAsync(existing.BracketLinkId, _clock.UtcNow, ct).ConfigureAwait(false);
        var newLinkId = await _links.InsertAsync(new BracketLink
        {
            PositionId        = req.PositionId,
            StopOrderId       = newSlRow.OrderId,
            TakeProfitOrderId = existing.TakeProfitOrderId,
            StopClientOrderId = newSlCid,
            TpClientOrderId   = existing.TpClientOrderId,
            AccountType       = req.AccountType,
            SymbolId          = req.SymbolId,
            Status            = "ACTIVE",
            CreatedAt         = _clock.UtcNow,
        }, ct).ConfigureAwait(false);

        _log.LogInformation(
            "Futures bracket SL replaced corr={Corr} pos={Pos} oldCid={Old} newCid={New} seq={Seq}",
            req.CorrelationId, req.PositionId, existing.StopClientOrderId, newSlCid, req.Sequence);

        return new BracketPlacement(newLinkId, newSlCid, existing.TpClientOrderId,
            slResult.ExchangeOrderId, null, req.AccountType);
    }

    public async Task HandleLegFilledAsync(BracketLegFilled fill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fill);
        var link = await _links.GetByLegClientOrderIdAsync(fill.ClientOrderId, ct).ConfigureAwait(false);
        if (link is null || !string.Equals(link.Status, "ACTIVE", StringComparison.Ordinal))
        {
            _log.LogDebug("Bracket leg fill ignored — no active link for cid={Cid}", fill.ClientOrderId);
            return;
        }

        // Reserve sibling-cancellation slot atomically. The leg that *won*
        // (filled) is named in <c>fill.LegFilled</c>; we want to cancel the
        // OTHER leg.
        var won = await _links.TryReserveSiblingCancelAsync(link.BracketLinkId, fill.LegFilled, ct).ConfigureAwait(false);
        if (!won)
        {
            _log.LogDebug("Sibling cancellation already reserved for link={Link} (race resolved peacefully)",
                link.BracketLinkId);
            return;
        }

        var siblingCid = string.Equals(fill.LegFilled, "SL", StringComparison.Ordinal)
            ? link.TpClientOrderId
            : link.StopClientOrderId;
        var siblingOrderId = string.Equals(fill.LegFilled, "SL", StringComparison.Ordinal)
            ? link.TakeProfitOrderId
            : link.StopOrderId;

        var account = ParseAccount(link.AccountType);
        try
        {
            await _gateways.Get(account)
                .CancelOrderAsync(fill.SymbolCode, siblingCid, ct).ConfigureAwait(false);
            await _orders.UpdateStatusOnlyAsync(siblingOrderId, OrderStatuses.Cancelled,
                $"sibling-of:{fill.ClientOrderId}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Sibling cancel failed corr={Corr} cid={Cid} — reconciliation will retry",
                fill.CorrelationId, siblingCid);
        }

        await _links.MarkResolvedAsync(link.BracketLinkId, _clock.UtcNow, ct).ConfigureAwait(false);
    }

    private async Task<Order> PersistPendingAsync(
        BracketPlacementRequest req,
        string clientOrderId,
        string orderType,
        string side,
        string positionSide,
        decimal qty,
        decimal stopPrice,
        CancellationToken ct)
    {
        var row = new Order
        {
            SignalId        = req.SignalId,
            SymbolId        = req.SymbolId,
            AccountType     = req.AccountType,
            ClientOrderId   = clientOrderId,
            OrderType       = orderType,
            Side            = side,
            PositionSide    = positionSide,
            Quantity        = qty,
            StopPrice       = stopPrice,
            ReduceOnly      = true,
            Status          = OrderStatuses.Submitting,
            FilledQty       = 0,
            CommissionPaid  = 0,
        };
        await _orders.InsertIfNewAsync(row, ct).ConfigureAwait(false);
        return row;
    }

    /// Snap to tick / step. We don't enforce minNotional on stops because the
    /// exchange clamps it; the order will still execute via reduceOnly.
    private (decimal Sl, decimal Tp, decimal Qty) ClampBracketPrices(
        AccountType account, string symbolCode, decimal sl, decimal tp, decimal qty)
    {
        var filter = _filters.TryGet(account, symbolCode);
        if (filter is null) return (sl, tp, qty);
        var clampedSl = BinanceFilterClamp.ClampPriceToTick(sl, filter.TickSize);
        var clampedTp = BinanceFilterClamp.ClampPriceToTick(tp, filter.TickSize);
        var clampedQty = BinanceFilterClamp.ClampQuantityToStep(qty, filter.StepSize);
        return (clampedSl, clampedTp, clampedQty);
    }

    private static AccountType ParseAccount(string s) =>
        string.Equals(s, AccountTypes.UmFut, StringComparison.OrdinalIgnoreCase)
            ? AccountType.UmFutures
            : AccountType.Spot;
}
