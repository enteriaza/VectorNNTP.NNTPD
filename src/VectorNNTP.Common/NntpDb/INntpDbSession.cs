using MySqlConnector;

namespace VectorNNTP.Common.NntpDb
{
    /// <summary>
    /// One open logical MySQL connection. Dispose returns the physical connection to MySqlConnector's pool.
    /// </summary>
    /// <remarks>
    /// This is the shared connection surface. Application query methods stay in the owning application.
    /// </remarks>
    public interface INntpDbSession : IAsyncDisposable
    {
        /// <summary>
        /// Executes <c>SELECT 1</c> and returns the scalar integer result.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel the query.</param>
        /// <returns>The integer result of <c>SELECT 1</c>.</returns>
        ValueTask<int> SelectOneAsync(CancellationToken cancellationToken);

        /// <summary>Creates a command on this open connection.</summary>
        /// <returns>A command the caller must dispose.</returns>
        MySqlCommand CreateCommand();

        /// <summary>Begins a transaction on this open connection.</summary>
        /// <param name="cancellationToken">Token used to cancel the begin.</param>
        /// <returns>The open transaction.</returns>
        ValueTask<MySqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

        /// <summary>Begins a transaction at <paramref name="isolationLevel"/>.</summary>
        /// <param name="isolationLevel">Transaction isolation.</param>
        /// <param name="cancellationToken">Token used to cancel the begin.</param>
        /// <returns>The open transaction.</returns>
        ValueTask<MySqlTransaction> BeginTransactionAsync(
            System.Data.IsolationLevel isolationLevel,
            CancellationToken cancellationToken);
    }
}
