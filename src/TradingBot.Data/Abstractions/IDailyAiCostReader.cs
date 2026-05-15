namespace TradingBot.Data.Abstractions;

/// <summary>
/// Reads aggregated AI cost from <c>dbo.AiInteractions</c> for a date window.
/// Lives in Data (not AI) so Observability can consume it via the existing
/// Data project reference instead of pulling the whole AI module.
/// </summary>
public interface IDailyAiCostReader
{
    Task<decimal> GetTotalForDayAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken);
}
