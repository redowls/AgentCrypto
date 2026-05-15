namespace TradingBot.Risk.Abstractions;

/// <summary>
/// Real-time risk view of the account. Built by <see cref="IAccountSnapshotProvider"/>
/// from open <c>dbo.Positions</c> + Binance live prices + the AccountSnapshots
/// HWM. The §8 gates consume this — never call out to the exchange themselves.
/// </summary>
/// <param name="AccountType">"SPOT" or "UMFUT" — see <see cref="TradingBot.Core.Domain.Enums.AccountTypes"/>.</param>
/// <param name="EquityUsd">Cash balance + unrealized PnL on open positions.</param>
/// <param name="AvailableUsd">Cash not committed to margin; used only by audits.</param>
/// <param name="UnrealizedPnlUsd">Mark-to-market PnL summed across open positions.</param>
/// <param name="OpenPositions">Count of <c>Status='OPEN'</c> rows in dbo.Positions.</param>
/// <param name="GrossExposureUsd">Σ |qty × markPrice| across open positions.</param>
/// <param name="NetExposureUsd">Σ (sign × qty × markPrice). Long is positive, short negative.</param>
/// <param name="HighWaterMarkUsd">All-time max equity observed in dbo.AccountSnapshots.</param>
/// <param name="DrawdownPct">(EquityUsd / HighWaterMarkUsd - 1). Always ≤ 0; 0 means
/// equity is at or above HWM.</param>
/// <param name="DailyPnlPct">(EquityUsd / EquityAt00UtcUsd - 1). Negative means
/// we're underwater for the calendar day.</param>
/// <param name="EquityAt00UtcUsd">Anchor used to compute DailyPnlPct. Falls back
/// to the current equity (DailyPnlPct = 0) when no snapshot has been written
/// yet today.</param>
/// <param name="SnapshotTimeUtc">When this view was assembled.</param>
public sealed record AccountRiskState(
    string   AccountType,
    decimal  EquityUsd,
    decimal  AvailableUsd,
    decimal  UnrealizedPnlUsd,
    int      OpenPositions,
    decimal  GrossExposureUsd,
    decimal  NetExposureUsd,
    decimal  HighWaterMarkUsd,
    decimal  DrawdownPct,
    decimal  DailyPnlPct,
    decimal  EquityAt00UtcUsd,
    DateTime SnapshotTimeUtc);
