using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Backtest.Configuration;
using TradingBot.Backtest.Domain;
using TradingBot.Backtest.Exchange;
using TradingBot.Backtest.Metrics;
using TradingBot.Backtest.Repositories;
using TradingBot.Backtest.Risk;
using TradingBot.Backtest.Time;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Indicators;
using TradingBot.Data.Abstractions;
using TradingBot.Execution.Slippage;
using TradingBot.Execution.State;
using TradingBot.Risk.Abstractions;
using TradingBot.Risk.Configuration;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Brackets;
using TradingBot.Strategies.Engine;

namespace TradingBot.Backtest.Engine;

// Synchronous deterministic replay engine. Reuses the live indicator engine,
// regime classifier, strategy modules, bracket calculator, slippage model and
// order state machine; the orchestration plumbing (BackgroundServices + WS
// channels) is replaced by a single in-process loop that processes one
// candle at a time and persists every side-effect to bt.* tables.
internal sealed class BacktestEngine
{
    private readonly BacktestEngineOptions _options;
    private readonly SimulatedClock _clock;
    private readonly ICandleRepository _candles;
    private readonly ISymbolRepository _symbols;
    private readonly IIndicatorEngine _indicators;
    private readonly IRegimeClassifier _regime;
    private readonly MarketContextBuilder _ctxBuilder;
    private readonly IEnumerable<IStrategy> _strategies;
    private readonly OrderStateMachine _stateMachine;
    private readonly ISlippageModel _slippage;
    private readonly IOptionsMonitor<RiskOptions> _riskOpts;
    private readonly BacktestRunRepository _runs;
    private readonly BacktestSignalRepository _btSignals;
    private readonly BacktestOrderRepository _btOrders;
    private readonly BacktestFillRepository _btFills;
    private readonly BacktestPositionRepository _btPositions;
    private readonly BacktestTradeHistoryRepository _btTrades;
    private readonly BacktestAccountSnapshotRepository _btSnapshots;
    private readonly ILogger<BacktestEngine> _log;

    public BacktestEngine(
        IOptions<BacktestEngineOptions> options,
        SimulatedClock clock,
        ICandleRepository candles,
        ISymbolRepository symbols,
        IIndicatorEngine indicators,
        IRegimeClassifier regime,
        MarketContextBuilder ctxBuilder,
        IEnumerable<IStrategy> strategies,
        OrderStateMachine stateMachine,
        ISlippageModel slippage,
        IOptionsMonitor<RiskOptions> riskOpts,
        BacktestRunRepository runs,
        BacktestSignalRepository btSignals,
        BacktestOrderRepository btOrders,
        BacktestFillRepository btFills,
        BacktestPositionRepository btPositions,
        BacktestTradeHistoryRepository btTrades,
        BacktestAccountSnapshotRepository btSnapshots,
        ILogger<BacktestEngine> log)
    {
        _options      = options.Value;
        _clock        = clock;
        _candles      = candles;
        _symbols      = symbols;
        _indicators   = indicators;
        _regime       = regime;
        _ctxBuilder   = ctxBuilder;
        _strategies   = strategies;
        _stateMachine = stateMachine;
        _slippage     = slippage;
        _riskOpts     = riskOpts;
        _runs         = runs;
        _btSignals    = btSignals;
        _btOrders     = btOrders;
        _btFills      = btFills;
        _btPositions  = btPositions;
        _btTrades     = btTrades;
        _btSnapshots  = btSnapshots;
        _log          = log;
    }

