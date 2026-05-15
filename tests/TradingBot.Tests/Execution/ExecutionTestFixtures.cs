using System.Collections.Concurrent;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Resilience;

namespace TradingBot.Tests.Execution;

/// In-memory fakes for the §6/§8 execution tests. Designed for assertions
/// against captured calls; behaviour is the bare minimum to drive the
/// engine through a happy path or a forced-failure scenario.
internal sealed class FakeOrderRepository : IOrderRepository
{
    private long _nextId;
    public ConcurrentDictionary<long, Order> ById { get; } = new();
    public ConcurrentDictionary<string, long> ByCid { get; } = new();
    public List<(long OrderId, string Status)> StatusUpdates { get; } = new();

    public Task<long> InsertIfNewAsync(Order order, CancellationToken ct)
    {
        if (ByCid.TryGetValue(order.ClientOrderId, out var existing))
        {
            order.OrderId = existing;
            return Task.FromResult(existing);
        }
        var id = Interlocked.Increment(ref _nextId);
        order.OrderId = id;
        order.SubmittedAt   = DateTime.UtcNow;
        order.LastUpdatedAt = DateTime.UtcNow;
        ById[id] = order;
        ByCid[order.ClientOrderId] = id;
        return Task.FromResult(id);
    }

    public Task<Order?> GetByIdAsync(long orderId, CancellationToken ct)
        => Task.FromResult(ById.GetValueOrDefault(orderId));

    public Task<Order?> GetByClientOrderIdAsync(string cid, CancellationToken ct)
        => Task.FromResult(ByCid.TryGetValue(cid, out var id) ? ById[id] : null);

    public Task<int> UpdateStatusAsync(long orderId, string status, decimal filledQty, decimal? avgFillPrice,
        decimal commissionPaid, string? commissionAsset, CancellationToken ct)
    {
        if (!ById.TryGetValue(orderId, out var o)) return Task.FromResult(0);
        o.Status = status;
        o.FilledQty = filledQty;
        o.AvgFillPrice = avgFillPrice;
        o.CommissionPaid = commissionPaid;
        o.CommissionAsset = commissionAsset;
        o.LastUpdatedAt = DateTime.UtcNow;
        StatusUpdates.Add((orderId, status));
        return Task.FromResult(1);
    }

