namespace TradingBot.Execution.Identity;

/// <summary>
/// Generates the Binance <c>newClientOrderId</c> per §6:
///
///   <c>BOT-{strategyShort}-{signalId}-{guid8}</c>
///
/// Constraints:
///   • ≤ 36 chars (Binance Spot limit; Futures allows 36 too).
///   • Only [a-zA-Z0-9_-] (regex <c>^[\.A-Z\:/a-z0-9_-]{1,36}$</c> per docs;
///     we stick to the safe ASCII alnum + dash subset).
///   • Deterministic in (signalId, purpose) so a retry produces the SAME id —
///     idempotency-by-clientOrderId is the contract that prevents double
///     submission. The guid suffix is *seeded* by signalId+purpose, NOT
///     random, so two retries of the same intent collide on the unique
///     index in <c>dbo.Orders</c>.
///
/// Usage:
///   <code>
///   var entryCid   = ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", signalId);
///   var stopCid    = ClientOrderIdGenerator.ForBracket("BR", signalId, BracketLeg.Stop);
///   var tpCid      = ClientOrderIdGenerator.ForBracket("BR", signalId, BracketLeg.TakeProfit);
///   var trailCid   = ClientOrderIdGenerator.ForTrailingReplacement(signalId, sequence: 3);
///   </code>
/// </summary>
public static class ClientOrderIdGenerator
{
    public const int MaxLength = 36;
    private const string Prefix = "BOT-";

    public enum BracketLeg { Stop, TakeProfit }

    /// Stable strategy abbreviation used inside the clientOrderId. Anything
    /// outside the canonical set falls back to a sanitised 4-char prefix.
    public static string ShortenStrategy(string strategy) => strategy switch
    {
        "BREAKOUT_DON"  => "BD",
        "MR_BB_VWAP"    => "MR",
        "TREND_EMA_ADX" => "TR",
        _ => SanitizeForCid(strategy).Substring(0, Math.Min(4, SanitizeForCid(strategy).Length)),
    };

    public static string ForEntry(string strategy, long signalId)
    {
        var shortStrat = ShortenStrategy(strategy);
        // Deterministic 8-char suffix from (signalId, "ENTRY"). Not for
        // security — purely for collision avoidance on resubmits.
        var suffix = StableSuffix($"{signalId}|ENTRY");
        return Build(shortStrat, signalId, suffix);
    }

    public static string ForBracket(string shortPurpose, long signalId, BracketLeg leg)
    {
        var legCode = leg == BracketLeg.Stop ? "SL" : "TP";
        var suffix = StableSuffix($"{signalId}|{legCode}");
        // Encode the leg into the strat slot so SL/TP are visibly distinct.
        return Build($"{shortPurpose}{legCode}", signalId, suffix);
    }

    /// Trailing-stop replacements are issued repeatedly for the same signal;
    /// the <paramref name="sequence"/> ensures each new replacement gets a
    /// fresh, unique clientOrderId while remaining idempotent within the
    /// same (signal, sequence) pair.
    public static string ForTrailingReplacement(long signalId, int sequence)
    {
        var suffix = StableSuffix($"{signalId}|TR|{sequence}");
        // "TR" namespace plus sequence digit packed into the strat slot.
        return Build($"TR{sequence:000}", signalId, suffix);
    }

    public static string ForReconciliationProbe(long signalId)
    {
        var suffix = StableSuffix($"{signalId}|REC|{DateTime.UtcNow.Ticks}");
        return Build("REC", signalId, suffix);
    }

    private static string Build(string shortStrat, long signalId, string suffix)
    {
        // Build prefix = "BOT-{strat}-{signalId}-{suffix}" but cap to 36 chars.
        // Maximum signalId today fits in ~12 digits; leave at least 8 for the
        // suffix and 4 for the strategy slug.
        var raw = $"{Prefix}{shortStrat}-{signalId}-{suffix}";
        if (raw.Length > MaxLength)
        {
            var trim = raw.Length - MaxLength;
            // Trim from the suffix first (collisions are improbable: 8→0 chars
            // shaved still leaves the deterministic strat+signalId gluing).
            var newSuffix = suffix.Length > trim ? suffix[..^trim] : string.Empty;
            raw = $"{Prefix}{shortStrat}-{signalId}-{newSuffix}";
            if (raw.Length > MaxLength)
                raw = raw[..MaxLength];
        }
        return raw;
    }

    /// 8-char base32-style suffix derived from a SHA-256 of the seed. Not a
    /// guid — fixed-length and safe for the Binance regex.
    internal static string StableSuffix(string seed)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        // base32 alphabet (Crockford-ish, no padding). 8 chars × 5 bits = 40 bits
        // — collision probability per (signalId, purpose) is negligible.
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        Span<char> buf = stackalloc char[8];
        for (var i = 0; i < 8; i++)
            buf[i] = alphabet[bytes[i] & 0x1F];
        return new string(buf);
    }

    private static string SanitizeForCid(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var ch in s)
        {
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                buf[n++] = ch;
        }
        return new string(buf[..n]);
    }
}
