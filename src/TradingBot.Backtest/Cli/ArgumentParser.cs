using System.Globalization;
using TradingBot.Backtest.Configuration;
using TradingBot.Backtest.Domain;

namespace TradingBot.Backtest.Cli;

// Tiny GNU-style flag parser — sufficient for `run`, `wfa`, `mc` subcommands.
// We don't pull in System.CommandLine to keep the executable lean.
internal static class ArgumentParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static (string subcommand, IReadOnlyDictionary<string, string> flags, IReadOnlyList<string> rest) Parse(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("Missing subcommand. Expected: run | wfa | mc");

        var sub = args[0].ToLowerInvariant();
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rest  = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var key = token.Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    flags[key] = args[++i];
                }
                else
                {
                    flags[key] = "true";
                }
            }
            else
            {
                rest.Add(token);
            }
        }
        return (sub, flags, rest);
    }

    public static BacktestRunOptions ParseRun(IReadOnlyDictionary<string, string> flags)
    {
        var strategy = Required(flags, "strategy");
        var symbol   = Required(flags, "symbol");
        var from     = ParseUtc(Required(flags, "from"));
        var to       = ParseUtc(Required(flags, "to"));
        if (to <= from) throw new ArgumentException("--to must be > --from");
        flags.TryGetValue("notes", out var notes);
        return new BacktestRunOptions
        {
            StrategyCode = strategy,
            SymbolCode   = symbol,
            FromUtc      = from,
            ToUtc        = to,
            Notes        = notes,
            RunKind      = RunKinds.Run,
        };
    }

    private static string Required(IReadOnlyDictionary<string, string> flags, string key) =>
        flags.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new ArgumentException($"Missing required flag --{key}");

    public static DateTime ParseUtc(string s)
    {
        if (DateTime.TryParse(s, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        throw new ArgumentException($"Could not parse '{s}' as a UTC date/time. Try ISO-8601 (e.g. 2024-01-01).");
    }

    public static int ParseInt(IReadOnlyDictionary<string, string> flags, string key, int fallback) =>
        flags.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, Inv, out var n) ? n : fallback;

    public static long ParseLong(IReadOnlyDictionary<string, string> flags, string key) =>
        flags.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, Inv, out var n)
            ? n
            : throw new ArgumentException($"--{key} must be a long integer");
}
