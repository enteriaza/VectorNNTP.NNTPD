using MySqlConnector;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Accounts;

/// <summary>
/// Reads <c>nntpbackfilleraccounts</c> for the configured server id. Opens a connection only while querying.
/// </summary>
internal sealed class MySqlProviderAccountSource : IProviderAccountSource
{
    /// <summary>Authoritative table name from the old BackFiller.</summary>
    internal const string AccountsTableName = "nntpbackfilleraccounts";

    /// <summary>Parameterized query used by the old worker, filtered by <c>serverid</c>.</summary>
    internal const string AccountsQuery =
        "SELECT " +
        "entryid, " +
        "backbone, " +
        "hostname, " +
        "keepalive, " +
        "maxconnections, " +
        "password, " +
        "port, " +
        "serverid, " +
        "username, " +
        "usessl " +
        "FROM nntpbackfilleraccounts " +
        "WHERE serverid = @ServerId;";

    /// <summary>NntpDB connection string copied from runtime options at construction. Secret. Not mutated.</summary>
    private readonly string _connectionString;

    /// <summary>Server id copied at construction and bound to <c>@ServerId</c> as an unsigned byte.</summary>
    private readonly byte _serverId;

    /// <summary>Copies the NntpDB connection string and server id from runtime options.</summary>
    /// <param name="runtime">Runtime snapshot. Only <see cref="BackFillerRuntimeOptions.NntpDb"/> and <see cref="BackFillerRuntimeOptions.ServerId"/> are read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><see cref="BackFillerRuntimeOptions.ServerId"/> is outside 0–255.</exception>
    public MySqlProviderAccountSource(BackFillerRuntimeOptions runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ServerId is < byte.MinValue or > byte.MaxValue)
        {
            throw new InvalidOperationException("BackFiller:ServerId must fit in an unsigned byte for nntpbackfilleraccounts.");
        }

        _connectionString = runtime.NntpDb.ConnectionString;
        _serverId = (byte)runtime.ServerId;
    }

    /// <summary>
    /// Opens one connection, runs <see cref="AccountsQuery"/> for <see cref="_serverId"/>, and returns every row.
    /// </summary>
    /// <param name="cancellationToken">Passed to open, execute, and row reads. Cancellation is not wrapped.</param>
    /// <returns>Rows in reader order. Backbone and session rules are not applied here.</returns>
    /// <exception cref="OperationCanceledException">Open, execute, or read threw <see cref="OperationCanceledException"/>. It is rethrown, not wrapped.</exception>
    /// <exception cref="InvalidOperationException">
    /// The server threw <see cref="MySqlException"/> (it is the inner exception), or <c>entryid</c> or <c>keepalive</c> parsing failed.
    /// </exception>
    /// <exception cref="OverflowException"><c>keepalive</c> is an integer outside 0–255.</exception>
    /// <remarks>
    /// The connection, command, and reader are disposed before this method returns.
    /// The instance keeps no per-query state, so overlapping calls each open a connection.
    /// Reader failures other than <see cref="MySqlException"/> propagate unwrapped.
    /// </remarks>
    public async Task<IReadOnlyList<ProviderAccountRow>> QueryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = new MySqlConnection(_connectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = AccountsQuery;
                    _ = command.Parameters.Add(new MySqlParameter("@ServerId", MySqlDbType.UByte) { Value = _serverId });
                    var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        return await ReadRowsAsync(reader, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MySqlException ex)
        {
            throw new InvalidOperationException("Provider account query failed against NntpDB.", ex);
        }
    }

    /// <summary>Materializes every remaining row. Column ordinals are resolved once before the first read.</summary>
    /// <param name="reader">Open reader positioned before the first row. Not disposed here.</param>
    /// <param name="cancellationToken">Observed between rows by <see cref="MySqlDataReader.ReadAsync(CancellationToken)"/>.</param>
    /// <returns>The rows read. Empty when the reader has no rows.</returns>
    private static async Task<IReadOnlyList<ProviderAccountRow>> ReadRowsAsync(
        MySqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var rows = new List<ProviderAccountRow>();
        var entryIdOrdinal = reader.GetOrdinal("entryid");
        var backboneOrdinal = reader.GetOrdinal("backbone");
        var hostnameOrdinal = reader.GetOrdinal("hostname");
        var keepAliveOrdinal = reader.GetOrdinal("keepalive");
        var maxConnectionsOrdinal = reader.GetOrdinal("maxconnections");
        var passwordOrdinal = reader.GetOrdinal("password");
        var portOrdinal = reader.GetOrdinal("port");
        var serverIdOrdinal = reader.GetOrdinal("serverid");
        var usernameOrdinal = reader.GetOrdinal("username");
        var useSslOrdinal = reader.GetOrdinal("usessl");

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ProviderAccountRow(
                ParseEntryId(reader.GetValue(entryIdOrdinal)),
                reader.GetString(backboneOrdinal),
                reader.GetString(hostnameOrdinal),
                ParseKeepAlive(reader.GetValue(keepAliveOrdinal)),
                reader.GetInt32(maxConnectionsOrdinal),
                reader.GetString(usernameOrdinal),
                reader.GetString(passwordOrdinal),
                reader.GetInt32(portOrdinal),
                reader.GetInt32(serverIdOrdinal),
                reader.GetString(useSslOrdinal)));
        }

        return rows;
    }

    /// <summary>Parses <c>entryid</c> from a GUID or GUID string.</summary>
    /// <param name="raw">Value returned by the reader for <c>entryid</c>.</param>
    /// <returns>The GUID value, or the parsed GUID text.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="raw"/> is not a <see cref="Guid"/> or a parseable GUID string.</exception>
    internal static Guid ParseEntryId(object raw) =>
        raw switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => throw new InvalidOperationException("nntpbackfilleraccounts.entryid is not a GUID."),
        };

    /// <summary>Parses <c>keepalive</c> into the stored tinyint representation.</summary>
    /// <param name="raw">Value returned by the reader for <c>keepalive</c>.</param>
    /// <returns>The integer value as a byte.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="raw"/> is null or not one of the accepted integer types.</exception>
    /// <exception cref="OverflowException">The integer is outside 0–255. The conversion is checked.</exception>
    /// <remarks>Accepted types are <see cref="byte"/>, <see cref="sbyte"/>, <see cref="short"/>, <see cref="ushort"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>, and <see cref="ulong"/>.</remarks>
    internal static byte ParseKeepAlive(object raw) =>
        raw switch
        {
            byte value => value,
            sbyte value => checked((byte)value),
            short value => checked((byte)value),
            ushort value => checked((byte)value),
            int value => checked((byte)value),
            uint value => checked((byte)value),
            long value => checked((byte)value),
            ulong value => checked((byte)value),
            _ => throw new InvalidOperationException("nntpbackfilleraccounts.keepalive is not a non-null integer."),
        };
}
