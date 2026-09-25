namespace VectorNNTP.NNTPD.NntpDb;

/// <summary>Opens a logical MySQL connection using MySqlConnector's native pool.</summary>
public interface INntpDbConnectionFactory
{
    /// <summary>
    /// Opens one logical connection. Dispose returns the physical connection to the provider pool.
    /// </summary>
    /// <param name="connectionString">Configured <c>ConnectionStrings:NntpDB</c> value.</param>
    /// <param name="cancellationToken">Token used to cancel the open.</param>
    /// <returns>An open logical connection.</returns>
    Task<INntpDbConnection> OpenAsync(string connectionString, CancellationToken cancellationToken);
}