    public async Task<long> RunAsync(BacktestRunOptions runOpts, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // 1. Resolve the strategy and the symbol.
        var strategy = ResolveStrategy(runOpts.StrategyCode);
        var symbol   = await _symbols.GetByExchangeAndCodeAsync(
                Exchanges.BinanceSpot, runOpts.SymbolCode, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Symbol '{runOpts.SymbolCode}' not found in dbo.Symbols (exchange BINANCE_SPOT). "
                + "Run reference-data refresh first or seed via ./Make-DevDb.ps1.");

        // 2. Insert the BacktestRun row and transition to RUNNING.
        var run = new BacktestRun
        {
            RunKind             = runOpts.RunKind,
            ParentRunId         = runOpts.ParentRunId,
            Strategy            = strategy.Name,
            Symbols             = symbol.SymbolCode,
            AccountType         = _options.AccountType,
            FromUtc             = runOpts.FromUtc,
            ToUtc               = runOpts.ToUtc,
            StartingEquityUsd   = _options.StartingEquityUsd,
            Seed                = _options.RandomSeed,
            ParametersJson      = SerializeFrozenConfig(),
            FeeMakerBps         = MakerBps(),
            FeeTakerBps         = TakerBps(),
            SlippageModelVersion = _slippage.Version,
            Status              = RunStatuses.Pending,
            StartedAt           = DateTime.UtcNow,
            Notes               = runOpts.Notes,
        };
        var runId = await _runs.InsertAsync(run, ct).ConfigureAwait(false);
        await _runs.UpdateStatusAsync(runId, RunStatuses.Running, ct).ConfigureAwait(false);

        // 3. Load candles for the primary timeframe + the higher one (if any).
        var primaryTf = strategy.PrimaryTimeframe;
        var candleList = await _candles.GetRangeAsync(
                symbol.SymbolId, primaryTf, runOpts.FromUtc, runOpts.ToUtc, ct)
            .ConfigureAwait(false);
        if (candleList.Count == 0)
        {
            await _runs.FinalizeAsync(runId, RunStatuses.Failed, DateTime.UtcNow, sw.ElapsedMilliseconds,
                0, 0, _options.StartingEquityUsd, null,
                $"No {primaryTf} candles in window for SymbolId={symbol.SymbolId}",
                ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"No candles for {symbol.SymbolCode}@{primaryTf} between {runOpts.FromUtc:O} and {runOpts.ToUtc:O}.");
        }

        // 4. Build engine state.
        var book      = new Bookkeeper(_options.StartingEquityUsd, _options.AccountType);
        var sim       = new SimulatedExchange(_options, _slippage);
        var risk      = new BacktestRiskManager(_riskOpts, symbol, new ZeroAtrSizingContext());
        var curve     = new List<EquityPoint>(candleList.Count);
        var clientOidByPosition = new Dictionary<string, long>(StringComparer.Ordinal); // entry COID → positionId
        long signalsGenerated = 0, tradesClosed = 0;

        try
        {
            // 5. Replay loop.
            foreach (var bar in candleList.OrderBy(c => c.OpenTime))
            {
                _clock.Set(bar.OpenTime);

                // 5a. Process any pending orders against this bar.
                var fills = sim.ProcessBar(bar);
                foreach (var f in fills.OrderBy(f => f.ClientOrderId, StringComparer.Ordinal))
                {
                    await ApplyFillAsync(runId, symbol, f, sim, book, ct).ConfigureAwait(false);
                }

                // 5b. End of bar — clock advances to close so strategies see the full bar.
                _clock.Set(bar.CloseTime);

                // 5c. Compute regime + indicator snapshot for this bar.
                IndicatorSnapshot? snap = null;
                try
                {
                    snap = await _indicators.GetSnapshotAsync(symbol.SymbolId, primaryTf, bar.OpenTime, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "IndicatorEngine snapshot failed at {Bar}; skipping", bar.OpenTime);
                }

                if (snap is null)
                {
                    SnapshotEquity(book, bar, curve);
                    continue;
                }

                var regime = _regime.Classify(snap).Regime;

                // 5d. Run the strategy when the regime allows it.
                if (Array.IndexOf(strategy.AllowedRegimes, regime) < 0)
                {
                    SnapshotEquity(book, bar, curve);
                    continue;
                }

                IndicatorSnapshot? htfSnap = null;
                if (!string.IsNullOrWhiteSpace(strategy.HigherTimeframe))
                {
                    htfSnap = await _indicators.GetHtfSnapshotAsync(
                        symbol.SymbolId, strategy.HigherTimeframe!, bar.OpenTime, ct).ConfigureAwait(false);
                }

                var ctxOrNull = await _ctxBuilder.BuildAsync(
                    symbolId:        symbol.SymbolId,
                    symbolCode:      symbol.SymbolCode,
                    primaryInterval: primaryTf,
                    barOpenTime:     bar.OpenTime,
                    barCandle:       bar,
                    higherTimeframe: strategy.HigherTimeframe,
                    nowUtc:          _clock.UtcNow,
                    contextWindowBars: _options.ContextWindowBars,
                    ct:              ct).ConfigureAwait(false);
                if (ctxOrNull is null)
                {
                    SnapshotEquity(book, bar, curve);
                    continue;
                }

                var candidate = strategy.Evaluate(snap, htfSnap, regime, ctxOrNull);
                if (candidate is null)
                {
                    SnapshotEquity(book, bar, curve);
                    continue;
                }

                signalsGenerated++;

                // 5e. Persist the signal row.
                var signal = new Signal
                {
                    SymbolId    = symbol.SymbolId,
                    Strategy    = candidate.StrategyCode,
                    Interval    = primaryTf,
                    BarOpenTime = bar.OpenTime,
                    Side        = candidate.Side,
                    EntryPrice  = candidate.EntryPrice,
                    StopLoss    = candidate.StopLoss,
                    TakeProfit  = candidate.TakeProfit,
                    AtrValue    = candidate.AtrValue,
                    Regime      = RegimeCodeFor(regime),
                    Confidence  = candidate.Confidence,
                    Status      = SignalStatuses.Generated,
                    Reason      = candidate.Reason,
                    CreatedAt   = bar.CloseTime,
                };
                await _btSignals.InsertAsync(runId, signal, ct).ConfigureAwait(false);

                // 5f. Risk gate.
                var account = AccountSnapshot(book);
                var decision = await risk.ApproveAsync(signal, account, ct).ConfigureAwait(false);
                if (!decision.Approved)
                {
                    SnapshotEquity(book, bar, curve);
                    continue;
                }

                // 5g. Place entry MARKET order — fills at next bar's open.
                var entryCorrId = book.NewCorrelationId();
                var entryClientOid = $"{entryCorrId}-E";
                var entryOrder = new Order
                {
                    SignalId        = signal.SignalId,
                    SymbolId        = symbol.SymbolId,
                    AccountType     = _options.AccountType,
                    ClientOrderId   = entryClientOid,
                    OrderType       = OrderTypes.Market,
                    Side            = candidate.Side,
                    PositionSide    = candidate.Side == Sides.Buy ? PositionSides.Long : PositionSides.Short,
                    Quantity        = decision.Quantity,
                    Status          = OrderStatuses.New,
                    SubmittedAt     = bar.CloseTime,
                    LastUpdatedAt   = bar.CloseTime,
                };
                await _btOrders.InsertAsync(runId, entryOrder, ct).ConfigureAwait(false);
                var pending = sim.Submit(
                    localOrderId:    entryOrder.OrderId,
                    clientOrderId:   entryClientOid,
                    symbol:          symbol.SymbolCode,
                    side:            candidate.Side,
                    orderType:       OrderTypes.Market,
                    quantity:        decision.Quantity,
                    price:           null,
                    stopPrice:       null,
                    timeInForce:     "IOC",
                    reduceOnly:      false,
                    positionSide:    entryOrder.PositionSide,
                    correlationId:   entryCorrId,
                    nowUtc:          bar.CloseTime);

                // Stash signal/qty against the entry COID so we can wire brackets at fill.
                _pendingEntries[entryClientOid] = new PendingEntryContext(
                    SignalId:    signal.SignalId,
                    Strategy:    candidate.StrategyCode,
                    Regime:      RegimeCodeFor(regime),
                    Side:        candidate.Side,
                    Quantity:    decision.Quantity,
                    StopLoss:    candidate.StopLoss,
                    TakeProfit:  candidate.TakeProfit,
                    InitialRiskUsd: decision.RiskUsd ?? 0m,
                    EntryOrderId:   entryOrder.OrderId,
                    OpenedAtSignalBar: bar.OpenTime);

                SnapshotEquity(book, bar, curve);
            }

            // 6. Force-close any positions left at the end of the window so PnL is bounded.
            var lastBar = candleList[^1];
            var openIds = book.Open.Keys.OrderBy(k => k).ToList();
            foreach (var posId in openIds)
            {
                var pos    = book.Open[posId];
                var exitPx = lastBar.Close;
                var feeBps = TakerBps();
                var fee    = pos.Quantity * exitPx * feeBps / 10_000m;
                book.ClosePosition(posId, exitPx, fee, lastBar.CloseTime, out var closed);
                await _btPositions.CloseAsync(posId, lastBar.CloseTime, exitPx,
                    closed.NetPnlUsd ?? 0m, ct).ConfigureAwait(false);
                await WriteTradeHistoryAsync(runId, closed, FillExitReasons.Manual, ct).ConfigureAwait(false);
                tradesClosed++;
            }

            // 7. Write the equity / drawdown CSVs and the markdown + JSON report.
            var allTrades = await _btTrades.GetForRunAsync(runId, ct).ConfigureAwait(false);
            var metrics = MetricsCalculator.Compute(curve, allTrades, _options.StartingEquityUsd);

            var runDir = ReportWriter.EnsureRunDirectory(_options.OutputDirectory, runId);
            await ReportWriter.WriteEquityCurveAsync(runDir, curve, ct).ConfigureAwait(false);
            await ReportWriter.WriteDrawdownCurveAsync(runDir, curve, ct).ConfigureAwait(false);
            await ReportWriter.WriteMetricsJsonAsync(runDir, metrics, ct).ConfigureAwait(false);
            // We need the run record we inserted with the final values for the report.
            run.BacktestRunId = runId;
            await ReportWriter.WriteMarkdownReportAsync(runDir, run, metrics, ct).ConfigureAwait(false);

            await _runs.FinalizeAsync(
                runId,
                RunStatuses.Completed,
                DateTime.UtcNow,
                sw.ElapsedMilliseconds,
                barsReplayed:    candleList.Count,
                tradesGenerated: allTrades.Count,
                finalEquityUsd:  metrics.FinalEquity,
                metricsJson:     JsonSerializer.Serialize(metrics),
                errorMessage:    null,
                ct).ConfigureAwait(false);

            _log.LogInformation(
                "Backtest run #{RunId} completed: {Bars} bars, {Signals} signals, {Trades} trades, "
                + "final equity {Equity:F2} USD, Sharpe {Sharpe:F3}",
                runId, candleList.Count, signalsGenerated, allTrades.Count,
                metrics.FinalEquity, metrics.Sharpe);

            return runId;
        }
        catch (Exception ex)
        {
            await _runs.FinalizeAsync(runId, RunStatuses.Failed, DateTime.UtcNow,
                sw.ElapsedMilliseconds, null, null, null, null, ex.ToString(), ct).ConfigureAwait(false);
            throw;
        }
    }

