using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Domain.Enums;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Resilience;
using AccountType = TradingBot.Exchange.Abstractions.AccountType;

namespace TradingBot.Exchange.Binance;

public sealed class BinanceSpotGateway : IBinanceGateway, ISpotOcoCapability
{
    private readonly IBinanceRestClient _rest;
    private readonly IBinanceSocketClient _socket;
    private readonly BinanceResiliencePipeline _pipeline;
    private readonly ILogger<BinanceSpotGateway> _log;

    public BinanceSpotGateway(
        IBinanceRestClient rest,
        IBinanceSocketClient socket,
        BinanceResiliencePipeline pipeline,
        ILogger<BinanceSpotGateway> log)
    {
        _rest = rest;
        _socket = socket;
        _pipeline = pipeline;
        _log = log;
    }

    public AccountType Account => AccountType.Spot;

    public Task<ExchangeInfoSnapshot> GetExchangeInfoAsync(CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            var result = await _rest.SpotApi.ExchangeData.GetExchangeInfoAsync(ct).ConfigureAwait(false);
            var data = BinanceCallOutcome.Unwrap(result, "spot.exchangeInfo");
            var rows = new List<ExchangeSymbolFilter>(data.Symbols.Count());
            foreach (var s in data.Symbols)
            {
                var tick = s.PriceFilter?.TickSize ?? 0m;
                var step = s.LotSizeFilter?.StepSize ?? 0m;
                var minNotional = s.MinNotionalFilter?.MinNotional ?? 0m;
                rows.Add(new ExchangeSymbolFilter(
                    s.Name,
                    s.BaseAsset,
                    s.QuoteAsset,
                    tick,
                    step,
                    minNotional,
                    s.Status == SymbolStatus.Trading));
            }
            return new ExchangeInfoSnapshot(AccountType.Spot, DateTime.UtcNow, rows);
        }, "spot.exchangeInfo", cancellationToken);

    public Task<IReadOnlyList<KlineData>> GetKlinesAsync(
        string symbol, string interval, DateTime? startUtc, DateTime? endUtc, int limit,
        CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync<IReadOnlyList<KlineData>>(async ct =>
        {
            var k = BinanceIntervalMap.Parse(interval);
            var result = await _rest.SpotApi.ExchangeData
                .GetKlinesAsync(symbol, k, startUtc, endUtc, limit, ct).ConfigureAwait(false);
            var data = BinanceCallOutcome.Unwrap(result, "spot.klines");
            var list = new List<KlineData>(limit);
            foreach (var bar in data)
            {
                list.Add(new KlineData(
                    bar.OpenTime, bar.CloseTime,
                    bar.OpenPrice, bar.HighPrice, bar.LowPrice, bar.ClosePrice,
                    bar.Volume, bar.QuoteVolume, bar.TradeCount,
                    bar.TakerBuyBaseVolume, IsClosed: true));
            }
            return list;
        }, "spot.klines", cancellationToken);

    public Task<OrderResult> PlaceOrderAsync(OrderRequest req, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            _log.LogInformation(
                "Spot PLACE op corr={CorrelationId} sym={Symbol} side={Side} type={Type} qty={Qty} px={Px} cid={Cid}",
                req.CorrelationId, req.Symbol, req.Side, req.OrderType, req.Quantity, req.Price, req.ClientOrderId);

            var side = BinanceIntervalMap.ParseSide(req.Side);
            var type = MapSpotOrderType(req.OrderType);
            var tif  = BinanceIntervalMap.ParseTif(req.TimeInForce);

            var result = await _rest.SpotApi.Trading.PlaceOrderAsync(
                req.Symbol,
                side,
                type,
                quantity: req.Quantity,
                price: req.Price,
                timeInForce: tif,
                stopPrice: req.StopPrice,
                newClientOrderId: req.ClientOrderId,
                ct: ct).ConfigureAwait(false);

            var data = BinanceCallOutcome.Unwrap(result, "spot.placeOrder");
            return ToResult(data.Symbol, data.ClientOrderId ?? req.ClientOrderId, data.Id, data.Status.ToString().ToUpperInvariant(),
                data.QuantityFilled, data.AverageFillPrice, data.CreateTime);
        }, "spot.placeOrder", cancellationToken);

    public Task<OrderResult> CancelOrderAsync(string symbol, string clientOrderId, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            _log.LogInformation("Spot CANCEL sym={Symbol} cid={Cid}", symbol, clientOrderId);
            var result = await _rest.SpotApi.Trading
                .CancelOrderAsync(symbol, origClientOrderId: clientOrderId, ct: ct).ConfigureAwait(false);
            var data = BinanceCallOutcome.Unwrap(result, "spot.cancelOrder");
            return ToResult(data.Symbol, data.ClientOrderId ?? clientOrderId, data.Id, data.Status.ToString().ToUpperInvariant(),
                data.QuantityFilled, data.AverageFillPrice, data.CreateTime);
        }, "spot.cancelOrder", cancellationToken);

    public Task<OrderResult?> GetOrderAsync(string symbol, string clientOrderId, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync<OrderResult?>(async ct =>
        {
            var result = await _rest.SpotApi.Trading
                .GetOrderAsync(symbol, origClientOrderId: clientOrderId, ct: ct).ConfigureAwait(false);
            if (!result.Success)
            {
                if (result.Error?.Code == -2013) return null; // NO_SUCH_ORDER
                throw BinanceCallOutcome.ToException(result, "spot.getOrder");
            }
            var d = result.Data!;
            return ToResult(d.Symbol, d.ClientOrderId ?? clientOrderId, d.Id, d.Status.ToString().ToUpperInvariant(),
                d.QuantityFilled, d.AverageFillPrice, d.CreateTime);
        }, "spot.getOrder", cancellationToken);

    public Task<IReadOnlyList<OrderResult>> GetOpenOrdersAsync(string? symbol, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync<IReadOnlyList<OrderResult>>(async ct =>
        {
            var result = await _rest.SpotApi.Trading.GetOpenOrdersAsync(symbol, ct: ct).ConfigureAwait(false);
            var data = BinanceCallOutcome.Unwrap(result, "spot.openOrders");
            return data.Select(d => ToResult(d.Symbol, d.ClientOrderId ?? string.Empty, d.Id,
                d.Status.ToString().ToUpperInvariant(), d.QuantityFilled, d.AverageFillPrice, d.CreateTime))
                .ToList();
        }, "spot.openOrders", cancellationToken);

    public Task<AccountInfoSnapshot> GetAccountAsync(CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            var result = await _rest.SpotApi.Account.GetAccountInfoAsync(ct: ct).ConfigureAwait(false);
            var d = BinanceCallOutcome.Unwrap(result, "spot.account");
            var balances = d.Balances
                .Where(b => b.Total > 0m)
                .Select(b => new AccountBalance(b.Asset, b.Available, b.Locked))
                .ToList();
            var perms = d.Permissions?.Select(p => p.ToString().ToUpperInvariant()).ToList() ?? new List<string>();
            return new AccountInfoSnapshot(AccountType.Spot, d.CanTrade, d.CanWithdraw, d.CanDeposit, perms, balances);
        }, "spot.account", cancellationToken);

    public Task<IReadOnlyList<UserTrade>> GetUserTradesAsync(string symbol, long? fromTradeId, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync<IReadOnlyList<UserTrade>>(async ct =>
        {
            var result = await _rest.SpotApi.Trading
                .GetUserTradesAsync(symbol, fromId: fromTradeId, ct: ct).ConfigureAwait(false);
            var data = BinanceCallOutcome.Unwrap(result, "spot.userTrades");
            return data.Select(t => new UserTrade(
                t.Id, t.OrderId, t.Symbol,
                t.IsBuyer ? Sides.Buy : Sides.Sell,
                t.Price, t.Quantity, t.QuoteQuantity,
                t.Fee, t.FeeAsset ?? string.Empty,
                t.IsMaker, t.Timestamp)).ToList();
        }, "spot.userTrades", cancellationToken);

    public Task<string> StartUserDataStreamAsync(CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            var result = await _rest.SpotApi.Account.StartUserStreamAsync(ct).ConfigureAwait(false);
            return BinanceCallOutcome.Unwrap(result, "spot.startUserStream");
        }, "spot.startUserStream", cancellationToken);

    public Task KeepAliveUserDataStreamAsync(string listenKey, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            var result = await _rest.SpotApi.Account.KeepAliveUserStreamAsync(listenKey, ct).ConfigureAwait(false);
            BinanceCallOutcome.EnsureSuccess(result, "spot.keepAliveUserStream");
        }, "spot.keepAliveUserStream", cancellationToken);

    public Task CloseUserDataStreamAsync(string listenKey, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            var result = await _rest.SpotApi.Account.StopUserStreamAsync(listenKey, ct).ConfigureAwait(false);
            BinanceCallOutcome.EnsureSuccess(result, "spot.stopUserStream");
        }, "spot.stopUserStream", cancellationToken);

    public async Task<IStreamSubscription> SubscribeKlineAsync(
        string symbol, string interval,
        Func<KlineData, ValueTask> onKline,
        CancellationToken cancellationToken)
    {
        var k = BinanceIntervalMap.Parse(interval);
        var result = await _socket.SpotApi.ExchangeData
            .SubscribeToKlineUpdatesAsync(symbol, k, update =>
            {
                var bar = update.Data.Data;
                // Binance.Net's socket callbacks are synchronous Action<DataEvent<>>;
                // our onKline is async so we fire-and-observe (any handler exception
                // is logged by the WS manager via a record.LastError write).
                _ = onKline(new KlineData(
                    bar.OpenTime, bar.CloseTime,
                    bar.OpenPrice, bar.HighPrice, bar.LowPrice, bar.ClosePrice,
                    bar.Volume, bar.QuoteVolume, bar.TradeCount,
                    bar.TakerBuyBaseVolume, bar.Final));
            }, cancellationToken).ConfigureAwait(false);
        var sub = BinanceCallOutcome.Unwrap(result, "spot.subscribeKline");
        return new StreamSubscriptionHandle(sub, _socket, $"spot.kline.{symbol}.{interval}");
    }

    public async Task<IStreamSubscription> SubscribeUserDataAsync(
        string listenKey,
        Func<UserDataEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        // Param shape (10.6): listenKey, onOrderUpdateMessage,
        // onOcoOrderUpdateMessage, onAccountPositionMessage,
        // onAccountBalanceUpdate, onListenKeyExpired, ct.
        var result = await _socket.SpotApi.Account.SubscribeToUserDataUpdatesAsync(
            listenKey,
            onOrderUpdateMessage: upd =>
            {
                var d = upd.Data;
                _ = onEvent(new UserDataEvent(
                    UserDataEventKind.OrderUpdate,
                    d.ClientOrderId ?? string.Empty,
                    d.Symbol,
                    d.Status.ToString().ToUpperInvariant(),
                    d.QuantityFilled,
                    d.LastPriceFilled > 0 ? d.LastPriceFilled : null,
                    d.Id,
                    d.EventTime,
                    d));
            },
            onOcoOrderUpdateMessage: null,
            onAccountPositionMessage: upd =>
            {
                _ = onEvent(new UserDataEvent(
                    UserDataEventKind.AccountUpdate, string.Empty, null, null,
                    null, null, null, upd.Data.EventTime, upd.Data));
            },
            onAccountBalanceUpdate: upd =>
            {
                _ = onEvent(new UserDataEvent(
                    UserDataEventKind.BalanceUpdate, string.Empty, null, null,
                    null, null, null, upd.Data.EventTime, upd.Data));
            },
            onListenKeyExpired: upd =>
            {
                _ = onEvent(new UserDataEvent(
                    UserDataEventKind.ListenKeyExpired, string.Empty, null, null,
                    null, null, null, upd.Data.EventTime, upd.Data));
            },
            ct: cancellationToken).ConfigureAwait(false);

        var sub = BinanceCallOutcome.Unwrap(result, "spot.subscribeUserData");
        return new StreamSubscriptionHandle(sub, _socket, $"spot.userData.{listenKey[..Math.Min(8, listenKey.Length)]}");
    }

    public Task<SpotOcoResult> PlaceOcoAsync(SpotOcoRequest req, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            _log.LogInformation(
                "Spot OCO PLACE corr={CorrelationId} sym={Symbol} side={Side} qty={Qty} tp={Tp} sl={Sl}->{SlLim} listCid={ListCid}",
                req.CorrelationId, req.Symbol, req.Side, req.Quantity,
                req.TakeProfitPrice, req.StopTriggerPrice, req.StopLimitPrice, req.ListClientOrderId);

            var side = BinanceIntervalMap.ParseSide(req.Side);
            var tif  = BinanceIntervalMap.ParseTif(req.TimeInForce ?? "GTC") ?? TimeInForce.GoodTillCanceled;

            // Deprecated-but-functional OCO; arg order:
            //   symbol, side, quantity, price, stopPrice, stopLimitPrice?,
            //   stopClientOrderId, limitClientOrderId, listClientOrderId, ...
            var result = await _rest.SpotApi.Trading.PlaceOcoOrderAsync(
                symbol:             req.Symbol,
                side:               side,
                quantity:           req.Quantity,
                price:              req.TakeProfitPrice,
                stopPrice:          req.StopTriggerPrice,
                stopLimitPrice:     req.StopLimitPrice,
                stopClientOrderId:  req.StopClientId,
                limitClientOrderId: req.TakeProfitClientId,
                listClientOrderId:  req.ListClientOrderId,
                stopLimitTimeInForce: tif,
                ct: ct).ConfigureAwait(false);

            var data = BinanceCallOutcome.Unwrap(result, "spot.placeOco");

            // Two child orders. The TP leg uses limitClientOrderId we supplied;
            // the SL leg uses stopClientOrderId. Match by ClientOrderId rather
            // than positional order — Binance has reversed the array on us before.
            long tpId = 0, slId = 0;
            foreach (var o in data.Orders)
            {
                if (string.Equals(o.ClientOrderId, req.TakeProfitClientId, StringComparison.Ordinal)) tpId = o.OrderId;
                else if (string.Equals(o.ClientOrderId, req.StopClientId, StringComparison.Ordinal)) slId = o.OrderId;
            }

            return new SpotOcoResult(
                Symbol:                    data.Symbol,
                ListClientOrderId:         data.ListClientOrderId ?? req.ListClientOrderId,
                OrderListId:               data.Id,
                TakeProfitExchangeOrderId: tpId,
                StopExchangeOrderId:       slId,
                ListStatus:                data.ListOrderStatus.ToString().ToUpperInvariant());
        }, "spot.placeOco", cancellationToken);

    public Task<bool> CancelOcoAsync(string symbol, string listClientOrderId, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            _log.LogInformation("Spot OCO CANCEL sym={Symbol} listCid={ListCid}", symbol, listClientOrderId);
            var result = await _rest.SpotApi.Trading
                .CancelOcoOrderAsync(symbol, orderListId: null, listClientOrderId: listClientOrderId,
                    newClientOrderId: null, ct: ct).ConfigureAwait(false);
            if (result.Success) return true;
            // -2011 UNKNOWN_ORDER_COMPOSITION / -2013 NO_SUCH_ORDER → list already gone, idempotent.
            if (result.Error?.Code is -2011 or -2013) return false;
            throw BinanceCallOutcome.ToException(result, "spot.cancelOco");
        }, "spot.cancelOco", cancellationToken);

    private static SpotOrderType MapSpotOrderType(string type) => type switch
    {
        OrderTypes.Limit            => SpotOrderType.Limit,
        OrderTypes.Market           => SpotOrderType.Market,
        OrderTypes.LimitMaker       => SpotOrderType.LimitMaker,
        OrderTypes.StopMarket       => SpotOrderType.StopLoss,
        OrderTypes.TakeProfitMarket => SpotOrderType.TakeProfit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported spot order type."),
    };

    private static OrderResult ToResult(
        string symbol, string clientOrderId, long exchangeId, string status,
        decimal executedQty, decimal? avgFill, DateTime ts) =>
        new()
        {
            Symbol = symbol,
            ClientOrderId = clientOrderId,
            ExchangeOrderId = exchangeId,
            Status = status,
            ExecutedQty = executedQty,
            AvgFillPrice = avgFill,
            TransactTimeUtc = ts,
        };
}
