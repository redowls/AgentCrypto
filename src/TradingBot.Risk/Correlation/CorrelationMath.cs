namespace TradingBot.Risk.Correlation;

/// Pearson correlation + greedy single-link clustering. Pure math, no I/O —
/// keeps the §8.3 unit tests trivial. The universe is &lt;100 symbols so an
/// O(N²) implementation is well below noise.
public static class CorrelationMath
{
    /// Computes Pearson correlation of two equal-length return series.
    /// Returns 0 when either series is constant (zero variance) or shorter
    /// than 2 — a degenerate input we can't legitimately correlate, and
    /// flagging it as "uncorrelated" matches the design doc's failsafe.
    public static decimal Pearson(IReadOnlyList<decimal> a, IReadOnlyList<decimal> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
            throw new ArgumentException("series length mismatch", nameof(b));
        if (a.Count < 2) return 0m;

        decimal sumA = 0m, sumB = 0m;
        for (var i = 0; i < a.Count; i++) { sumA += a[i]; sumB += b[i]; }
        var meanA = sumA / a.Count;
        var meanB = sumB / b.Count;

        decimal num = 0m, denA = 0m, denB = 0m;
        for (var i = 0; i < a.Count; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            num += da * db;
            denA += da * da;
            denB += db * db;
        }

        if (denA == 0m || denB == 0m) return 0m;
        // decimal has no sqrt; fall back to double for the denominator only.
        var den = (decimal)Math.Sqrt((double)(denA * denB));
        if (den == 0m) return 0m;

        var r = num / den;
        if (r > 1m) r = 1m;
        if (r < -1m) r = -1m;
        return r;
    }

    /// Convert a price series into log returns. We use simple returns (close /
    /// prevClose - 1) which match the design's "30-day daily-return correlation"
    /// language and behave identically for tiny moves. Output length is
    /// inputs.Count - 1; a series of length &lt; 2 yields an empty array.
    public static decimal[] SimpleReturns(IReadOnlyList<decimal> closes)
    {
        ArgumentNullException.ThrowIfNull(closes);
        if (closes.Count < 2) return Array.Empty<decimal>();
        var r = new decimal[closes.Count - 1];
        for (var i = 1; i < closes.Count; i++)
        {
            var prev = closes[i - 1];
            r[i - 1] = prev == 0m ? 0m : (closes[i] / prev) - 1m;
        }
        return r;
    }

    /// Greedy single-link clustering: walk pairs in descending correlation
    /// order; whenever a pair exceeds <paramref name="threshold"/>, merge
    /// their clusters via union-find. Output is a parent array (cluster id
    /// per symbol index). Symbols absent from any over-threshold pair end up
    /// in singleton clusters.
    ///
    /// <paramref name="symbolCount"/> is the number of symbols in the
    /// universe. <paramref name="pairs"/> is the upper-triangle pair list:
    /// items with <c>I &lt;= J</c>, no diagonal. Both must be filled before
    /// calling; ordering doesn't matter — the function sorts internally.
    public static int[] AssignClusters(
        int symbolCount,
        IReadOnlyList<(int I, int J, decimal Corr)> pairs,
        decimal threshold)
    {
        if (symbolCount < 0) throw new ArgumentOutOfRangeException(nameof(symbolCount));
        ArgumentNullException.ThrowIfNull(pairs);

        var parent = new int[symbolCount];
        for (var i = 0; i < symbolCount; i++) parent[i] = i;

        // Walk pairs in descending |corr| so the strongest links merge first.
        // We compare against threshold using the SIGNED value: §8.3 talks
        // about "+0.7 to +0.9 BTC correlation" → positive threshold.
        var sorted = pairs.OrderByDescending(p => p.Corr);
        foreach (var (i, j, c) in sorted)
        {
            if (c < threshold) break; // sorted desc; nothing else qualifies.
            Union(parent, i, j);
        }

        // Compress so cluster ids are dense small integers (0..k-1).
        var dense = new Dictionary<int, int>();
        var labels = new int[symbolCount];
        for (var i = 0; i < symbolCount; i++)
        {
            var root = Find(parent, i);
            if (!dense.TryGetValue(root, out var label))
            {
                label = dense.Count;
                dense[root] = label;
            }
            labels[i] = label;
        }
        return labels;
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]]; // path compression by halving.
            x = parent[x];
        }
        return x;
    }

    private static void Union(int[] parent, int a, int b)
    {
        var ra = Find(parent, a);
        var rb = Find(parent, b);
        if (ra == rb) return;
        parent[ra] = rb;
    }
}
