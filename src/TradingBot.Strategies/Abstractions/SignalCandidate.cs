using TradingBot.Core.Domain.Enums;

namespace TradingBot.Strategies.Abstractions;

/// <summary>
/// A strategy's output for one bar close. The SignalEngine converts approved
/// candidates into <see cref="TradingBot.Core.Domain.Signal"/> rows
/// (Status=GENERATED) and forwards them to the next stage (AI confirmation /
/// risk gate).
///
/// Pure data — strategies never persist anything themselves.
/// </summary>
/// <param name="StrategyType">Which strategy emitted this candidate.</param>
/// <param name="StrategyCode">Persistence string (matches <see cref="StrategyCodes"/>).</param>
/// <param name="Side">"BUY" / "SELL" — see <see cref="Sides"/>.</param>
/// <param name="EntryPrice">Reference entry — typically bar close. The execution
/// engine may convert this into a stop-market or limit depending on slippage policy.</param>
/// <param name="StopLoss">Initial protective stop, in price terms.</param>
/// <param name="TakeProfit">Initial take-profit target. Strategies that use a
/// staged TP1/TP2 (mean reversion §3.2, trend §3.3) put the *first* target
/// here; the runner / partial-take logic lives in the position manager.</param>
/// <param name="AtrValue">ATR(14) at signal generation — persisted on the
/// signal so SL/TP can be reproduced offline.</param>
/// <param name="Confidence">Strategy's own confidence in [0, 1]. Layered with
/// regime confidence and AI confirmation downstream.</param>
/// <param name="Reason">Free-form explanation logged with the signal — e.g.
/// "Donchian-Up break, vol=1.8×SMA, ADX=27".</param>
/// <param name="Trail">Optional trail descriptor — non-null for breakout (§3.1)
/// and trend (§3.3); null for mean reversion (§3.2).</param>
/// <param name="TimeStopBars">Hard time-stop in bars of the strategy's primary
/// interval. Zero means "no time stop" (not used today).</param>
public sealed record SignalCandidate(
    StrategyType   StrategyType,
    string         StrategyCode,
    string         Side,
    decimal        EntryPrice,
    decimal        StopLoss,
    decimal        TakeProfit,
    decimal        AtrValue,
    decimal        Confidence,
    string         Reason,
    TrailSpec?     Trail,
    int            TimeStopBars);

/// <summary>
/// Trail configuration carried with a signal. The position manager applies
/// these once the trail-activation R-multiple is reached.
/// </summary>
/// <param name="Mode">Which trail formula to use at update time.</param>
/// <param name="ChandelierLookback">Lookback for the Chandelier high-water (§3.1: 22).</param>
/// <param name="ChandelierAtrMultiplier">ATR multiplier inside the Chandelier formula (§3.1: 3).</param>
/// <param name="ActivationRMultiple">Activate the trail once price is this many R favorable
/// (§3.1: 1.5R; §3.3: 1R).</param>
public sealed record TrailSpec(
    TrailMode Mode,
    int       ChandelierLookback,
    decimal   ChandelierAtrMultiplier,
    decimal   ActivationRMultiple);

public enum TrailMode
{
    /// <summary>Chandelier exit only — used by §3.1 (BREAKOUT_DON).</summary>
    Chandelier = 1,

    /// <summary>EMA21 trail or 2×ATR Chandelier, whichever is tighter — §3.3 (TREND_EMA_ADX).</summary>
    Ema21OrChandelier = 2,
}