    // -------------------------------------------------------------------------

    private readonly Dictionary<string, PendingEntryContext> _pendingEntries = new(StringComparer.Ordinal);

    private async Task ApplyFillAsync(
        long runId, Symbol symbol, SimulatedFill fill, SimulatedExchange sim, Bookkeeper book, CancellationToken ct)
    {
        // 1. Persist the fill row.
        var fillRow = new Fill
        {
            OrderId         = fill.LocalOrderId,
            TradeId         = sim.NextTradeId(),
            Quantity        = fill.Quantity,
            Price           = fill.Price,
            Commission      = fill.CommissionUsd,
            CommissionAsset = fill.CommissionAsset,
            IsMaker         = fill.IsMaker,
            TradeTime       = fill.FillTimeUtc,
        };
        await _btFills.InsertAsync(runId, fillRow, ct).ConfigureAwait(false);

        // 2. Transition the order to FILLED.
        var transition = _stateMachine.TryTransition(OrderStatuses.New, OrderStatuses.Filled);
        if (!transition.IsAccepted)
            throw new InvalidOperationException(
                $"Illegal state transition NEW → FILLED for COID {fill.ClientOrderId}: {transition.Reason}");
        await _btOrders.UpdateStatusAsync(
            fill.LocalOrderId, OrderStatuses.Filled, fill.Quantity, fill.Price,
            fill.CommissionUsd, fill.CommissionAsset, fill.FillTimeUtc, ct).ConfigureAwait(false);

        // 3. Entry vs exit dispatch.
        if (string.IsNullOrEmpty(fill.ExitReason))
        {
            await OpenFromEntryFillAsync(runId, symbol, fill, sim, book, ct).ConfigureAwait(false);
        }
        else
        {
            await CloseFromExitFillAsync(runId, fill, sim, book, ct).ConfigureAwait(false);
        }
    }

