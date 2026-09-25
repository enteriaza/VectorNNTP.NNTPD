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
    public async ValueTask<IReadOnlyList<NntpGroupRow>> QueryNewsgroupsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = NntpGroupQueries.SelectNewsgroups;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<NntpGroupRow>();
            var index = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadGroupRow(reader, index));
                index++;
            }

            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            and not NntpDbUnavailableException
            and not Newsgroups.NewsgroupCatalogueException)
        {
            throw new NntpDbUnavailableException("MySQL newsgroup catalogue query failed.", ex);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>Reads one <c>nntpgroups</c> row, preserving unsigned integer width.</summary>
    internal static NntpGroupRow ReadGroupRow(MySqlDataReader reader, int index)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsDBNull(0))
        {
            throw new Newsgroups.NewsgroupCatalogueException($"nntpgroups row {index} has a NULL group_name.");
        }

        var name = reader.GetString(0);
        var description = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var high = ConvertUnsignedCount(reader.GetValue(2), "count_high", name);
        var low = ConvertUnsignedCount(reader.GetValue(3), "count_low", name);
        var status = ConvertPostingStatus(reader.GetValue(4), name);
        return new NntpGroupRow(name, description, high, low, status);
    }

    /// <summary>
    /// Converts a MySQL unsigned/signed integer column to <see cref="ulong"/> without
    /// narrowing or floating-point conversion.
    /// </summary>
    internal static ulong ConvertUnsignedCount(object? value, string column, string groupName) =>
        value switch
        {
            ulong unsigned64 => unsigned64,
            uint unsigned32 => unsigned32,
            ushort unsigned16 => unsigned16,
            byte unsigned8 => unsigned8,
            long signed64 when signed64 >= 0 => (ulong)signed64,
            int signed32 when signed32 >= 0 => (ulong)signed32,
            short signed16 when signed16 >= 0 => (ulong)signed16,
            sbyte signed8 when signed8 >= 0 => (ulong)signed8,
            _ => throw new Newsgroups.NewsgroupCatalogueException(
                $"nntpgroups row '{groupName}' column {column} is not a non-negative integer."),
        };

    /// <summary>
    /// Reads posting_status as a single supported octet.
    /// MySQL ENUM/CHAR typically arrives as a one-character string from MySqlConnector;
    /// byte/sbyte/char are also accepted. Values other than <c>y</c>/<c>n</c>/<c>m</c>/<c>x</c>/<c>j</c>
    /// are rejected, including RFC 6048 <c>=&lt;newsgroup&gt;</c>.
    /// </summary>
    internal static byte ConvertPostingStatus(object? value, string groupName)
    {
        byte status;
        switch (value)
        {
            case byte b:
                status = b;
                break;
            case sbyte sb:
                status = (byte)sb;
                break;
            case char ch:
                status = (byte)ch;
                break;
            case string text when text.Length == 1:
                status = (byte)text[0];
                break;
            case string text when text.Length > 0 && text[0] == '=':
                throw new Newsgroups.NewsgroupCatalogueException(
                    $"nntpgroups row '{groupName}' uses unsupported RFC 6048 =<newsgroup> posting_status.");
            default:
                throw new Newsgroups.NewsgroupCatalogueException(
                    $"nntpgroups row '{groupName}' has an unreadable posting_status.");
        }

        if (!Newsgroups.NewsgroupPostingStatusOctets.IsSupported(status))
        {
            var reason = status == (byte)'='
                ? $"nntpgroups row '{groupName}' uses unsupported RFC 6048 =<newsgroup> posting_status."
                : $"nntpgroups row '{groupName}' has invalid posting_status 0x{status:X2}.";
            throw new Newsgroups.NewsgroupCatalogueException(reason);
        }

        return status;
    }

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
