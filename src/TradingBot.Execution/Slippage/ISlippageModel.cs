namespace TradingBot.Execution.Slippage;

/// <summary>
/// §6.2 — slippage model. Pure function of (mid, spread, size, depth) →
/// expected fill price for one side of the book. Lives behind an interface so
/// the backtest engine and the live observer can swap implementations:
///
///   • Backtests use <see cref="DefaultSlippageModel"/> to fill simulated
///     orders at <c>mid ± slippage</c>.
///   • The live engine submits assuming zero slippage on its own books
///     (i.e. the model does NOT shift our quoted price), but instantiates
///     this same model to compute the *expected* slippage for diagnostics —
///     the difference between expected and observed lands in
///     <c>dbo.ExecutionDiagnostics</c>.
///
/// All slippage values are returned in basis points where 1bp = 0.0001 (so
/// 10bp on a $100 mid = $0.10). Conversion to price is convenience helpers.
/// </summary>
public interface ISlippageModel
{
    /// Stable identifier persisted to <c>dbo.ExecutionDiagnostics.ModelVersion</c>.
    string Version { get; }

    /// Compute the expected slippage for a market-style order.
    SlippageEstimate Estimate(SlippageInputs inputs);
}

/// <param name="MidPrice">Mid-price (best bid + best ask) / 2.</param>
/// <param name="SpreadBps">Quoted spread in basis points = (ask - bid) / mid × 10_000.</param>
/// <param name="OrderQuantity">Order size in base units.</param>
/// <param name="AvailableTopOfBookQuantity">Sum of bid+ask top-of-book qty, or
/// best-effort approximation. When unknown the impact term collapses to 0.</param>
/// <param name="Side">"BUY" or "SELL". Affects sign of the price shift only.</param>
public sealed record SlippageInputs(
    decimal MidPrice,
    decimal SpreadBps,
    decimal OrderQuantity,
    decimal? AvailableTopOfBookQuantity,
    string  Side);

/// <param name="ExpectedPrice">Mid-shifted by the spread + impact terms.</param>
/// <param name="SlippageBps">Total slippage in basis points (positive = worse).</param>
/// <param name="HalfSpreadBps">The crossing-the-spread component.</param>
/// <param name="ImpactBps">The size/liquidity impact component.</param>
/// <param name="ParticipationPct">orderQty / topOfBookQty, or null when unknown.</param>
public sealed record SlippageEstimate(
    decimal  ExpectedPrice,
    decimal  SlippageBps,
    decimal  HalfSpreadBps,
    decimal  ImpactBps,
    decimal? ParticipationPct);