    private async Task OpenFromEntryFillAsync(
        long runId, Symbol symbol, SimulatedFill fill, SimulatedExchange sim, Bookkeeper book, CancellationToken ct)
    {
        if (!_pendingEntries.Remove(fill.ClientOrderId, out var ctx))
        {
            _log.LogWarning("Entry fill {Coid} has no pending context — orphan; skipping bracket placement", fill.ClientOrderId);
            return;
        }

        var positionRow = new Position
        {
            SymbolId        = symbol.SymbolId,
            AccountType     = _options.AccountType,
            Side            = ctx.Side == Sides.Buy ? PositionSides.Long : PositionSides.Short,
            EntrySignalId   = ctx.SignalId,
            EntryOrderId    = ctx.EntryOrderId,
            Quantity        = fill.Quantity,
            AvgEntryPrice   = fill.Price,
            StopLoss        = ctx.StopLoss,
            TakeProfit      = ctx.TakeProfit,
            InitialRiskUsd  = ctx.InitialRiskUsd,
            OpenedAt        = fill.FillTimeUtc,
            Status          = PositionStatuses.Open,
        };
        var posId = await _btPositions.InsertAsync(runId, positionRow, ct).ConfigureAwait(false);

        var open = new OpenPosition
        {
            PositionId       = posId,
            SymbolId         = symbol.SymbolId,
            Side             = positionRow.Side,
            Quantity         = fill.Quantity,
            AvgEntryPrice    = fill.Price,
            StopLoss         = ctx.StopLoss,
            TakeProfit       = ctx.TakeProfit,
            InitialRiskUsd   = ctx.InitialRiskUsd,
            OpenedAtUtc      = fill.FillTimeUtc,
            EntrySignalId    = ctx.SignalId,
            EntryOrderId     = ctx.EntryOrderId,
            Strategy         = ctx.Strategy,
            Regime           = ctx.Regime,
            EntryFeesUsd     = fill.CommissionUsd,
        };
        book.OpenPosition(open);

        // Place SL and TP bracket orders.
        var slCoid = $"{book.NewCorrelationId()}-SL";
        var tpCoid = $"{book.NewCorrelationId()}-TP";
        open.StopClientOrderId = slCoid;
        open.TpClientOrderId   = tpCoid;
        var slSide = ctx.Side == Sides.Buy ? Sides.Sell : Sides.Buy;
        var tpSide = slSide;

        var slOrder = new Order
        {
            SignalId        = ctx.SignalId,
            SymbolId        = symbol.SymbolId,
            AccountType     = _options.AccountType,
            ClientOrderId   = slCoid,
            OrderType       = OrderTypes.StopMarket,
            Side            = slSide,
            PositionSide    = positionRow.Side,
            Quantity        = fill.Quantity,
            StopPrice       = ctx.StopLoss,
            ReduceOnly      = true,
            Status          = OrderStatuses.New,
            SubmittedAt     = fill.FillTimeUtc,
            LastUpdatedAt   = fill.FillTimeUtc,
        };
        await _btOrders.InsertAsync(runId, slOrder, ct).ConfigureAwait(false);
        sim.Submit(slOrder.OrderId, slCoid, symbol.SymbolCode, slSide, OrderTypes.StopMarket,
            fill.Quantity, null, ctx.StopLoss, "GTC", true, positionRow.Side,
            slCoid, fill.FillTimeUtc);

        var tpOrder = new Order
        {
            SignalId        = ctx.SignalId,
            SymbolId        = symbol.SymbolId,
            AccountType     = _options.AccountType,
            ClientOrderId   = tpCoid,
            OrderType       = OrderTypes.TakeProfitMarket,
            Side            = tpSide,
            PositionSide    = positionRow.Side,
            Quantity        = fill.Quantity,
            StopPrice       = ctx.TakeProfit,
            ReduceOnly      = true,
            Status          = OrderStatuses.New,
            SubmittedAt     = fill.FillTimeUtc,
            LastUpdatedAt   = fill.FillTimeUtc,
        };
        await _btOrders.InsertAsync(runId, tpOrder, ct).ConfigureAwait(false);
        sim.Submit(tpOrder.OrderId, tpCoid, symbol.SymbolCode, tpSide, OrderTypes.TakeProfitMarket,
            fill.Quantity, null, ctx.TakeProfit, "GTC", true, positionRow.Side,
            tpCoid, fill.FillTimeUtc);
    }

