using MySqlConnector;

namespace VectorNNTP.NNTPD.NntpDb;

/// <summary>MySqlConnector-backed logical connection. Dispose returns it to the provider pool.</summary>
internal sealed class MySqlNntpDbConnection : INntpDbConnection
{
    private readonly MySqlConnection _connection;

    /// <summary>Initializes a new instance wrapping an already-open connection.</summary>
    public MySqlNntpDbConnection(MySqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <inheritdoc />
    public async ValueTask<int> SelectOneAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT 1";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return ConvertSelectOneScalar(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
        {
            throw new NntpDbUnavailableException("MySQL health query SELECT 1 failed.", ex);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>
    /// Converts a MySQL <c>SELECT 1</c> scalar, including the connector's <see cref="long"/> result type.
    /// </summary>
    internal static int ConvertSelectOneScalar(object? result) =>
        result switch
        {
            int value => value,
            long longValue => checked((int)longValue),
            _ => throw new NntpDbUnavailableException("MySQL health query SELECT 1 returned an unexpected result."),
        };
}
