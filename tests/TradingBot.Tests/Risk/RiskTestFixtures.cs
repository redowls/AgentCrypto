using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Risk.Abstractions;
using TradingBot.Risk.Configuration;
using TradingBot.Strategies.Abstractions;

namespace TradingBot.Tests.Risk;

/// Shared helpers + in-memory fakes for the §7 unit tests. Keeping them in
/// one file makes the per-gate tests dense and stop-the-presses readable.
internal static class RiskTestFixtures
{
    public const int    BtcId           = 101;
    public const int    EthId           = 102;
    public const int    SolId           = 103;
    public const string Btc             = "BTCUSDT";
    public const string Eth             = "ETHUSDT";
    public const string Sol             = "SOLUSDT";

    public static RiskOptions DefaultOptions() => new();

    public static IOptionsMonitor<RiskOptions> Monitor(RiskOptions opts) =>
        new StaticOptionsMonitor<RiskOptions>(opts);

    public static Signal Signal(
        int symbolId   = BtcId,
        string side    = Sides.Buy,
        decimal entry  = 30_000m,
        decimal sl     = 29_400m,
        decimal tp     = 31_200m,
        decimal? atr   = 250m,
        long signalId  = 1) =>
        new()
        {
            SignalId   = signalId,
            SymbolId   = symbolId,
            Strategy   = StrategyCodes.BreakoutDonchian,
            Interval   = CandleIntervals.OneHour,
            Side       = side,
            EntryPrice = entry,
            StopLoss   = sl,
            TakeProfit = tp,
            AtrValue   = atr,
            Confidence = 0.7m,
            Status     = SignalStatuses.Generated,
            CreatedAt  = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    public static AccountRiskState State(
        decimal equity     = 20_000m,
        decimal cash       = 20_000m,
        decimal unrealized = 0m,
        int openPositions  = 0,
        decimal gross      = 0m,
        decimal net        = 0m,
        decimal hwm        = 20_000m,
        decimal drawdown   = 0m,
        decimal dailyPnl   = 0m,
        decimal? equityAt00 = null,
        DateTime? at        = null,
        string accountType  = AccountTypes.Spot) =>
        new(
            AccountType:      accountType,
            EquityUsd:        equity,
            AvailableUsd:     cash,
            UnrealizedPnlUsd: unrealized,
            OpenPositions:    openPositions,
            GrossExposureUsd: gross,
            NetExposureUsd:   net,
            HighWaterMarkUsd: hwm,
            DrawdownPct:      drawdown,
            DailyPnlPct:      dailyPnl,
            EquityAt00UtcUsd: equityAt00 ?? equity,
            SnapshotTimeUtc:  at ?? new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

    public static Symbol BuildSymbol(int id, string code, string exchange = Exchanges.BinanceSpot) => new()
    {
        SymbolId    = id,
        Exchange    = exchange,
        SymbolCode  = code,
        BaseAsset   = code.Replace("USDT", string.Empty, StringComparison.Ordinal),
        QuoteAsset  = "USDT",
        TickSize    = 0.01m,
        StepSize    = 0.0001m,
        MinNotional = 5m,
        IsActive    = true,
        UpdatedAt   = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
    };

    public static FixedClock Clock(DateTime? at = null) =>
        new(at ?? new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
}

internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T> where T : class
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; private set; }
    public T Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;
    public void Set(T value) => CurrentValue = value;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

internal sealed class FixedClock : IClock
{
    public FixedClock(DateTime utc) => UtcNow = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    public DateTime UtcNow { get; set; }
    public DateTimeOffset UtcNowOffset => new(UtcNow, TimeSpan.Zero);
    public void Advance(TimeSpan span) => UtcNow = UtcNow.Add(span);
    public void SetUtc(DateTime utc) => UtcNow = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
}

internal sealed class FakePositionRepository : IPositionRepository
{
    public List<Position> Open { get; } = new();
    public Task<long> InsertAsync(Position position, CancellationToken ct) => Task.FromResult(1L);
    public Task<Position?> GetByIdAsync(long positionId, CancellationToken ct)
        => Task.FromResult(Open.FirstOrDefault(p => p.PositionId == positionId));
    public Task<Position?> GetOpenForSymbolAsync(int symbolId, string accountType, CancellationToken ct)
        => Task.FromResult(Open.FirstOrDefault(p => p.SymbolId == symbolId && p.AccountType == accountType));
    public Task<IReadOnlyList<Position>> GetOpenAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Position>>(Open.ToList());
    public Task<int> UpdateStopsAsync(long positionId, decimal stopLoss, decimal takeProfit, CancellationToken ct)
        => Task.FromResult(1);
    public Task<int> CloseAsync(long positionId, DateTime closedAtUtc, decimal closePrice, decimal realizedPnlUsd, CancellationToken ct)
        => Task.FromResult(1);
    public Task<int> ExtendAsync(long positionId, decimal addedQuantity, decimal addedFillPrice, CancellationToken ct)
        => Task.FromResult(1);
    public Task<int> ReduceQuantityAsync(long positionId, decimal removedQuantity, CancellationToken ct)
        => Task.FromResult(1);
}

internal sealed class FakeRiskEventRepository : IRiskEventRepository
{
    public List<RiskEvent> Events { get; } = new();
    public Task<long> InsertAsync(RiskEvent riskEvent, CancellationToken ct)
    {
        riskEvent.RiskEventId = Events.Count + 1;
        Events.Add(riskEvent);
        return Task.FromResult(riskEvent.RiskEventId);
    }
    public Task<IReadOnlyList<RiskEvent>> GetRecentAsync(string eventType, int top, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RiskEvent>>(
            Events.Where(e => e.EventType == eventType).TakeLast(top).ToList());
}

internal sealed class FakeSymbolRepository : ISymbolRepository
{
    public Dictionary<int, Symbol> ById { get; } = new();

    public Task<Symbol?> GetByIdAsync(int symbolId, CancellationToken ct)
        => Task.FromResult(ById.GetValueOrDefault(symbolId));
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
        TryGet(account, symbolCode) ?? throw new InvalidOperationException($"missing filter for {symbolCode}");
    public IReadOnlyList<Symbol> All(AccountType account) =>
        Map.Where(kv => kv.Key.Item1 == account).Select(kv => kv.Value).ToList();
}

internal sealed class FakeCorrelationService : ICorrelationService
{
    public bool ReturnOccupied { get; set; }
    public int CallCount { get; private set; }
    public Task<bool> IsClusterOccupiedAsync(int symbolId, string side, IReadOnlyCollection<Position> openPositions, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(ReturnOccupied);
    }
}

internal sealed class FakeKillSwitch : IKillSwitch
{
    public bool IsTripped { get; set; }
    public string? Reason { get; set; }
    public DateTime? TrippedAtUtc { get; set; }
    public KillSwitchSource Source { get; set; } = KillSwitchSource.None;
    public int TripCalls { get; private set; }

    public Task TripAsync(KillSwitchSource source, string reason, CancellationToken ct)
    {
        IsTripped = true;
        Source = source;
        Reason = reason;
        TrippedAtUtc ??= DateTime.UtcNow;
        TripCalls++;
        return Task.CompletedTask;
    }
    public Task ResetAsync(string operatorNote, CancellationToken ct)
    {
        IsTripped = false;
        Reason = null;
        Source = KillSwitchSource.None;
        TrippedAtUtc = null;
        return Task.CompletedTask;
    }
    public void RefreshFromCache() { }
}

internal sealed class FakeFundingRateProvider : IFundingRateProvider
{
    public Dictionary<string, FundingRateSnapshot?> Map { get; } = new();
    public Task<FundingRateSnapshot?> TryGetUpcomingAsync(string symbolCode, CancellationToken ct)
        => Task.FromResult(Map.GetValueOrDefault(symbolCode));
}

/// <summary>
/// Helper to build a fully-wired RiskManager with mostly-fake collaborators.
/// Tests override only what they care about — a single failing gate at a time.
/// </summary>
internal sealed class RiskManagerHarness
{
    public FakePositionRepository Positions { get; } = new();
    public FakeRiskEventRepository RiskEvents { get; } = new();
    public FakeSymbolRepository Symbols { get; } = new();
    public FakeSymbolFilters Filters { get; } = new();
    public FakeCorrelationService Correlation { get; } = new();
    public FakeKillSwitch KillSwitch { get; } = new();
    public FakeFundingRateProvider Funding { get; } = new();
    public FixedClock Clock { get; } = RiskTestFixtures.Clock();
    public RiskOptions Options { get; }

    public RiskManagerHarness(RiskOptions? overrides = null)
    {
        Options = overrides ?? RiskTestFixtures.DefaultOptions();
        Symbols.ById[RiskTestFixtures.BtcId] = RiskTestFixtures.BuildSymbol(RiskTestFixtures.BtcId, RiskTestFixtures.Btc);
        Symbols.ById[RiskTestFixtures.EthId] = RiskTestFixtures.BuildSymbol(RiskTestFixtures.EthId, RiskTestFixtures.Eth);
        Symbols.ById[RiskTestFixtures.SolId] = RiskTestFixtures.BuildSymbol(RiskTestFixtures.SolId, RiskTestFixtures.Sol);
    }

    public TradingBot.Risk.Manager.RiskManager Build()
        => new(
            options:    RiskTestFixtures.Monitor(Options),
            positions:  Positions,
            correlation: Correlation,
            killSwitch: KillSwitch,
            symbols:    Symbols,
            filters:    Filters,
            funding:    Funding,
            riskEvents: RiskEvents,
            clock:      Clock,
            log:        NullLogger<TradingBot.Risk.Manager.RiskManager>.Instance);
}
