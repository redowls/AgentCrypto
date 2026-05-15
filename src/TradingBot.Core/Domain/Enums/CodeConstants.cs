namespace TradingBot.Core.Domain.Enums;

// Constant string codes used for Side / Status / OrderType / ExitReason fields.
// Entities store these as plain strings (matching the VARCHAR columns); these
// constants exist so call sites can avoid magic literals.
public static class Sides
{
    public const string Buy  = "BUY";
    public const string Sell = "SELL";
}

public static class PositionSides
{
    public const string Long  = "LONG";
    public const string Short = "SHORT";
}

public static class AccountTypes
{
    public const string Spot  = "SPOT";
    public const string UmFut = "UMFUT";
}

public static class Exchanges
{
    public const string BinanceSpot  = "BINANCE_SPOT";
    public const string BinanceUmFut = "BINANCE_UMFUT";
}

public static class SignalStatuses
{
    public const string Generated = "GENERATED";
    public const string Approved  = "APPROVED";
    public const string Rejected  = "REJECTED";
    public const string Executed  = "EXECUTED";
    public const string Expired   = "EXPIRED";
}

public static class OrderStatuses
{
    // Pre-exchange (local-only) states from the §6.4 state machine.
    public const string Pending         = "PENDING";       // local row created, not yet submitted
    public const string Submitting      = "SUBMITTING";    // REST call in flight
    public const string Error           = "ERROR";         // submit failed irrecoverably (terminal)

    // Exchange-acknowledged states.
    public const string New             = "NEW";
    public const string PartiallyFilled = "PARTIALLY_FILLED";
    public const string Filled          = "FILLED";
    public const string Canceling       = "CANCELING";     // local intent — REST cancel in flight
    public const string Cancelled       = "CANCELED";
    public const string Rejected        = "REJECTED";
    public const string Expired         = "EXPIRED";
}

public static class OrderTypes
{
    public const string Limit            = "LIMIT";
    public const string Market           = "MARKET";
    public const string StopMarket       = "STOP_MARKET";
    public const string TakeProfitMarket = "TAKE_PROFIT_MARKET";
    public const string LimitMaker       = "LIMIT_MAKER";
}

public static class PositionStatuses
{
    public const string Open    = "OPEN";
    public const string Closing = "CLOSING";
    public const string Closed  = "CLOSED";
}

public static class ExitReasons
{
    public const string TakeProfit = "TP";
    public const string StopLoss   = "SL";
    public const string Trailing   = "TRAIL";
    public const string Time       = "TIME";
    public const string Manual     = "MANUAL";
    public const string Regime     = "REGIME";
}

public static class CandleIntervals
{
    public const string OneMinute     = "1m";
    public const string FiveMinutes   = "5m";
    public const string FifteenMinutes = "15m";
    public const string OneHour       = "1h";
    public const string FourHours     = "4h";
    public const string OneDay        = "1d";
}
