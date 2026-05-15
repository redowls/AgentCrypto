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
/// Spot bracket using Binance's atomic OCO. The exchange itself enforces the
/// "one cancels the other" semantics: when either leg fills, the sibling is
/// auto-cancelled, so we DON'T need the futures-style reservation flag for
/// the sibling-cancel race.
///
/// We still write a <c>dbo.BracketLinks</c> row so reconciliation can locate
/// the active OCO list by clientOrderId after a process restart, and so the
/// trailing-stop manager has a single point of truth for "is there an active
/// bracket on this position".
/// </summary>
public sealed class SpotOcoBracketPlacer : IBracketPlacer
{
    private readonly IBinanceGatewayResolver _gateways;
    private readonly ISymbolFilters _filters;
    private readonly IOrderRepository _orders;
    private readonly IBracketLinkRepository _links;
    private readonly IClock _clock;
    private readonly ILogger<SpotOcoBracketPlacer> _log;

    public SpotOcoBracketPlacer(
        IBinanceGatewayResolver gateways,
        ISymbolFilters filters,
        IOrderRepository orders,
        IBracketLinkRepository links,
        IClock clock,
        ILogger<SpotOcoBracketPlacer> log)
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
        var account = AccountType.Spot;
        var gateway = _gateways.Get(account);
        if (gateway is not ISpotOcoCapability oco)
            throw new InvalidOperationException(
                $"resolved spot gateway does not implement {nameof(ISpotOcoCapability)}");

        var exitSide = string.Equals(req.EntrySide, Sides.Buy, StringComparison.Ordinal) ? Sides.Sell : Sides.Buy;
        var slCid    = ClientOrderIdGenerator.ForBracket("BR", req.SignalId, ClientOrderIdGenerator.BracketLeg.Stop);
        var tpCid    = ClientOrderIdGenerator.ForBracket("BR", req.SignalId, ClientOrderIdGenerator.BracketLeg.TakeProfit);
        var listCid  = ClientOrderIdGenerator.ForBracket("LST", req.SignalId, ClientOrderIdGenerator.BracketLeg.Stop);

        var (slPrice, tpPrice, qty, slLimit) = ClampOcoPrices(req.SymbolCode,
            req.StopLossPrice, req.TakeProfitPrice, req.Quantity, req.EntryFillPrice);

        // Persist BOTH child rows up front so the WS reactor (which keys on
        // ClientOrderId) can find them when the OCO acknowledges.
        var slRow = await PersistPendingAsync(req, slCid, OrderTypes.StopMarket, exitSide, qty, slPrice, ct).ConfigureAwait(false);
        var tpRow = await PersistPendingAsync(req, tpCid, OrderTypes.LimitMaker, exitSide, qty, tpPrice, ct).ConfigureAwait(false);

        var ocoResult = await oco.PlaceOcoAsync(new SpotOcoRequest
        {
            Symbol             = req.SymbolCode,
            Side               = exitSide,
            Quantity           = qty,
            TakeProfitPrice    = tpPrice,
            StopTriggerPrice   = slPrice,
            StopLimitPrice     = slLimit,
            ListClientOrderId  = listCid,
            TakeProfitClientId = tpCid,
            StopClientId       = slCid,
            CorrelationId      = req.CorrelationId,
        }, ct).ConfigureAwait(false);

        await _orders.SetExchangeOrderIdAsync(slRow.OrderId, ocoResult.StopExchangeOrderId,
            OrderStatuses.New, ct).ConfigureAwait(false);
        await _orders.SetExchangeOrderIdAsync(tpRow.OrderId, ocoResult.TakeProfitExchangeOrderId,
            OrderStatuses.New, ct).ConfigureAwait(false);

        var linkId = await _links.InsertAsync(new BracketLink
        {
            PositionId        = req.PositionId,
            StopOrderId       = slRow.OrderId,
            TakeProfitOrderId = tpRow.OrderId,
            StopClientOrderId = slCid,
            TpClientOrderId   = tpCid,
            AccountType       = req.AccountType,
            SymbolId          = req.SymbolId,
            Status            = "ACTIVE",
            CreatedAt         = _clock.UtcNow,
        }, ct).ConfigureAwait(false);

        _log.LogInformation(
            "Spot OCO placed corr={Corr} pos={Pos} list={ListCid} sl={SlCid} tp={TpCid} link={Link}",
            req.CorrelationId, req.PositionId, listCid, slCid, tpCid, linkId);