    private async Task CloseFromExitFillAsync(
        long runId, SimulatedFill fill, SimulatedExchange sim, Bookkeeper book, CancellationToken ct)
    {
        // Find the open position whose bracket COID matches this fill's COID.
        OpenPosition? match = null;
        foreach (var (posId, pos) in book.Open.OrderBy(kv => kv.Key))
        {
            if (pos.StopClientOrderId == fill.ClientOrderId || pos.TpClientOrderId == fill.ClientOrderId)
            {
                match = pos;
                break;
            }
        }
        if (match is null)
        {
            _log.LogWarning("Exit fill {Coid} has no matching open position; skipping", fill.ClientOrderId);
            return;
        }

        var sibling = fill.ClientOrderId == match.StopClientOrderId
            ? match.TpClientOrderId
            : match.StopClientOrderId;
        sim.Cancel(sibling);

        book.ClosePosition(match.PositionId, fill.Price, fill.CommissionUsd, fill.FillTimeUtc, out var closed);
        await _btPositions.CloseAsync(match.PositionId, fill.FillTimeUtc, fill.Price,
            closed.NetPnlUsd ?? 0m, ct).ConfigureAwait(false);
        await WriteTradeHistoryAsync(runId, closed, fill.ExitReason, ct).ConfigureAwait(false);
    }

