using System.ComponentModel.DataAnnotations;

namespace TradingBot.AI.Configuration;

public sealed class RegimeConfirmerOptions
{
    public const string SectionName = "RegimeConfirmer";

    public bool Enabled { get; init; } = true;

    /// <summary>4h cadence per §5.4.2.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "06:00:00")]
    public TimeSpan Cadence { get; init; } = TimeSpan.FromHours(4);

    /// <summary>Per S9 spec: only override the rule output when Claude's
    /// confidence in the disagreement crosses this threshold.</summary>
    [Range(0.0, 1.0)]
    public decimal OverrideThreshold { get; init; } = 0.70m;
}
