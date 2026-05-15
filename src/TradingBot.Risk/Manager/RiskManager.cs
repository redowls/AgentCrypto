using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Filters;
using TradingBot.Risk.Abstractions;
using TradingBot.Risk.Configuration;

namespace TradingBot.Risk.Manager;

/// <summary>
/// §8.5 risk gate. Implements the gates a–l in the order specified by the
/// section prompt:
///   a) Daily loss limit                              → DAILY_LOSS_LIMIT
///   b) Max drawdown -15% from HWM                    → MAX_DRAWDOWN_HALT
///   c) Open positions ≥ 4                            → MAX_CONCURRENT_POSITIONS
///   d) Correlation cluster occupied (same side)      → CORRELATION_CLUSTER_OCCUPIED
///   e) Drawdown ladder kFactor (0/0.25/0.5/1.0)
///   f) Vol-adjust factor (ATR ratio)
///   g) Risk dollars = equity × 0.01 × kFactor × volAdjust
///   h) Quantity     = riskDollars / stopDistance
///   i) Clamp to Binance lot/notional filters
///   j) Single-symbol cap 50% equity (clamp)
///   k) Gross exposure cap 200% equity                → GROSS_EXPOSURE
///   l) Funding-rate veto (futures only)              → FUNDING_RATE_HOSTILE
///
/// Plus a pre-gate: <see cref="IKillSwitch.IsTripped"/> short-circuits with
/// <see cref="RiskRejectReasons.KillSwitchActive"/> before any other check.
///
/// The gate is purely synchronous logic except for the kill-switch read,
/// the correlation lookup, and the funding-rate fetch. Sizing math is in
/// <see cref="RiskMath"/> and is pure / unit-tested in isolation.
/// </summary>
public sealed class RiskManager : IRiskManager
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly IPositionRepository _positions;
    private readonly ICorrelationService _correlation;
    private readonly IKillSwitch _killSwitch;
    private readonly ISymbolRepository _symbols;
    private readonly ISymbolFilters _filters;
    private readonly IFundingRateProvider _funding;
    private readonly IRiskEventRepository _riskEvents;
    private readonly IClock _clock;
    private readonly ILogger<RiskManager> _log;

    public RiskManager(
        IOptionsMonitor<RiskOptions> options,
        IPositionRepository positions,
        ICorrelationService correlation,
        IKillSwitch killSwitch,
        ISymbolRepository symbols,
        ISymbolFilters filters,
        IFundingRateProvider funding,
        IRiskEventRepository riskEvents,
        IClock clock,
        ILogger<RiskManager> log)
    {
        _options = options;
        _positions = positions;
        _correlation = correlation;
        _killSwitch = killSwitch;
        _symbols = symbols;
        _filters = filters;
        _funding = funding;
        _riskEvents = riskEvents;
        _clock = clock;
        _log = log;
    }

    public async Task<RiskDecision> ApproveAsync(
        Signal signal,
        AccountRiskState account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(account);
        var opts = _options.CurrentValue;

        // Pre-gate: global kill switch ------------------------------------------------
        _killSwitch.RefreshFromCache();
        if (_killSwitch.IsTripped)
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(RiskRejectReasons.KillSwitchActive, _killSwitch.Reason),
                cancellationToken).ConfigureAwait(false);
        }

        // (a) Daily loss limit --------------------------------------------------------
        if (account.DailyPnlPct <= opts.DailyLossLimitPct)
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(
                    RiskRejectReasons.DailyLossLimit,
                    $"DailyPnlPct={account.DailyPnlPct:P2} ≤ {opts.DailyLossLimitPct:P2}"),
                cancellationToken).ConfigureAwait(false);
        }

        // (b) Max drawdown halt -------------------------------------------------------
        if (account.DrawdownPct <= opts.MaxDrawdownHaltPct)
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(
                    RiskRejectReasons.MaxDrawdownHalt,
                    $"DrawdownPct={account.DrawdownPct:P2} ≤ {opts.MaxDrawdownHaltPct:P2}"),
                cancellationToken).ConfigureAwait(false);
        }

        // (c) Concurrent position cap -------------------------------------------------
        var openPositions = await _positions.GetOpenAsync(cancellationToken).ConfigureAwait(false);
        if (openPositions.Count >= opts.MaxConcurrentPositions)
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(
                    RiskRejectReasons.MaxConcurrentPositions,
                    $"open={openPositions.Count} ≥ {opts.MaxConcurrentPositions}"),
                cancellationToken).ConfigureAwait(false);
        }

        // (d) Correlation cluster -----------------------------------------------------
        var sideForCluster = NormalizeSide(signal.Side);
        if (await _correlation
                .IsClusterOccupiedAsync(signal.SymbolId, sideForCluster, openPositions, cancellationToken)
                .ConfigureAwait(false))
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(
                    RiskRejectReasons.CorrelationClusterOccupied,
                    $"symbol={signal.SymbolId} side={sideForCluster} clustered with an open position"),
                cancellationToken).ConfigureAwait(false);
        }

        // (e) Ladder kFactor ----------------------------------------------------------
        var kFactor = RiskMath.LadderMultiplier(account.DrawdownPct, opts.DrawdownLadder);
        if (kFactor == 0m)
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(
                    RiskRejectReasons.DerislHalt,
                    $"DrawdownPct={account.DrawdownPct:P2} produced 0× ladder multiplier"),
                cancellationToken).ConfigureAwait(false);
        }

        // (f) Vol-adjust --------------------------------------------------------------
        var volAdjust = RiskMath.VolAdjust(signal.AtrValue, atr50Sma: null, opts);

        // (g) Risk dollars ------------------------------------------------------------
        var riskUsd = account.EquityUsd * opts.RiskPerTradeFraction * kFactor * volAdjust;
        if (riskUsd <= 0m)
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(
                    RiskRejectReasons.PreconditionFailed,
                    $"riskUsd={riskUsd} (equity={account.EquityUsd}, k={kFactor}, volAdj={volAdjust})"),
                cancellationToken).ConfigureAwait(false);
        }

        // (h) Quantity ----------------------------------------------------------------
        var stopDistance = Math.Abs(signal.EntryPrice - signal.StopLoss);
        if (stopDistance <= 0m)
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(
                    RiskRejectReasons.PreconditionFailed,
                    $"stopDistance={stopDistance} (entry={signal.EntryPrice}, sl={signal.StopLoss})"),
                cancellationToken).ConfigureAwait(false);
        }
        var rawQty = riskUsd / stopDistance;

        // (i) Lot/notional clamp ------------------------------------------------------
        var symbol = await _symbols.GetByIdAsync(signal.SymbolId, cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(
                    RiskRejectReasons.PreconditionFailed,
                    $"symbol id {signal.SymbolId} not found in dbo.Symbols"),
                cancellationToken).ConfigureAwait(false);
        }

        // Use the live filter snapshot from S3 for the active account; falls
        // back to the persisted symbol row if not loaded (e.g. during tests).
        var filter = TryGetLiveFilter(symbol) ?? symbol;
        decimal clampedQty;
        try
        {
            clampedQty = BinanceFilterClamp.ClampQuantityToStep(rawQty, filter.StepSize);
            if (clampedQty <= 0m || clampedQty * signal.EntryPrice < filter.MinNotional)
            {
                return await LogAndReturnAsync(
                    signal,
                    RiskDecision.Reject(
                        RiskRejectReasons.LotFilterInfeasible,
                        $"clamped qty={clampedQty} step={filter.StepSize} minNotional={filter.MinNotional} entry={signal.EntryPrice}"),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(RiskRejectReasons.PreconditionFailed, ex.Message),
                cancellationToken).ConfigureAwait(false);
        }

        // (j) Single-symbol cap 50% equity (clamp, not reject) ------------------------
        var notional = clampedQty * signal.EntryPrice;
        var capUsd = opts.SingleSymbolCapFraction * account.EquityUsd;
        if (notional > capUsd)
        {
            var qtyAtCap = capUsd / signal.EntryPrice;
            clampedQty = BinanceFilterClamp.ClampQuantityToStep(qtyAtCap, filter.StepSize);
            notional = clampedQty * signal.EntryPrice;

            if (clampedQty <= 0m || notional < filter.MinNotional)
            {
                return await LogAndReturnAsync(
                    signal,
                    RiskDecision.Reject(
                        RiskRejectReasons.SingleSymbolCapInfeasible,
                        $"50% cap={capUsd} produced qty={clampedQty} below step/minNotional"),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // (k) Gross exposure cap (futures) --------------------------------------------
        if (string.Equals(signal.Status, SignalStatuses.Generated, StringComparison.Ordinal))
        {
            // we don't gate by status, just defensive: signals at the gate are GENERATED.
        }
        var totalGross = account.GrossExposureUsd + notional;
        var grossCap = opts.GrossExposureCapMultiple * account.EquityUsd;
        if (totalGross > grossCap)
        {
            return await LogAndReturnAsync(
                signal,
                RiskDecision.Reject(
                    RiskRejectReasons.GrossExposure,
                    $"gross+notional={totalGross} > {grossCap} (={opts.GrossExposureCapMultiple}× equity)"),
                cancellationToken).ConfigureAwait(false);
        }

        // (l) Funding-rate veto (futures only) ----------------------------------------
        if (IsFutures(account.AccountType))
        {
            var hostile = await IsFundingHostileAsync(signal, opts, cancellationToken).ConfigureAwait(false);
            if (hostile is { } reason)
            {
                return await LogAndReturnAsync(
                    signal,
                    RiskDecision.Reject(RiskRejectReasons.FundingRateHostile, reason),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var decision = RiskDecision.Approve(
            quantity: clampedQty,
            riskUsd: riskUsd,
            notionalUsd: notional,
            kFactor: kFactor,
            volAdjust: volAdjust,
            message: $"k={kFactor} volAdj={volAdjust} stopDist={stopDistance}");

        return await LogAndReturnAsync(signal, decision, cancellationToken).ConfigureAwait(false);
    }

    private async Task<decimal?> ResolveAtr50SmaAsync()
    {
        // Hook for future extension: pull ATR50 SMA from the indicator cache.
        // For now the snapshot's signal.AtrValue is the only ATR available
        // here; vol-adjust falls back to the default factor (1.0) when atr50
        // is null. Marked async to keep the seam — currently a no-op.
        return await Task.FromResult<decimal?>(null).ConfigureAwait(false);
    }

    private Symbol? TryGetLiveFilter(Symbol persistedRow)
    {
        try
        {
            var spotFilter = _filters.TryGet(AccountType.Spot, persistedRow.SymbolCode);
            if (spotFilter is not null) return spotFilter;
            return _filters.TryGet(AccountType.UmFutures, persistedRow.SymbolCode);
        }
        catch
        {
            // ReferenceDataService may not yet have loaded; fall back silently.
            return null;
        }
    }

    private async Task<string?> IsFundingHostileAsync(Signal signal, RiskOptions opts, CancellationToken ct)
    {
        var symbol = await _symbols.GetByIdAsync(signal.SymbolId, ct).ConfigureAwait(false);
        if (symbol is null) return null;

        var snap = await _funding.TryGetUpcomingAsync(symbol.SymbolCode, ct).ConfigureAwait(false);
        if (snap is null) return null;

        // Window check: only veto when funding tick is imminent.
        var window = snap.NextFundingTimeUtc - _clock.UtcNow;
        if (window > opts.FundingVetoWindow) return null;

        var absRate = Math.Abs(snap.Rate);
        if (absRate <= opts.FundingVetoAbsThreshold) return null;

        // Sign convention (Binance): positive funding ⇒ longs pay shorts.
        // We're hostile only when our side pays.
        var weArePaying = snap.Rate > 0m
            ? string.Equals(signal.Side, Sides.Buy, StringComparison.Ordinal)
            : string.Equals(signal.Side, Sides.Sell, StringComparison.Ordinal);

        if (!weArePaying) return null;

        return $"|funding|={absRate:P4} > {opts.FundingVetoAbsThreshold:P4}, " +
               $"next tick in {window.TotalMinutes:F1}m, side={signal.Side} pays";
    }

    private static bool IsFutures(string accountType) =>
        string.Equals(accountType, AccountTypes.UmFut, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSide(string side) =>
        string.Equals(side, Sides.Buy, StringComparison.OrdinalIgnoreCase)
            ? PositionSides.Long
            : PositionSides.Short;

    private async Task<RiskDecision> LogAndReturnAsync(
        Signal signal,
        RiskDecision decision,
        CancellationToken ct)
    {
        if (decision.Approved)
        {
            _log.LogInformation(
                "Risk APPROVE corr={SignalId} sym={SymbolId} {Strategy} {Side}: qty={Qty} riskUsd={Risk} notional={Notional} k={K} volAdj={V}",
                signal.SignalId, signal.SymbolId, signal.Strategy, signal.Side,
                decision.Quantity, decision.RiskUsd, decision.NotionalUsd, decision.KFactor, decision.VolAdjust);
        }
        else
        {
            _log.LogWarning(
                "Risk REJECT corr={SignalId} sym={SymbolId} {Strategy} {Side}: {Reason} ({Message})",
                signal.SignalId, signal.SymbolId, signal.Strategy, signal.Side,
                decision.RejectReason, decision.Message);

            try
            {
                await _riskEvents.InsertAsync(new RiskEvent
                {
                    EventTime = _clock.UtcNow,
                    EventType = decision.RejectReason ?? "UNKNOWN",
                    Severity  = "WARN",
                    SymbolId  = signal.SymbolId,
                    SignalId  = signal.SignalId == 0 ? null : signal.SignalId,
                    Payload   = decision.Message,
                    Acted     = true,
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Failed to record RiskEvent for {Reason}", decision.RejectReason);
            }
        }

        return decision;
    }
}
