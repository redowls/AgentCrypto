using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface IRegimeRepository
{
    /// <summary>
    /// Idempotent insert keyed on (SymbolId, Interval, AsOf, Source). Returns
    /// the existing row's RegimeId when a duplicate write arrives — useful for
    /// the bar-close path which can occasionally double-fire on connection
    /// blips. The natural-key UNIQUE constraint UQ_Regimes_Sym_Tf_AsOf_Src
    /// enforces dedup at the database.
    /// </summary>
    Task<long> InsertAsync(RegimeRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Latest persisted regime for (symbol, interval), regardless of source.
    /// Returns null when no classification has been stored yet (cold start).
    /// </summary>
    Task<RegimeRecord?> GetLatestAsync(int symbolId, string interval, CancellationToken cancellationToken);
}
