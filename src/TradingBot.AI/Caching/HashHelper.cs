using System.Security.Cryptography;
using System.Text;

namespace TradingBot.AI.Caching;

internal static class HashHelper
{
    /// <summary>
    /// SHA-256 of the canonical "purpose|model|systemPrompt|userPrompt" string,
    /// returned as lower-case hex. Used as the cache key against
    /// <c>dbo.AiInteractions.InputHash</c> per §5.5.
    ///
    /// Newlines inside the inputs are preserved — two prompts that differ
    /// only in trailing whitespace must produce different hashes (an
    /// operator might add a strategic blank line and we don't want a stale
    /// cache hit to mask it).
    /// </summary>
    public static string Sha256Hex(string purpose, string model, string systemPrompt, string userPrompt)
    {
        var canonical = $"{purpose}|{model}|{systemPrompt}|{userPrompt}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);

        // ToHexStringLower is .NET 9+; on .NET 8 we lower-case the upper-hex.
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
