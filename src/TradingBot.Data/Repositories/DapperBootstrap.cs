using Dapper;
using TradingBot.Core.Domain;

namespace TradingBot.Data.Repositories;

internal static class DapperBootstrap
{
    private static int _initialised;

    /// <summary>
    /// Idempotent. Wires up Dapper-wide settings: underscore-aware column mapping
    /// (so R_Multiple → RMultiple) and a custom map for TradeHistory.
    /// </summary>
    public static void EnsureInitialised()
    {
        if (Interlocked.Exchange(ref _initialised, 1) != 0)
        {
            return;
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;

        // Keep DateTime values as UTC kind on the way out of the DB. Dapper hands
        // back DateTime with Kind=Unspecified by default; the bot treats every time
        // value as UTC, so we re-tag explicitly.
        SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
        SqlMapper.AddTypeHandler(new UtcNullableDateTimeHandler());

        // TradeHistory.RMultiple ↔ R_Multiple is already handled by the underscore
        // setting; no per-type map needed.
        _ = typeof(TradeHistory);
    }

    private sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override DateTime Parse(object value) =>
            DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc);

        public override void SetValue(System.Data.IDbDataParameter parameter, DateTime value)
        {
            parameter.Value = value.Kind == DateTimeKind.Utc
                ? value
                : value.ToUniversalTime();
        }
    }

    private sealed class UtcNullableDateTimeHandler : SqlMapper.TypeHandler<DateTime?>
    {
        public override DateTime? Parse(object value) =>
            value is null or DBNull
                ? null
                : DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc);

        public override void SetValue(System.Data.IDbDataParameter parameter, DateTime? value)
        {
            if (value is null)
            {
                parameter.Value = DBNull.Value;
                return;
            }
            parameter.Value = value.Value.Kind == DateTimeKind.Utc
                ? value.Value
                : value.Value.ToUniversalTime();
        }
    }
}