        return new BracketPlacement(linkId, slCid, tpCid,
            ocoResult.StopExchangeOrderId, ocoResult.TakeProfitExchangeOrderId, req.AccountType);
    }

    public async Task<BracketPlacement> UpdateStopAsync(BracketUpdateRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        // Spot OCO has no native "modify stop" — cancel the OCO list, then
        // place a fresh one with the new SL.
        var gateway = _gateways.Get(AccountType.Spot);
        if (gateway is not ISpotOcoCapability oco)
            throw new InvalidOperationException("spot gateway lacks OCO capability");

        var existing = await _links.GetActiveByPositionAsync(req.PositionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"no active bracket link for position {req.PositionId}");

        var listCidToCancel = ClientOrderIdGenerator.ForBracket("LST", req.SignalId, ClientOrderIdGenerator.BracketLeg.Stop);
        try
        {
            await oco.CancelOcoAsync(req.SymbolCode, listCidToCancel, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Spot OCO cancel before re-place failed; reconciliation will retry. list={List}", listCidToCancel);
        }
        await _orders.UpdateStatusOnlyAsync(existing.StopOrderId, OrderStatuses.Cancelled, "trailing-replace", ct).ConfigureAwait(false);
        await _orders.UpdateStatusOnlyAsync(existing.TakeProfitOrderId, OrderStatuses.Cancelled, "trailing-replace", ct).ConfigureAwait(false);
        await _links.MarkResolvedAsync(existing.BracketLinkId, _clock.UtcNow, ct).ConfigureAwait(false);

        return await PlaceAsync(new BracketPlacementRequest(
            PositionId:      req.PositionId,
            SignalId:        req.SignalId,
            AccountType:     req.AccountType,
            SymbolCode:      req.SymbolCode,
            SymbolId:        req.SymbolId,
            EntrySide:       string.Equals(req.PositionSide, PositionSides.Long, StringComparison.Ordinal) ? Sides.Buy : Sides.Sell,
            EntryFillPrice:  req.NewStopLossPrice,    // best-available reference for stop-limit padding
            Quantity:        req.Quantity,
            StopLossPrice:   req.NewStopLossPrice,
            TakeProfitPrice: req.ExistingTakeProfitPrice,
            CorrelationId:   req.CorrelationId), ct).ConfigureAwait(false);
    }

    public Task HandleLegFilledAsync(BracketLegFilled fill, CancellationToken ct)
    {
        // Native OCO auto-cancels the sibling at the exchange — we just
        // record the link as resolved.
        return ResolveAsync(fill, ct);
    }

    private async Task ResolveAsync(BracketLegFilled fill, CancellationToken ct)
    {
        var link = await _links.GetByLegClientOrderIdAsync(fill.ClientOrderId, ct).ConfigureAwait(false);
        if (link is null) return;
        await _links.MarkResolvedAsync(link.BracketLinkId, _clock.UtcNow, ct).ConfigureAwait(false);
        _log.LogInformation("Spot OCO leg filled cid={Cid} leg={Leg} link={Link}",
            fill.ClientOrderId, fill.LegFilled, link.BracketLinkId);
    }

    private async Task<Order> PersistPendingAsync(
        BracketPlacementRequest req,
        string clientOrderId,
        string orderType,
        string side,
        decimal qty,
        decimal price,
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
            Quantity        = qty,
            Price           = orderType == OrderTypes.LimitMaker ? price : null,
            StopPrice       = orderType == OrderTypes.StopMarket ? price : null,
            Status          = OrderStatuses.Submitting,
            FilledQty       = 0,
            CommissionPaid  = 0,
        };
        await _orders.InsertIfNewAsync(row, ct).ConfigureAwait(false);
        return row;
    }

    /// Snap to tick / step. The OCO stop leg is a STOP_LOSS_LIMIT — we need
    /// both the trigger price and a limit price slightly worse than the
    /// trigger so the fill goes through after the trigger.
    private (decimal Sl, decimal Tp, decimal Qty, decimal SlLimit) ClampOcoPrices(
        string symbolCode, decimal sl, decimal tp, decimal qty, decimal entryFill)
    {
        var filter = _filters.TryGet(AccountType.Spot, symbolCode);
        if (filter is null)
            return (sl, tp, qty, sl);

        var slClamped  = BinanceFilterClamp.ClampPriceToTick(sl, filter.TickSize);
        var tpClamped  = BinanceFilterClamp.ClampPriceToTick(tp, filter.TickSize);
        var qtyClamped = BinanceFilterClamp.ClampQuantityToStep(qty, filter.StepSize);

        // For stop-limit, place the limit one tick beyond the trigger in the
        // direction of execution. Choosing direction by entry-fill vs SL.
        var direction = entryFill >= sl ? -1m : 1m;       // long position: limit BELOW trigger
        var slLimit = slClamped + direction * filter.TickSize;
        if (slLimit <= 0m) slLimit = slClamped;
        return (slClamped, tpClamped, qtyClamped, slLimit);
    }
}