    private async Task WriteTradeHistoryAsync(long runId, OpenPosition closed, string exitReason, CancellationToken ct)
    {
        var holding = (int)Math.Round(((closed.ClosedAtUtc!.Value - closed.OpenedAtUtc).TotalMinutes), MidpointRounding.AwayFromZero);
        var rPerUnit = Math.Abs(closed.AvgEntryPrice - closed.StopLoss);
        var rMult    = rPerUnit > 0m && closed.NetPnlUsd is not null
            ? closed.NetPnlUsd!.Value / (rPerUnit * closed.Quantity)
            : 0m;
        var trade = new BacktestTrade(
            TradeHistoryId: 0,
            PositionId:     closed.PositionId,
            SymbolId:       closed.SymbolId,
            Strategy:       closed.Strategy,
            Regime:         closed.Regime,
            Side:           closed.Side,
            EntryTime:      closed.OpenedAtUtc,
            ExitTime:       closed.ClosedAtUtc!.Value,
            HoldingMinutes: holding,
            EntryPrice:     closed.AvgEntryPrice,
            ExitPrice:      closed.ExitPrice ?? 0m,
            Quantity:       closed.Quantity,
            GrossPnlUsd:    closed.GrossPnlUsd ?? 0m,
            FeesUsd:        closed.EntryFeesUsd + (closed.ExitFeesUsd ?? 0m),
            NetPnlUsd:      closed.NetPnlUsd ?? 0m,
            RMultiple:      rMult,
            ExitReason:     exitReason);
        await _btTrades.InsertAsync(runId, trade, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------

    private void SnapshotEquity(Bookkeeper book, Candle bar, List<EquityPoint> curve)
    {
        var pt = book.Snapshot(bar.CloseTime, bar.Close);
        curve.Add(pt);
    }

    private AccountRiskState AccountSnapshot(Bookkeeper book)
    {
        // Equity uses cash + unrealised PnL valued at the latest sample. We
        // can't pass markPrice here so we use cash-only when no positions are
        // open; with positions open, callers have already snapshot()ed the
        // curve and we have a fresh number. Equivalent: equity = book.HighWaterMarkUsd
        // for HWM purposes, cash + unrealized for daily PnL. Simplification
        // for the backtest: drawdown ≈ 0 between sample points (the next
        // SnapshotEquity will refresh it).
        return new AccountRiskState(
            AccountType:        _options.AccountType,
            EquityUsd:          book.CashUsd,                  // refreshed at SnapshotEquity
            AvailableUsd:       book.CashUsd,
            UnrealizedPnlUsd:   0m,
            OpenPositions:      book.Open.Count,
            GrossExposureUsd:   0m,
            NetExposureUsd:     0m,
            HighWaterMarkUsd:   book.HighWaterMarkUsd,
            DrawdownPct:        book.HighWaterMarkUsd > 0 ? (book.CashUsd / book.HighWaterMarkUsd) - 1m : 0m,
            DailyPnlPct:        0m,                              // §10 simplification — no daily reset gating
            EquityAt00UtcUsd:   book.StartingEquityUsd,
            SnapshotTimeUtc:    _clock.UtcNow);
    }

    private static string RegimeCodeFor(Regime r) => r switch
    {
        Regime.TrendingUp   => RegimeCodes.TrendingUp,
        Regime.TrendingDown => RegimeCodes.TrendingDown,
        Regime.Ranging      => RegimeCodes.Ranging,
        Regime.Volatile     => RegimeCodes.Volatile,
        Regime.Compressing  => RegimeCodes.Compressing,
        _                   => RegimeCodes.Unknown,
    };

    private IStrategy ResolveStrategy(string code)
    {
        var match = _strategies.FirstOrDefault(s => string.Equals(s.Name, code, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException(
            $"Unknown strategy code '{code}'. Registered: {string.Join(", ", _strategies.Select(s => s.Name))}");
    }

    private decimal MakerBps() => _options.AccountType == AccountTypes.Spot ? _options.SpotMakerBps : _options.UmFutMakerBps;
    private decimal TakerBps() => _options.AccountType == AccountTypes.Spot ? _options.SpotTakerBps : _options.UmFutTakerBps;

    private string SerializeFrozenConfig() => JsonSerializer.Serialize(new
    {
        engine = _options,
        risk   = _riskOpts.CurrentValue,
    });

    private sealed record PendingEntryContext(
        long?    SignalId,
        string   Strategy,
        string   Regime,
        string   Side,
        decimal  Quantity,
        decimal  StopLoss,
        decimal  TakeProfit,
        decimal  InitialRiskUsd,
        long     EntryOrderId,
        DateTime OpenedAtSignalBar);

    // Lightweight ATR50-SMA fallback: we don't pre-compute it in S10 v1, so
    // sizing falls back to the default vol-adjust factor. Hooked here so a
    // future iteration can wire the pre-cache without changing risk-manager call sites.
    private sealed class ZeroAtrSizingContext : IBacktestSizingContext
    {
        public decimal? Atr50SmaAt(int symbolId, string interval, DateTime barOpenTime) => null;
    }
}
