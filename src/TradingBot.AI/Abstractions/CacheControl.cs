namespace TradingBot.AI.Abstractions;

/// <summary>
/// Per-call caching policy. <see cref="UseAnthropicPromptCache"/> drives
/// the <c>cache_control</c> marker on the system block of the outgoing
/// Messages request — set true for static system prompts so Anthropic charges
/// only 10% of the base input price for the cached prefix on subsequent
/// calls within the 5-minute TTL.
///
/// <see cref="LocalCacheTtl"/> drives the SHA-256(input)-keyed lookup
/// against <c>dbo.AiInteractions</c>: when a row with a matching hash
/// exists and is younger than the TTL, the recorded output is returned
/// without contacting the API at all (per §5.5 — sentiment 5m, regime 1h,
/// confirmation 30s, journal no-cache).
/// </summary>
public sealed record CacheControl(bool UseAnthropicPromptCache, TimeSpan LocalCacheTtl)
{
    public static readonly CacheControl None =
        new(UseAnthropicPromptCache: false, LocalCacheTtl: TimeSpan.Zero);

    public static CacheControl Sentiment   { get; } = new(true, TimeSpan.FromMinutes(5));
    public static CacheControl Regime      { get; } = new(true, TimeSpan.FromHours(1));
    public static CacheControl Confirmation{ get; } = new(true, TimeSpan.FromSeconds(30));
    public static CacheControl Journal     { get; } = new(true, TimeSpan.Zero);
}
