using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace TradingBot.Data.Connection;

public sealed class SqlConnectionFactory : IDbConnectionFactory
{
    public SqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "SQL Server connection string is missing. Configure 'Database:ConnectionString'.",
                nameof(connectionString));
        }

        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public DbConnection Create() => new SqlConnection(ConnectionString);

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
