namespace TradingBot.Data.Abstractions;

public interface IAlertJournalRepository
{
    Task InsertAsync(AlertJournalRow row, CancellationToken ct);

    /// <param name="severity">null = all severities</param>
    Task<IReadOnlyList<AlertJournalRow>> GetWindowAsync(
        byte? severity, DateTime sinceUtc, DateTime untilUtc, CancellationToken ct);
}
