using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface IAiJournalRepository
{
    /// <summary>Idempotent on (IsoYear, IsoWeek). Returns the row id.</summary>
    Task<long> UpsertAsync(AiJournalRecord row, CancellationToken cancellationToken);

    Task<AiJournalRecord?> GetByIsoWeekAsync(
        int               isoYear,
        int               isoWeek,
        CancellationToken cancellationToken);
}
