using System.Globalization;
using System.Net;
using System.Text;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Digest;

/// <summary>
/// Renders a <see cref="DigestData"/> snapshot as an inline-styled HTML email
/// body. Pure; testable with a golden-file fixture.
/// </summary>
public sealed class DigestRenderer
{
    public string RenderHtml(DigestData d)
    {
        var sb = new StringBuilder();
        sb.Append("<html><body style='font-family:Arial,sans-serif;font-size:13px;'>");
        sb.Append($"<h2>TradingBot daily digest — {d.DayStartUtc:yyyy-MM-dd} UTC</h2>");

        AppendEquity(sb, d);
        AppendClosedTrades(sb, d);
        AppendOpenPositions(sb, d);
        AppendAlerts(sb, d);
        AppendAiCost(sb, d);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void AppendEquity(StringBuilder sb, DigestData d)
    {
        var start = d.EquityStart?.EquityUsd ?? 0m;
        var end   = d.EquityEnd?.EquityUsd   ?? 0m;
        var delta = end - start;
        sb.Append("<h3>Equity</h3>");
        sb.Append("<table border='1' cellpadding='4' cellspacing='0'>");
        sb.Append("<tr><th>Open</th><th>Close</th><th>Δ</th></tr>");
        sb.Append($"<tr><td>{Fmt(start)}</td><td>{Fmt(end)}</td><td>{Fmt(delta)}</td></tr>");
        sb.Append("</table>");
    }

    private static void AppendClosedTrades(StringBuilder sb, DigestData d)
    {
        sb.Append($"<h3>Closed trades ({d.ClosedTrades.Count})</h3>");
        if (d.ClosedTrades.Count == 0)
        {
            sb.Append("<p>No trades closed today.</p>");
            return;
        }

        sb.Append("<table border='1' cellpadding='4' cellspacing='0'>");
        sb.Append("<tr><th>Symbol</th><th>Side</th><th>Qty</th><th>Net PnL</th></tr>");
        foreach (var t in d.ClosedTrades)
            sb.Append($"<tr><td>{t.SymbolId}</td><td>{Esc(t.Side)}</td><td>{t.Quantity}</td><td>{Fmt(t.NetPnlUsd)}</td></tr>");
        sb.Append("</table>");
    }

    private static void AppendOpenPositions(StringBuilder sb, DigestData d)
    {
        sb.Append($"<h3>Open positions ({d.OpenPositions.Count})</h3>");
        if (d.OpenPositions.Count == 0)
        {
            sb.Append("<p>No open positions.</p>");
            return;
        }

        sb.Append("<table border='1' cellpadding='4' cellspacing='0'>");
        sb.Append("<tr><th>Symbol</th><th>Side</th><th>Qty</th><th>Avg entry</th></tr>");
        foreach (var p in d.OpenPositions)
            sb.Append($"<tr><td>{p.SymbolId}</td><td>{Esc(p.Side)}</td><td>{p.Quantity}</td><td>{p.AvgEntryPrice}</td></tr>");
        sb.Append("</table>");
    }

    private static void AppendAlerts(StringBuilder sb, DigestData d)
    {
        sb.Append("<h3>Alerts</h3>");
        if (d.AlertRows.Count == 0)
        {
            sb.Append("<p>No alerts in the last 24h.</p>");
            return;
        }

        var bySev = d.AlertRows.GroupBy(a => a.Severity)
                               .ToDictionary(g => g.Key, g => g.Count());
        sb.Append("<table border='1' cellpadding='4' cellspacing='0'>");
        sb.Append("<tr><th>Severity</th><th>Count</th></tr>");
        foreach (var sev in new[] { (byte)AlertSeverity.Critical, (byte)AlertSeverity.Error, (byte)AlertSeverity.Warn, (byte)AlertSeverity.Info })
            sb.Append($"<tr><td>{(AlertSeverity)sev}</td><td>{(bySev.TryGetValue(sev, out var c) ? c : 0)}</td></tr>");
        sb.Append("</table>");

        var notable = d.AlertRows.Where(a => a.Severity >= (byte)AlertSeverity.Error).Take(20).ToList();
        if (notable.Count > 0)
        {
            sb.Append("<h4>Notable (Error / Critical)</h4><ul>");
            foreach (var a in notable)
                sb.Append($"<li>{a.SentAtUtc:HH:mm} — {Esc(a.Title)}</li>");
            sb.Append("</ul>");
        }
    }

    private static void AppendAiCost(StringBuilder sb, DigestData d)
    {
        sb.Append($"<h3>AI cost</h3><p>{Fmt(d.AiCostUsd)} spent on Claude calls.</p>");
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s);
    private static string Fmt(decimal usd) => usd.ToString("C", CultureInfo.InvariantCulture);
}
