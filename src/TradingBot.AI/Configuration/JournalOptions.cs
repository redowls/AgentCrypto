using System.ComponentModel.DataAnnotations;

namespace TradingBot.AI.Configuration;

public sealed class JournalOptions
{
    public const string SectionName = "Journal";

    public bool Enabled { get; init; } = true;

    /// <summary>Quartz cron — Sunday 06:00 UTC per §5.4.4 / §9 ops schedule.
    /// Fields: <c>sec min hr dom mon dow</c>.</summary>
    public string Cron { get; init; } = "0 0 6 ? * SUN";

    /// <summary>Where the markdown reports are dropped on disk. The DB row is
    /// the source of truth; this directory exists for n8n's email step.</summary>
    [Required, MinLength(1)]
    public string OutputDirectory { get; init; } = "journals";

    /// <summary>Cap on trades fed to Claude (defensive — a runaway week of
    /// signals shouldn't blow the context window).</summary>
    [Range(1, 10_000)]
    public int MaxTradesPerJournal { get; init; } = 500;

    /// <summary>Polling cadence for the Batch API. The batch SLA is &lt;= 24h
    /// but typical jobs return in &lt; 1 min — poll fast initially.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan BatchPollInterval { get; init; } = TimeSpan.FromSeconds(15);

    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan BatchMaxWait { get; init; } = TimeSpan.FromMinutes(30);
}
