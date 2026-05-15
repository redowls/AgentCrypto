namespace TradingBot.Core.Domain;

/// One row per (AsOf, SymbolId): the cluster index assigned to a symbol by the
/// greedy partition described in §8.3. Threshold is stored alongside so a
/// reviewer can reproduce the assignment from the row alone.
public sealed class CorrelationCluster
{
    public DateTime AsOf      { get; set; }
    public int      SymbolId  { get; set; }
    public int      Cluster   { get; set; }
    public decimal  Threshold { get; set; }
    public DateTime CreatedAt { get; set; }
}
