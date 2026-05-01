using System.Data.Common;

namespace TradingBot.Data.Connection;

public interface IDbConnectionFactory
{
    DbConnection Create();

    Task<DbConnection> OpenAsync(CancellationToken cancellationToken);

    string ConnectionString { get; }
}
