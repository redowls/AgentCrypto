using TradingBot.Exchange.Abstractions;

namespace TradingBot.MarketData.Abstractions;

/// <summary>
/// In-process envelope around <see cref="KlineData"/> that pins the kline to a
/// specific (SymbolId, Symbol, Interval, Account) tuple. The persistor needs
/// SymbolId for FK-correct upsert; the ingestor knows it from the symbol map
/// constructed at startup, so we resolve once at the producer side rather than
/// every consumer doing it.
/// </summary>
/// <param name="Source">Where this kline came from (REST backfill vs WS).
/// Persistor uses this only for logging — both paths take the same code path.</param>
public sealed record KlineEvent(
    int          SymbolId,
    string       Symbol,
    string       Interval,
    AccountType  Account,
    KlineData    Kline,
    KlineSource  Source);

public enum KlineSource
{
    RestBackfill = 0,
    WebSocket    = 1,
    GapBackfill  = 2,
}
