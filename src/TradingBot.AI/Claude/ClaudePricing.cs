using TradingBot.AI.Configuration;

namespace TradingBot.AI.Claude;

/// <summary>
/// Pricing math for §5.2 cost discipline. We separate "fresh input tokens"
/// (pay base price), "cache read input tokens" (pay 10% of base), and
/// "cache creation input tokens" (pay 1.25× base). Output tokens always
/// pay base. The Batches API path multiplies the final figure by
/// <c>BatchDiscount</c>.
///
/// Anthropic's documented usage envelope returns
/// <c>input_tokens / cache_read_input_tokens / cache_creation_input_tokens /
/// output_tokens</c>; the <c>input_tokens</c> field excludes cached prefix
/// tokens (i.e. the three are additive). We follow that convention.
/// </summary>
internal static class ClaudePricing
{
    public static decimal CostUsd(
        ClaudeOptions opt,
        int           freshInputTokens,
        int           cacheReadInputTokens,
        int           cacheCreationInputTokens,
        int           outputTokens,
        bool          batched)
    {
        const decimal mtok = 1_000_000m;

        var input  = (freshInputTokens         / mtok) *  opt.InputPricePerMTokUsd
                   + (cacheReadInputTokens     / mtok) * (opt.InputPricePerMTokUsd * opt.CacheReadMultiplier)
                   + (cacheCreationInputTokens / mtok) * (opt.InputPricePerMTokUsd * opt.CacheWriteMultiplier);

        var output = (outputTokens / mtok) * opt.OutputPricePerMTokUsd;

        var total = input + output;

        if (batched)
        {
            total *= (1m - opt.BatchDiscount);
        }

        return total;
    }
}
