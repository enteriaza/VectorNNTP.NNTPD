namespace VectorNNTP.NNTPD.NntpDb;

/// <summary>
/// One logical MySQL connection. Dispose returns the physical connection to MySqlConnector's pool.
/// </summary>
public interface INntpDbConnection : IAsyncDisposable
{
    /// <summary>
    /// Executes <c>SELECT 1</c> and returns the scalar integer result.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the query.</param>
    /// <returns>The integer result of <c>SELECT 1</c>.</returns>
    ValueTask<int> SelectOneAsync(CancellationToken cancellationToken);
}