    public Task<IReadOnlyList<Order>> GetOpenAsync(int symbolId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Order>>(
            ById.Values.Where(o => o.SymbolId == symbolId &&
                                   o.Status is OrderStatuses.New or OrderStatuses.PartiallyFilled).ToList());

    public Task<int> SetExchangeOrderIdAsync(long orderId, long exchangeOrderId, string newStatus, CancellationToken ct)
    {
        if (!ById.TryGetValue(orderId, out var o)) return Task.FromResult(0);
        o.ExchangeOrderId = exchangeOrderId;
        o.Status = newStatus;
        o.LastUpdatedAt = DateTime.UtcNow;
        StatusUpdates.Add((orderId, newStatus));
        return Task.FromResult(1);
    }

    public Task<int> UpdateStatusOnlyAsync(long orderId, string newStatus, string? notes, CancellationToken ct)
    {
        if (!ById.TryGetValue(orderId, out var o)) return Task.FromResult(0);
        o.Status = newStatus;
        if (notes is not null) o.Notes = notes;
        o.LastUpdatedAt = DateTime.UtcNow;
        StatusUpdates.Add((orderId, newStatus));
        return Task.FromResult(1);
    }

    public Task<IReadOnlyList<Order>> GetNonTerminalOlderThanAsync(DateTime olderThanUtc, int maxRows, CancellationToken ct)
    {
        var terminals = new HashSet<string>(StringComparer.Ordinal)
        {
            OrderStatuses.Filled, OrderStatuses.Cancelled, OrderStatuses.Rejected,
            OrderStatuses.Expired, OrderStatuses.Error,
        };
        var rows = ById.Values
            .Where(o => !terminals.Contains(o.Status) && o.LastUpdatedAt <= olderThanUtc)
            .OrderBy(o => o.LastUpdatedAt)
            .Take(maxRows)
            .ToList();
        return Task.FromResult<IReadOnlyList<Order>>(rows);
    }
}

internal sealed class FakeFillRepository : IFillRepository
{
    public List<Fill> Fills { get; } = new();
    public Task<bool> InsertIfNewAsync(Fill fill, CancellationToken ct)
    {
        if (Fills.Any(f => f.OrderId == fill.OrderId && f.TradeId == fill.TradeId))
            return Task.FromResult(false);
        fill.FillId = Fills.Count + 1;
        Fills.Add(fill);
        return Task.FromResult(true);
    }
    public Task<IReadOnlyList<Fill>> GetByOrderAsync(long orderId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Fill>>(Fills.Where(f => f.OrderId == orderId).ToList());
}

internal sealed class FakeExecutionDiagnosticsRepository : IExecutionDiagnosticsRepository
{
    public List<ExecutionDiagnostic> Rows { get; } = new();
    public Task<long> InsertAsync(ExecutionDiagnostic row, CancellationToken ct)
    {
        row.DiagnosticId = Rows.Count + 1;
        Rows.Add(row);
        return Task.FromResult(row.DiagnosticId);
    }
}

internal sealed class FakeBracketLinkRepository : IBracketLinkRepository
{
    public List<BracketLink> Links { get; } = new();
    public Task<long> InsertAsync(BracketLink link, CancellationToken ct)
    {
        link.BracketLinkId = Links.Count + 1;
        Links.Add(link);
        return Task.FromResult(link.BracketLinkId);
    }
    public Task<BracketLink?> GetActiveByPositionAsync(long positionId, CancellationToken ct)
        => Task.FromResult(Links
            .Where(l => l.PositionId == positionId && l.Status == "ACTIVE")
            .OrderByDescending(l => l.CreatedAt).FirstOrDefault());
    public Task<BracketLink?> GetByLegClientOrderIdAsync(string cid, CancellationToken ct)
        => Task.FromResult(Links
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefault(l => l.StopClientOrderId == cid || l.TpClientOrderId == cid));
    public Task<bool> TryReserveSiblingCancelAsync(long bracketLinkId, string leg, CancellationToken ct)
    {
        var l = Links.FirstOrDefault(x => x.BracketLinkId == bracketLinkId);
        if (l is null || l.Status != "ACTIVE" || l.ReservedSibling is not null) return Task.FromResult(false);
        l.ReservedSibling = leg;
        return Task.FromResult(true);
    }
    public Task<int> MarkResolvedAsync(long bracketLinkId, DateTime resolvedAtUtc, CancellationToken ct)
    {
        var l = Links.FirstOrDefault(x => x.BracketLinkId == bracketLinkId);
        if (l is null) return Task.FromResult(0);
        l.Status = "RESOLVED";
        l.ResolvedAt = resolvedAtUtc;
        return Task.FromResult(1);
    }
}

internal sealed class FakeRiskEventRepository2 : IRiskEventRepository
{
    public List<RiskEvent> Events { get; } = new();
    public Task<long> InsertAsync(RiskEvent e, CancellationToken ct)
    {
        e.RiskEventId = Events.Count + 1;
        Events.Add(e);
        return Task.FromResult(e.RiskEventId);
    }
    public Task<IReadOnlyList<RiskEvent>> GetRecentAsync(string eventType, int top, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RiskEvent>>(Events.Where(e => e.EventType == eventType).TakeLast(top).ToList());
}

internal sealed class FakeSymbolRepository2 : ISymbolRepository
{
    public Dictionary<int, Symbol> ById { get; } = new();
    public Task<Symbol?> GetByIdAsync(int symbolId, CancellationToken ct) => Task.FromResult(ById.GetValueOrDefault(symbolId));
    public Task<Symbol?> GetByExchangeAndCodeAsync(string exchange, string symbol, CancellationToken ct)
        => Task.FromResult(ById.Values.FirstOrDefault(s => s.Exchange == exchange && s.SymbolCode == symbol));
    public Task<IReadOnlyList<Symbol>> GetActiveAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Symbol>>(ById.Values.Where(s => s.IsActive).ToList());
    public Task<SymbolUpsertCounts> UpsertExchangeCatalogAsync(string exchange, IReadOnlyList<Symbol> rows, CancellationToken ct)
        => Task.FromResult(new SymbolUpsertCounts(0, 0, 0));
    public Task<IReadOnlyList<Symbol>> GetByExchangeAsync(string exchange, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Symbol>>(ById.Values.Where(s => s.Exchange == exchange).ToList());
}

internal sealed class FakeSymbolFilters : ISymbolFilters
{
    public Dictionary<(AccountType, string), Symbol> Map { get; } = new();
    public Symbol? TryGet(AccountType account, string symbolCode) => Map.GetValueOrDefault((account, symbolCode));
    public Symbol Get(AccountType account, string symbolCode) =>
        TryGet(account, symbolCode) ?? throw new InvalidOperationException($"missing {symbolCode}");
    public IReadOnlyList<Symbol> All(AccountType account) =>
        Map.Where(kv => kv.Key.Item1 == account).Select(kv => kv.Value).ToList();
}

/// Mock gateway. Configurable success/failure surface. Tracks all calls for
/// later assertion (idempotency, double-submit, etc).
internal sealed class MockGateway : IBinanceGateway
{
    public AccountType Account { get; init; } = AccountType.Spot;
    public List<OrderRequest> Placed { get; } = new();
    public List<(string Symbol, string Cid)> Cancelled { get; } = new();
    public Func<OrderRequest, OrderResult>? OnPlace { get; set; }
    public Func<string, string, OrderResult?>? OnGet { get; set; }
    public Exception? PlaceThrows { get; set; }

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct)
    {
        Placed.Add(request);
        if (PlaceThrows is not null) throw PlaceThrows;
        var result = OnPlace?.Invoke(request) ?? new OrderResult
        {
            Symbol           = request.Symbol,
            ClientOrderId    = request.ClientOrderId,
            ExchangeOrderId  = 1_000L + Placed.Count,
            Status           = "NEW",
            ExecutedQty      = 0m,
            AvgFillPrice     = null,
            TransactTimeUtc  = DateTime.UtcNow,
        };
        return Task.FromResult(result);
    }

