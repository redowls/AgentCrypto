using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class CandleRepository(IDbConnectionFactory connectionFactory) : ICandleRepository
{
    private const int BulkBatchSize = 500;

    static CandleRepository() => DapperBootstrap.EnsureInitialised();

    // ------------------------------------------------------------------------
    // Single-row idempotent upsert. MERGE on the natural key
    // (SymbolId, Interval, OpenTime). Used for streaming kline closes.
    // ------------------------------------------------------------------------
    public async Task<int> UpsertAsync(Candle candle, CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE dbo.Candles AS tgt
            USING (SELECT
                    @SymbolId AS SymbolId, @Interval AS [Interval], @OpenTime AS OpenTime,
                    @CloseTime AS CloseTime,
                    @Open AS [Open], @High AS [High], @Low AS [Low], @Close AS [Close],
                    @Volume AS Volume, @QuoteVolume AS QuoteVolume,
                    @TradeCount AS TradeCount, @TakerBuyBase AS TakerBuyBase,
                    @IsClosed AS IsClosed) AS src
                ON tgt.SymbolId = src.SymbolId
               AND tgt.[Interval] = src.[Interval]
               AND tgt.OpenTime = src.OpenTime
            WHEN MATCHED THEN UPDATE SET
                CloseTime    = src.CloseTime,
                [Open]       = src.[Open],
                [High]       = src.[High],
                [Low]        = src.[Low],
                [Close]      = src.[Close],
                Volume       = src.Volume,
                QuoteVolume  = src.QuoteVolume,
                TradeCount   = src.TradeCount,
                TakerBuyBase = src.TakerBuyBase,
                IsClosed     = src.IsClosed
            WHEN NOT MATCHED BY TARGET THEN INSERT
                (SymbolId, [Interval], OpenTime, CloseTime,
                 [Open], [High], [Low], [Close],
                 Volume, QuoteVolume, TradeCount, TakerBuyBase, IsClosed)
                VALUES
                (src.SymbolId, src.[Interval], src.OpenTime, src.CloseTime,
                 src.[Open], src.[High], src.[Low], src.[Close],
                 src.Volume, src.QuoteVolume, src.TradeCount, src.TakerBuyBase, src.IsClosed);
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(sql, candle, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------
    // Bulk path: SqlBulkCopy → dbo.Candles_Stage (heap) → MERGE drain.
    // Idempotent by (SymbolId, Interval, OpenTime). Returns rows affected by MERGE.
    // ------------------------------------------------------------------------
    public async Task<int> BulkUpsertAsync(
        IReadOnlyCollection<Candle> candles,
        CancellationToken cancellationToken)
    {
        if (candles.Count == 0)
        {
            return 0;
        }

        await using var conn = (SqlConnection)await connectionFactory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Truncate stage to keep it scoped to this transaction's payload.
            await new SqlCommand("TRUNCATE TABLE dbo.Candles_Stage;", conn, tx)
                .ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = "dbo.Candles_Stage",
                BatchSize = BulkBatchSize,
                BulkCopyTimeout = 60,
                EnableStreaming = true,
            })
            {
                MapBulkColumns(bulk);
                using var reader = new CandleDataReader(candles);
                await bulk.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
            }

            const string mergeSql = """
                MERGE dbo.Candles WITH (HOLDLOCK) AS tgt
                USING dbo.Candles_Stage AS src
                  ON tgt.SymbolId = src.SymbolId
                 AND tgt.[Interval] = src.[Interval]
                 AND tgt.OpenTime = src.OpenTime
                WHEN MATCHED THEN UPDATE SET
                    CloseTime    = src.CloseTime,
                    [Open]       = src.[Open],
                    [High]       = src.[High],
                    [Low]        = src.[Low],
                    [Close]      = src.[Close],
                    Volume       = src.Volume,
                    QuoteVolume  = src.QuoteVolume,
                    TradeCount   = src.TradeCount,
                    TakerBuyBase = src.TakerBuyBase,
                    IsClosed     = src.IsClosed
                WHEN NOT MATCHED BY TARGET THEN INSERT
                    (SymbolId, [Interval], OpenTime, CloseTime,
                     [Open], [High], [Low], [Close],
                     Volume, QuoteVolume, TradeCount, TakerBuyBase, IsClosed)
                    VALUES
                    (src.SymbolId, src.[Interval], src.OpenTime, src.CloseTime,
                     src.[Open], src.[High], src.[Low], src.[Close],
                     src.Volume, src.QuoteVolume, src.TradeCount, src.TakerBuyBase, src.IsClosed);
            """;

            using var mergeCmd = new SqlCommand(mergeSql, conn, tx) { CommandTimeout = 120 };
            var affected = await mergeCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void MapBulkColumns(SqlBulkCopy bulk)
    {
        // Column order: explicit mapping makes us robust against later
        // reorderings of the staging table or DataReader.
        bulk.ColumnMappings.Add(nameof(Candle.SymbolId),     "SymbolId");
        bulk.ColumnMappings.Add(nameof(Candle.Interval),     "Interval");
        bulk.ColumnMappings.Add(nameof(Candle.OpenTime),     "OpenTime");
        bulk.ColumnMappings.Add(nameof(Candle.CloseTime),    "CloseTime");
        bulk.ColumnMappings.Add(nameof(Candle.Open),         "Open");
        bulk.ColumnMappings.Add(nameof(Candle.High),         "High");
        bulk.ColumnMappings.Add(nameof(Candle.Low),          "Low");
        bulk.ColumnMappings.Add(nameof(Candle.Close),        "Close");
        bulk.ColumnMappings.Add(nameof(Candle.Volume),       "Volume");
        bulk.ColumnMappings.Add(nameof(Candle.QuoteVolume),  "QuoteVolume");
        bulk.ColumnMappings.Add(nameof(Candle.TradeCount),   "TradeCount");
        bulk.ColumnMappings.Add(nameof(Candle.TakerBuyBase), "TakerBuyBase");
        bulk.ColumnMappings.Add(nameof(Candle.IsClosed),     "IsClosed");
    }

    public async Task<Candle?> GetAsync(
        int symbolId,
        string interval,
        DateTime openTime,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT SymbolId, [Interval], OpenTime, CloseTime,
                   [Open], [High], [Low], [Close],
                   Volume, QuoteVolume, TradeCount, TakerBuyBase, IsClosed, InsertedAt
            FROM   dbo.Candles
            WHERE  OpenTime = @OpenTime AND SymbolId = @SymbolId AND [Interval] = @Interval;
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<Candle>(
            new CommandDefinition(sql, new { SymbolId = symbolId, Interval = interval, OpenTime = openTime },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Candle>> GetRangeAsync(
        int symbolId,
        string interval,
        DateTime fromUtcInclusive,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken)
    {
        // OpenTime appears first in the WHERE clause to enable partition elimination.
        const string sql = """
            SELECT SymbolId, [Interval], OpenTime, CloseTime,
                   [Open], [High], [Low], [Close],
                   Volume, QuoteVolume, TradeCount, TakerBuyBase, IsClosed, InsertedAt
            FROM   dbo.Candles
            WHERE  OpenTime >= @From AND OpenTime < @To
              AND  SymbolId = @SymbolId AND [Interval] = @Interval
            ORDER BY OpenTime ASC;
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Candle>(
            new CommandDefinition(sql,
                new { SymbolId = symbolId, Interval = interval, From = fromUtcInclusive, To = toUtcExclusive },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<DateTime?> GetLatestOpenTimeAsync(int symbolId, string interval, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT MAX(OpenTime)
            FROM   dbo.Candles
            WHERE  SymbolId = @SymbolId AND [Interval] = @Interval;
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition(sql, new { SymbolId = symbolId, Interval = interval },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------
    // IDataReader over IReadOnlyCollection<Candle> — feeds SqlBulkCopy
    // without materialising an intermediate DataTable.
    // -----------------------------------------------------------------
    private sealed class CandleDataReader : IDataReader
    {
        private static readonly (string Name, Type Type)[] Columns =
        [
            (nameof(Candle.SymbolId),     typeof(int)),
            (nameof(Candle.Interval),     typeof(string)),
            (nameof(Candle.OpenTime),     typeof(DateTime)),
            (nameof(Candle.CloseTime),    typeof(DateTime)),
            (nameof(Candle.Open),         typeof(decimal)),
            (nameof(Candle.High),         typeof(decimal)),
            (nameof(Candle.Low),          typeof(decimal)),
            (nameof(Candle.Close),        typeof(decimal)),
            (nameof(Candle.Volume),       typeof(decimal)),
            (nameof(Candle.QuoteVolume),  typeof(decimal)),
            (nameof(Candle.TradeCount),   typeof(int)),
            (nameof(Candle.TakerBuyBase), typeof(decimal)),
            (nameof(Candle.IsClosed),     typeof(bool)),
        ];

        private readonly IEnumerator<Candle> _enumerator;
        private bool _closed;

        public CandleDataReader(IEnumerable<Candle> source)
        {
            _enumerator = source.GetEnumerator();
        }

        public int FieldCount => Columns.Length;

        public bool Read()
        {
            if (_closed) return false;
            return _enumerator.MoveNext();
        }

        public object GetValue(int i)
        {
            var c = _enumerator.Current;
            return i switch
            {
                0  => c.SymbolId,
                1  => c.Interval,
                2  => c.OpenTime,
                3  => c.CloseTime,
                4  => c.Open,
                5  => c.High,
                6  => c.Low,
                7  => c.Close,
                8  => c.Volume,
                9  => c.QuoteVolume,
                10 => c.TradeCount,
                11 => c.TakerBuyBase,
                12 => c.IsClosed,
                _  => throw new IndexOutOfRangeException(),
            };
        }

        public string GetName(int i) => Columns[i].Name;
        public int GetOrdinal(string name)
        {
            for (var i = 0; i < Columns.Length; i++)
                if (string.Equals(Columns[i].Name, name, StringComparison.Ordinal)) return i;
            throw new IndexOutOfRangeException(name);
        }
        public Type GetFieldType(int i) => Columns[i].Type;
        public string GetDataTypeName(int i) => Columns[i].Type.Name;

        public bool IsDBNull(int i) => false;

        // Members SqlBulkCopy doesn't actually exercise — minimal impl.
        public void Close() => _closed = true;
        public void Dispose() { _enumerator.Dispose(); _closed = true; }
        public DataTable? GetSchemaTable() => null;
        public bool NextResult() => false;
        public int Depth => 0;
        public bool IsClosed => _closed;
        public int RecordsAffected => -1;
        public object this[int i] => GetValue(i);
        public object this[string name] => GetValue(GetOrdinal(name));

        public bool     GetBoolean(int i)  => (bool)GetValue(i);
        public byte     GetByte(int i)     => (byte)GetValue(i);
        public char     GetChar(int i)     => (char)GetValue(i);
        public DateTime GetDateTime(int i) => (DateTime)GetValue(i);
        public decimal  GetDecimal(int i)  => (decimal)GetValue(i);
        public double   GetDouble(int i)   => (double)GetValue(i);
        public float    GetFloat(int i)    => (float)GetValue(i);
        public Guid     GetGuid(int i)     => (Guid)GetValue(i);
        public short    GetInt16(int i)    => (short)GetValue(i);
        public int      GetInt32(int i)    => (int)GetValue(i);
        public long     GetInt64(int i)    => (long)GetValue(i);
        public string   GetString(int i)   => (string)GetValue(i);
        public int      GetValues(object[] values)
        {
            var n = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < n; i++) values[i] = GetValue(i);
            return n;
        }
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();
        public long GetChars(int i, long fieldOffset, char[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
    }
}
