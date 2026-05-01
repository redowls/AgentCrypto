using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Domain.Enums;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Resilience;
using AccountType = TradingBot.Exchange.Abstractions.AccountType;

namespace TradingBot.Exchange.Binance;

public sealed class BinanceFuturesGateway : IBinanceGateway
{
    private readonly IBinanceRestClient _rest;
    private readonly IBinanceSocketClient _socket;
    private readonly BinanceResiliencePipeline _pipeline;
    private readonly ILogger<BinanceFuturesGateway> _log;

    public BinanceFuturesGateway(
        IBinanceRestClient rest,
        IBinanceSocketClient socket,
        BinanceResiliencePipeline pipeline,
        ILogger<BinanceFuturesGateway> log)
    {
        _rest = rest;
        _socket = socket;
        _pipeline = pipeline;
        _log = log;
    }

    public AccountType Account => AccountType.UmFutures;

    public Task<ExchangeInfoSnapshot> GetExchangeInfoAsync(CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            var result = await _rest.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct).ConfigureAwait(false);
            var data = BinanceCallOutcome.Unwrap(result, "fut.exchangeInfo");
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
            return new ExchangeInfoSnapshot(AccountType.UmFutures, DateTime.UtcNow, rows);
        }, "fut.exchangeInfo", cancellationToken);

    public Task<IReadOnlyList<KlineData>> GetKlinesAsync(
        string symbol, string interval, DateTime? startUtc, DateTime? endUtc, int limit,
        CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync<IReadOnlyList<KlineData>>(async ct =>
        {
            var k = BinanceIntervalMap.Parse(interval);
            var result = await _rest.UsdFuturesApi.ExchangeData
                .GetKlinesAsync(symbol, k, startUtc, endUtc, limit, ct).ConfigureAwait(false);
            var data = BinanceCallOutcome.Unwrap(result, "fut.klines");
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
        }, "fut.klines", cancellationToken);

    public Task<OrderResult> PlaceOrderAsync(OrderRequest req, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            _log.LogInformation(
                "Fut PLACE op corr={CorrelationId} sym={Symbol} side={Side} type={Type} qty={Qty} px={Px} cid={Cid} reduceOnly={Ro}",
                req.CorrelationId, req.Symbol, req.Side, req.OrderType, req.Quantity, req.Price, req.ClientOrderId, req.ReduceOnly);

            var side = BinanceIntervalMap.ParseSide(req.Side);
            var type = MapFuturesOrderType(req.OrderType);
            var tif  = BinanceIntervalMap.ParseTif(req.TimeInForce);
            var posSide = BinanceIntervalMap.ParsePositionSide(req.PositionSide);

            var result = await _rest.UsdFuturesApi.Trading.PlaceOrderAsync(
                req.Symbol,
                side,
                type,
                quantity: req.Quantity,
                price: req.Price,
                positionSide: posSide,
                timeInForce: tif,
                reduceOnly: req.ReduceOnly ? true : null,
                stopPrice: req.StopPrice,
                newClientOrderId: req.ClientOrderId,
                ct: ct).ConfigureAwait(false);

            var data = BinanceCallOutcome.Unwrap(result, "fut.placeOrder");
            return ToResult(data.Symbol, data.ClientOrderId ?? req.ClientOrderId, data.Id,
                data.Status.ToString().ToUpperInvariant(),
                data.QuantityFilled,
                data.AveragePrice > 0 ? data.AveragePrice : null,
                data.UpdateTime);
        }, "fut.placeOrder", cancellationToken);

    public Task<OrderResult> CancelOrderAsync(string symbol, string clientOrderId, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            _log.LogInformation("Fut CANCEL sym={Symbol} cid={Cid}", symbol, clientOrderId);
            var result = await _rest.UsdFuturesApi.Trading
                .CancelOrderAsync(symbol, origClientOrderId: clientOrderId, ct: ct).ConfigureAwait(false);
            var d = BinanceCallOutcome.Unwrap(result, "fut.cancelOrder");
            return ToResult(d.Symbol, d.ClientOrderId ?? clientOrderId, d.Id,
                d.Status.ToString().ToUpperInvariant(),
                d.QuantityFilled,
                d.AveragePrice > 0 ? d.AveragePrice : null,
                d.UpdateTime);
        }, "fut.cancelOrder", cancellationToken);

    public Task<OrderResult?> GetOrderAsync(string symbol, string clientOrderId, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync<OrderResult?>(async ct =>
        {
            var result = await _rest.UsdFuturesApi.Trading
                .GetOrderAsync(symbol, origClientOrderId: clientOrderId, ct: ct).ConfigureAwait(false);
            if (!result.Success)
            {
                if (result.Error?.Code == -2013) return null;
                throw BinanceCallOutcome.ToException(result, "fut.getOrder");
            }
            var d = result.Data!;
            return ToResult(d.Symbol, d.ClientOrderId ?? clientOrderId, d.Id,
                d.Status.ToString().ToUpperInvariant(),
                d.QuantityFilled,
                d.AveragePrice > 0 ? d.AveragePrice : null,
                d.UpdateTime);
        }, "fut.getOrder", cancellationToken);

    public Task<IReadOnlyList<OrderResult>> GetOpenOrdersAsync(string? symbol, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync<IReadOnlyList<OrderResult>>(async ct =>
        {
            var result = await _rest.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct).ConfigureAwait(false);
            var data = BinanceCallOutcome.Unwrap(result, "fut.openOrders");
            return data.Select(d => ToResult(d.Symbol, d.ClientOrderId ?? string.Empty, d.Id,
                d.Status.ToString().ToUpperInvariant(),
                d.QuantityFilled,
                d.AveragePrice > 0 ? d.AveragePrice : null,
                d.UpdateTime)).ToList();
        }, "fut.openOrders", cancellationToken);

    public Task<AccountInfoSnapshot> GetAccountAsync(CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            // V2 returns BinanceFuturesAccountInfo, which still carries
            // CanTrade / CanDeposit / CanWithdraw. V3 dropped those fields.
            var result = await _rest.UsdFuturesApi.Account.GetAccountInfoV2Async(ct: ct).ConfigureAwait(false);
            var d = BinanceCallOutcome.Unwrap(result, "fut.accountV2");
            var balances = d.Assets
                .Where(a => a.WalletBalance > 0m)
                .Select(a => new AccountBalance(a.Asset, a.AvailableBalance, a.WalletBalance - a.AvailableBalance))
                .ToList();
            return new AccountInfoSnapshot(
                AccountType.UmFutures,
                d.CanTrade, d.CanWithdraw, d.CanDeposit,
                Permissions: new[] { "FUTURES" },
                Balances: balances);
        }, "fut.accountV2", cancellationToken);

    public Task<IReadOnlyList<UserTrade>> GetUserTradesAsync(string symbol, long? fromTradeId, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync<IReadOnlyList<UserTrade>>(async ct =>
        {
            var result = await _rest.UsdFuturesApi.Trading
                .GetUserTradesAsync(symbol, fromId: fromTradeId, ct: ct).ConfigureAwait(false);
            var data = BinanceCallOutcome.Unwrap(result, "fut.userTrades");
            return data.Select(t => new UserTrade(
                t.Id, t.OrderId, t.Symbol,
                t.Side.ToString().ToUpperInvariant(),
                t.Price, t.Quantity, t.QuoteQuantity,
                t.Fee, t.FeeAsset ?? string.Empty,
                t.Maker, t.Timestamp)).ToList();
        }, "fut.userTrades", cancellationToken);

    public Task<string> StartUserDataStreamAsync(CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            var result = await _rest.UsdFuturesApi.Account.StartUserStreamAsync(ct).ConfigureAwait(false);
            return BinanceCallOutcome.Unwrap(result, "fut.startUserStream");
        }, "fut.startUserStream", cancellationToken);

    public Task KeepAliveUserDataStreamAsync(string listenKey, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            var result = await _rest.UsdFuturesApi.Account.KeepAliveUserStreamAsync(listenKey, ct).ConfigureAwait(false);
            BinanceCallOutcome.EnsureSuccess(result, "fut.keepAliveUserStream");
        }, "fut.keepAliveUserStream", cancellationToken);

    public Task CloseUserDataStreamAsync(string listenKey, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(async ct =>
        {
            var result = await _rest.UsdFuturesApi.Account.StopUserStreamAsync(listenKey, ct).ConfigureAwait(false);
            BinanceCallOutcome.EnsureSuccess(result, "fut.stopUserStream");
        }, "fut.stopUserStream", cancellationToken);

    public async Task<IStreamSubscription> SubscribeKlineAsync(
        string symbol, string interval,
        Func<KlineData, ValueTask> onKline,
        CancellationToken cancellationToken)
    {
        var k = BinanceIntervalMap.Parse(interval);
        var result = await _socket.UsdFuturesApi.ExchangeData
            .SubscribeToKlineUpdatesAsync(symbol, k, update =>
            {
                var bar = update.Data.Data;
                _ = onKline(new KlineData(
                    bar.OpenTime, bar.CloseTime,
                    bar.OpenPrice, bar.HighPrice, bar.LowPrice, bar.ClosePrice,
                    bar.Volume, bar.QuoteVolume, bar.TradeCount,
                    bar.TakerBuyBaseVolume, bar.Final));
            }, cancellationToken).ConfigureAwait(false);
        var sub = BinanceCallOutcome.Unwrap(result, "fut.subscribeKline");
        return new StreamSubscriptionHandle(sub, _socket, $"fut.kline.{symbol}.{interval}");
    }

    public async Task<IStreamSubscription> SubscribeUserDataAsync(
        string listenKey,
        Func<UserDataEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        // Param shape (10.6): listenKey, onLeverageUpdate, onMarginUpdate,
        // onAccountUpdate, onOrderUpdate, onTradeUpdate, onListenKeyExpired,
        // onStrategyUpdate, onGridUpdate, onConditionalOrderTriggerRejectUpdate, ct.
        var result = await _socket.UsdFuturesApi.Account.SubscribeToUserDataUpdatesAsync(
            listenKey,
            onLeverageUpdate: null,
            onMarginUpdate: null,
            onAccountUpdate: upd =>
            {
                _ = onEvent(new UserDataEvent(
                    UserDataEventKind.AccountUpdate, string.Empty, null, null,
                    null, null, null, upd.Data.EventTime, upd.Data));
            },
            onOrderUpdate: upd =>
            {
                var d = upd.Data.UpdateData;
                _ = onEvent(new UserDataEvent(
                    UserDataEventKind.OrderUpdate,
                    d.ClientOrderId ?? string.Empty,
                    d.Symbol,
                    d.Status.ToString().ToUpperInvariant(),
                    d.AccumulatedQuantityOfFilledTrades,
                    d.AveragePrice > 0 ? d.AveragePrice : null,
                    d.OrderId,
                    upd.Data.EventTime,
                    upd.Data));
            },
            onTradeUpdate: null,
            onListenKeyExpired: upd =>
            {
                _ = onEvent(new UserDataEvent(
                    UserDataEventKind.ListenKeyExpired, string.Empty, null, null,
                    null, null, null, upd.Data.EventTime, upd.Data));
            },
            onStrategyUpdate: null,
            onGridUpdate: null,
            onConditionalOrderTriggerRejectUpdate: null,
            ct: cancellationToken).ConfigureAwait(false);

        var sub = BinanceCallOutcome.Unwrap(result, "fut.subscribeUserData");
        return new StreamSubscriptionHandle(sub, _socket, $"fut.userData.{listenKey[..Math.Min(8, listenKey.Length)]}");
    }

    private static FuturesOrderType MapFuturesOrderType(string type) => type switch
    {
        OrderTypes.Limit            => FuturesOrderType.Limit,
        OrderTypes.Market           => FuturesOrderType.Market,
        OrderTypes.StopMarket       => FuturesOrderType.StopMarket,
        OrderTypes.TakeProfitMarket => FuturesOrderType.TakeProfitMarket,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported futures order type."),
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
