using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface IAiInteractionRepository
{
    /// <summary>
    /// Idempotent insert keyed on InputHash. If a row already exists with the same
    /// InputHash for the same Model+Purpose, returns the existing row's id without
    /// inserting (acts as cache lookup).
    /// </summary>
    Task<long> InsertIfNewAsync(AiInteraction interaction, CancellationToken cancellationToken);

    Task<AiInteraction?> GetByHashAsync(
        string purpose,
        string model,
        string inputHash,
        CancellationToken cancellationToken);
}
