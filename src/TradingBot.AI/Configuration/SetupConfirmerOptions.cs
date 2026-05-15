using System.ComponentModel.DataAnnotations;

namespace TradingBot.AI.Configuration;

public sealed class SetupConfirmerOptions
{
    public const string SectionName = "SetupConfirmer";

    public bool Enabled { get; init; } = true;

    /// <summary>2-second hard wall-clock timeout per S9 spec. On expiry the
    /// confirmer returns the documented fallback {approve=true, size_adj=0.7}.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.250", "00:00:30")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Default size adjustment applied when the timeout fires.</summary>
    [Range(0.1, 1.0)]
    public decimal FallbackSizeAdj { get; init; } = 0.70m;
}
