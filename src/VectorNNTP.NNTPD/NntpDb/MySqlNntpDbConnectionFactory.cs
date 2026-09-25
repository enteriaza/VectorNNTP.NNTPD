using MySqlConnector;

namespace VectorNNTP.NNTPD.NntpDb;

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
    public async Task<INntpDbConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = CreateConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new MySqlNntpDbConnection(connection);
        }
        catch (MySqlException ex) when (IsAuthenticationFailure(ex))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new NntpDbAuthenticationException("MySQL authentication failed.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbAuthenticationException)
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
