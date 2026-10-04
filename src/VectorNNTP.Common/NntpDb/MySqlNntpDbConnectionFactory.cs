using MySqlConnector;

namespace VectorNNTP.Common.NntpDb
{
    /// <summary>
    /// Opens logical MySQL connections from the configured NntpDB connection string.
    /// </summary>
    /// <remarks>
    /// Provider pooling is controlled only by <c>ConnectionStrings:NntpDB</c>.
    /// This factory does not rewrite that string.
    /// </remarks>
    public sealed class MySqlNntpDbConnectionFactory : INntpDbConnectionFactory
    {
        /// <inheritdoc />
        public async Task<INntpDbSession> OpenAsync(string connectionString, CancellationToken cancellationToken)
        {
            var connection = CreateConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return new MySqlNntpDbSession(connection);
            }
            catch (MySqlException ex) when (IsAuthenticationFailure(ex))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new NntpDbAuthenticationException("MySQL authentication failed.", ex);
            }
            catch (ArgumentException ex)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new NntpDbConfigurationException(ex.Message, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                and not NntpDbAuthenticationException
                and not NntpDbConfigurationException)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new NntpDbUnavailableException("MySQL connection failed.", ex);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Creates an unopened provider connection from the configured string without rewriting it.
        /// </summary>
        /// <param name="connectionString">Configured connection string.</param>
        /// <returns>An unopened <see cref="MySqlConnection"/>.</returns>
        internal static MySqlConnection CreateConnection(string connectionString)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            return new MySqlConnection(connectionString);
        }

        private static bool IsAuthenticationFailure(MySqlException exception) =>
            exception.ErrorCode is MySqlErrorCode.AccessDenied
                or (MySqlErrorCode)1044
                or (MySqlErrorCode)1045
                or (MySqlErrorCode)1698
                or (MySqlErrorCode)1862;
    }
}
