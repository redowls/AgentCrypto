using System.Text;
using TradingBot.Data.Abstractions;

namespace TradingBot.Observability.Digest;

/// <summary>
/// Renders a Telegram-Markdown digest of WARN alerts buffered over the
/// last interval. Caps the listing at <see cref="MaxEntries"/>; extra rows
/// collapse to a "+N more" footer. Returns an empty string when there are
/// no rows so the caller can skip the send entirely.
/// </summary>
public static class WarnDigestRenderer
{
    private const int MaxEntries = 30;
    private const int BodyExcerpt = 80;

    public static string Render(IReadOnlyList<AlertJournalRow> rows, DateTime sinceUtc, DateTime untilUtc)
    {
        if (rows.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"*WARN digest* — {rows.Count} alert(s) since {sinceUtc:HH:mm} UTC");
        sb.AppendLine();

        foreach (var row in rows.Take(MaxEntries))
        {
            var excerpt = row.Body.Length > BodyExcerpt
                ? row.Body[..BodyExcerpt] + "…"
                : row.Body;
            sb.AppendLine($"• {row.SentAtUtc:HH:mm} {row.Title} — {excerpt}");
        }

        if (rows.Count > MaxEntries)
            sb.AppendLine($"…+{rows.Count - MaxEntries} more");

        return sb.ToString();
    }
}
