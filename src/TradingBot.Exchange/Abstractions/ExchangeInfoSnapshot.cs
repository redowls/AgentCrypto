namespace TradingBot.Exchange.Abstractions;

public sealed record ExchangeSymbolFilter(
    string  SymbolCode,
    string  BaseAsset,
    string  QuoteAsset,
    decimal TickSize,
    decimal StepSize,
    decimal MinNotional,
    bool    IsActive);

public sealed record ExchangeInfoSnapshot(
    AccountType Account,
    DateTime    FetchedAtUtc,
    IReadOnlyList<ExchangeSymbolFilter> Symbols);