    public Task<OrderResult> CancelOrderAsync(string symbol, string cid, CancellationToken ct)
    {
        Cancelled.Add((symbol, cid));
        return Task.FromResult(new OrderResult
        {
            Symbol = symbol, ClientOrderId = cid, ExchangeOrderId = 0,
            Status = "CANCELED", ExecutedQty = 0m, AvgFillPrice = null, TransactTimeUtc = DateTime.UtcNow,
        });
    }

    public Task<OrderResult?> GetOrderAsync(string symbol, string cid, CancellationToken ct)
        => Task.FromResult(OnGet?.Invoke(symbol, cid));

    // Unused stubs.
    public Task<ExchangeInfoSnapshot> GetExchangeInfoAsync(CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<KlineData>> GetKlinesAsync(string s, string i, DateTime? a, DateTime? b, int l, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<OrderResult>> GetOpenOrdersAsync(string? s, CancellationToken ct) => Task.FromResult<IReadOnlyList<OrderResult>>(new List<OrderResult>());
    public Task<AccountInfoSnapshot> GetAccountAsync(CancellationToken ct) =>
        Task.FromResult(new AccountInfoSnapshot(Account, true, false, false, new[] { "SPOT" }, new List<AccountBalance>()));
    public Task<IReadOnlyList<UserTrade>> GetUserTradesAsync(string s, long? f, CancellationToken ct) => Task.FromResult<IReadOnlyList<UserTrade>>(new List<UserTrade>());
    public Task<string> StartUserDataStreamAsync(CancellationToken ct) => Task.FromResult("dummy-listen-key");
    public Task KeepAliveUserDataStreamAsync(string l, CancellationToken ct) => Task.CompletedTask;
    public Task CloseUserDataStreamAsync(string l, CancellationToken ct) => Task.CompletedTask;
    public Task<IStreamSubscription> SubscribeKlineAsync(string s, string i, Func<KlineData, ValueTask> h, CancellationToken ct) => throw new NotImplementedException();
    public Task<IStreamSubscription> SubscribeUserDataAsync(string l, Func<UserDataEvent, ValueTask> h, CancellationToken ct) => throw new NotImplementedException();
}

internal sealed class FixedGatewayResolver : IBinanceGatewayResolver
{
    public MockGateway Spot { get; } = new() { Account = AccountType.Spot };
    public MockGateway Futures { get; } = new() { Account = AccountType.UmFutures };
    public IBinanceGateway Get(AccountType account)
        => account == AccountType.Spot ? Spot : Futures;
}

/// Helper for tests that need to drive an exception simulating a 429/418 surface.
internal static class FakeBinanceErrors
{
    public static BinanceApiException Http429() =>
        new("place", code: -1003, httpStatus: 429, message: "TOO_MANY_REQUESTS", raw: "Retry-After: 1");

    public static BinanceApiException Http418() =>
        new("place", code: 0, httpStatus: 418, message: "BANNED", raw: null);

    public static BinanceApiException NetworkDrop() =>
        new("place", code: -1001, httpStatus: 0, message: "DISCONNECTED", raw: null);
}
