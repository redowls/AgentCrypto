namespace TradingBot.AI.Abstractions;

/// <summary>
/// Reads aggregated AI cost from <c>dbo.AiInteractions</c> for a date window.
/// Used by the daily digest job (§11) so the Observability module doesn't
/// need to know the AI cost journal schema.
/// </summary>
public interface IDailyAiCostReader
{
    Task<decimal> GetTotalForDayAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken);
}
