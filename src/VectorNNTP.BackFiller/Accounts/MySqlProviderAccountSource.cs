using MySqlConnector;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Accounts;

/// <summary>
/// Reads <c>nntpbackfilleraccounts</c> for the configured server id. Opens a connection only while querying.
/// </summary>
public sealed class MySqlProviderAccountSource : IProviderAccountSource
{
    /// <summary>Authoritative table name from the old BackFiller.</summary>
    public const string AccountsTableName = "nntpbackfilleraccounts";

    /// <summary>Parameterized query used by the old worker, filtered by <c>serverid</c>.</summary>
    public const string AccountsQuery =
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

    private readonly string _connectionString;
    private readonly byte _serverId;
    private readonly int _commandTimeoutSeconds;

    /// <summary>Initializes a MySQL-backed source from validated runtime options.</summary>
    public MySqlProviderAccountSource(BackFillerRuntimeOptions runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ServerId is < byte.MinValue or > byte.MaxValue)
        {
            throw new InvalidOperationException("BackFiller:ServerId must fit in an unsigned byte for nntpbackfilleraccounts.");
        }

        _connectionString = runtime.GrabberDb.ConnectionString;
        _serverId = (byte)runtime.ServerId;
        _commandTimeoutSeconds = (int)runtime.Accounts.CommandTimeout.TotalSeconds;
    }

    /// <inheritdoc />
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
                    command.CommandTimeout = _commandTimeoutSeconds;
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
            throw new InvalidOperationException("Provider account query failed against GrabberDB.", ex);
        }
    }

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
    internal static Guid ParseEntryId(object raw) =>
        raw switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => throw new InvalidOperationException("nntpbackfilleraccounts.entryid is not a GUID."),
        };

    /// <summary>Parses <c>keepalive</c> into the stored tinyint representation.</summary>
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
