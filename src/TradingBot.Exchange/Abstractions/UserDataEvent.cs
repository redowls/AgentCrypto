namespace TradingBot.Exchange.Abstractions;

public enum UserDataEventKind
{
    AccountUpdate    = 1,
    OrderUpdate      = 2,
    BalanceUpdate    = 3,
    ListenKeyExpired = 9,
    Other            = 99,
}

public sealed record UserDataEvent(
    UserDataEventKind Kind,
    string            ClientOrderId,
    string?           Symbol,
    string?           Status,
    decimal?          ExecutedQty,
    decimal?          AvgFillPrice,
    long?             ExchangeOrderId,
    DateTime          EventTimeUtc,
    object            Raw);
