using System.Globalization;
using System.Text;
using TradingBot.AI.Models;

namespace TradingBot.AI.Prompts;

/// <summary>
/// Pure functions that turn typed inputs into the USER blocks for §5.4
/// prompts. Pulled out of the analyzers so the rendered text can be unit-
/// tested directly (no Claude client required).
/// </summary>
public static class UserPromptRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>§5.4.1 USER block — wraps each item in &lt;item ts="…" source="…"&gt;</summary>
    public static string SentimentBatch(IReadOnlyList<NewsItem> items)
    {
        var sb = new StringBuilder(256 + items.Count * 128);
        sb.Append("<news_items>\n");
        foreach (var item in items)
        {
            sb.Append("  <item ts=\"")
              .Append(item.TimestampUtc.ToString("yyyy-MM-ddTHH:mmZ", Inv))
              .Append("\" source=\"")
              .Append(EscapeXmlAttr(item.Source))
              .Append("\">\n    ")
              .Append(EscapeXmlText(item.Headline.Trim()))
              .Append("\n  </item>\n");
        }
        sb.Append("</news_items>\n\n")
          .Append(SystemPrompts.SentimentNdjsonFooter);
        return sb.ToString();
    }

    /// <summary>§5.4.2 USER block — readings list mirrors the example
    /// formatting (two-decimal indices, three-decimal ATR ratio, etc.).</summary>
    public static string RegimeReadings(RegimeSnapshot snap)
    {
        var sb = new StringBuilder(256);
        sb.Append("Symbol: ").Append(snap.Symbol)
          .Append("  TF: ").Append(snap.Interval).Append('\n');
        sb.Append("Readings:\n")
          .Append("  ADX(14)=").Append(F1(snap.Adx14)).Append('\n')
          .Append("  +DI=").Append(F1(snap.PlusDi14))
          .Append("  -DI=").Append(F1(snap.MinusDi14)).Append('\n')
          .Append("  ATR(14)=").Append(F1(snap.Atr14))
          .Append("  ATR50_SMA=").Append(F1(snap.Atr50Sma))
          .Append("  ATR_ratio=").Append(F3(snap.AtrRatio)).Append('\n')
          .Append("  BBWidth_pct=").Append(F3(snap.BbWidthPct))
          .Append("  BBWidth_pct_50pctl=").Append(F2(snap.BbWidthPct50pctl)).Append('\n')
          .Append("  EMA(9)=").Append(F1(snap.Ema9))
          .Append("  EMA(21)=").Append(F1(snap.Ema21))
          .Append("  EMA(50)=").Append(F1(snap.Ema50))
          .Append("  EMA(200)=").Append(F1(snap.Ema200)).Append('\n')
          .Append("  Last 20 closes slope/bar: ").Append(SignedPct(snap.Last20BarSlopePct)).Append('\n');
        sb.Append("Output JSON.");
        return sb.ToString();
    }

    /// <summary>§5.4.3 USER block — sequence and labels match the design
    /// doc example exactly (including "1.5*ATR" / "3*ATR" annotation, the
    /// "(N items)" sentiment summary, and the "Concerns to consider" tail).
    /// </summary>
    public static string SetupReview(SetupContext ctx)
    {
        var sb = new StringBuilder(384);
        sb.Append("Strategy: ").Append(ctx.Strategy)
          .Append("  Symbol: ").Append(ctx.Symbol)
          .Append("  Side: ").Append(ctx.Side).Append('\n');
        sb.Append("Entry: ").Append(F2(ctx.Entry))
          .Append("  SL: ").Append(F2(ctx.StopLoss))
              .Append(" (").Append(FStripped(ctx.AtrMultipleStop)).Append("*ATR)")
          .Append("  TP: ").Append(F2(ctx.TakeProfit))
              .Append(" (").Append(FStripped(ctx.AtrMultipleTake)).Append("*ATR)")
          .Append('\n');
        sb.Append("Regime (rule): ").Append(ctx.RuleRegime)
          .Append(" (ADX ").Append(F0(ctx.RuleAdx)).Append(")\n");
        sb.Append("News sentiment last 6h: ").Append(SignedScore(ctx.SentimentScore6h))
          .Append(" (").Append(ctx.SentimentItems6h).Append(" items)\n");
        sb.Append("Setup features:\n")
          .Append("  Breakout magnitude: ").Append(SignedPct(ctx.BreakoutMagnitudePct)).Append(" above prior 20-bar high\n")
          .Append("  Volume confirmation: ").Append(F1(ctx.VolumeXSma20)).Append("x SMA20\n")
          .Append("  EMA200 distance: ").Append(SignedPct(ctx.Ema200DistancePct)).Append('\n')
          .Append("  Last 5 ").Append(ctx.Strategy).Append(" trades on this symbol: ")
          .Append(ctx.StrategyHistorySummary).Append('\n');
        sb.Append("Concerns to consider: late entry, exhaustion, news risk in next 8h.");
        return sb.ToString();
    }

    /// <summary>§5.4.4 USER block — header line then a CSV body. The
    /// CSV column set is derived from <c>dbo.TradeHistory + dbo.Signals</c>.</summary>
    public static string JournalCsv(string csv) =>
        "<csv of last week's trades from dbo.TradeHistory + linked Signals>\n" + csv;

    // ── formatting helpers ────────────────────────────────────────────────
    private static string F0(decimal v) => v.ToString("0",   Inv);
    private static string F1(decimal v) => v.ToString("0.0", Inv);
    private static string F2(decimal v) => v.ToString("0.00",Inv);
    private static string F3(decimal v) => v.ToString("0.000",Inv);

    /// <summary>Strips trailing zeros so 1.5m → "1.5" and 3.0m → "3".</summary>
    private static string FStripped(decimal v)
    {
        var s = v.ToString("0.###", Inv);
        return s;
    }

    private static string SignedScore(decimal v)
        => (v >= 0 ? "+" : "") + v.ToString("0.00", Inv);

    /// <summary>
    /// Variable-precision percentage with sign — matches the §5.4 examples
    /// where "+6.4%" and "+0.38%" coexist (one and two decimal places).
    /// We trim trailing zeros so 6.40 → "6.4" but 0.38 → "0.38".
    /// </summary>
    private static string SignedPct(decimal v)
        => (v >= 0 ? "+" : "") + v.ToString("0.0#", Inv) + "%";

    private static string EscapeXmlAttr(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    private static string EscapeXmlText(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
