using MySqlConnector;
using VectorNNTP.NNTPD.Authentication;
using VectorNNTP.NNTPD.Moderation;

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
    public async ValueTask<NntpUserRecord?> QueryUserAccountAsync(
        string accountName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = NntpUserQueries.SelectUserByName;
            command.Parameters.AddWithValue("@account_name", accountName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return MapUserRecord(reader, accountName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
        {
            throw new NntpDbUnavailableException("MySQL nntpusers lookup failed.", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<NntpModeratorRow>> QueryEnabledModeratorsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = NntpModeratorQueries.SelectEnabledModerators;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<NntpModeratorRow>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(MapModeratorRow(reader));
            }

            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
        {
            throw new NntpDbUnavailableException("MySQL nntpmoderators catalogue query failed.", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask<AccountByteConsumeResult> ConsumeAccountBytesAsync(
        string accountName,
        long bytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        try
        {
            await using var transaction = await _connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var locked = await ReadByteQuotaAsync(
                        NntpUserQueries.SelectByteQuotaForUpdate,
                        accountName,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (locked is null)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new AccountByteConsumeResult(AccountByteConsumeStatus.AccountNotFound, 0, 0);
                }

                if (!locked.Value.IsByteAccount)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new AccountByteConsumeResult(AccountByteConsumeStatus.NotByteAccount, 0, 0);
                }

                var current = ClampNonNegative(locked.Value.Remaining);
                var consumed = bytes > current ? current : bytes;
                await using (var update = _connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = NntpUserQueries.ConsumeAccountBytes;
                    update.Parameters.AddWithValue("@account_name", accountName);
                    update.Parameters.AddWithValue("@bytes", bytes);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var after = await ReadByteQuotaAsync(
                        NntpUserQueries.SelectAccountByteRemaining,
                        accountName,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                var remaining = after is { IsByteAccount: true } row
                    ? ClampNonNegative(row.Remaining)
                    : 0L;
                return new AccountByteConsumeResult(AccountByteConsumeStatus.Consumed, remaining, consumed);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
        {
            throw new NntpDbUnavailableException("MySQL account byte-quota consume failed.", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask<AccountByteConsumeResult> QueryAccountByteRemainingAsync(
        string accountName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        try
        {
            var row = await ReadByteQuotaAsync(
                    NntpUserQueries.SelectAccountByteRemaining,
                    accountName,
                    transaction: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                return new AccountByteConsumeResult(AccountByteConsumeStatus.AccountNotFound, 0, 0);
            }

            if (!row.Value.IsByteAccount)
            {
                return new AccountByteConsumeResult(AccountByteConsumeStatus.NotByteAccount, 0, 0);
            }

            return new AccountByteConsumeResult(
                AccountByteConsumeStatus.Consumed,
                ClampNonNegative(row.Value.Remaining),
                0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
        {
            throw new NntpDbUnavailableException("MySQL account byte-quota query failed.", ex);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>Maps one enabled <c>nntpmoderators</c> row. CHAR columns are trimmed.</summary>
    internal static NntpModeratorRow MapModeratorRow(MySqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var id = Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
        var pattern = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
        var address = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
        var account = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
        return new NntpModeratorRow(id, pattern, address, account);
    }

    /// <summary>Maps one <c>nntpusers</c> row. Flag columns are true only for <c>Y</c>.</summary>
    internal static NntpUserRecord MapUserRecord(MySqlDataReader reader, string accountName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var password = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var scramSalt = ReadBinaryColumn(reader, 1);
        var scramIterations = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
        var scramStoredKey = ReadBinaryColumn(reader, 3);
        var scramServerKey = ReadBinaryColumn(reader, 4);
        var allowAuthPlain = IsYesFlag(reader, 5);
        var allowAuthScram256 = IsYesFlag(reader, 6);
        var accountType = ReadAccountType(reader, 7);
        var rateLimit = reader.IsDBNull(8) ? 0 : Convert.ToInt32(reader.GetValue(8));
        var byteLimit = reader.IsDBNull(9) ? 0L : ConvertByteLimit(reader.GetValue(9));
        var sessionLimit = reader.IsDBNull(10) ? 0 : Convert.ToInt32(reader.GetValue(10));
        var srcIpLimit = reader.IsDBNull(11) ? 0 : Convert.ToInt32(reader.GetValue(11));
        var isEnabled = IsYesFlag(reader, 12);
        var customerId = ReadCustomerId(reader, 13);
        return new NntpUserRecord(
            accountName,
            password,
            allowAuthPlain,
            allowAuthScram256,
            scramSalt,
            scramIterations,
            scramStoredKey,
            scramServerKey,
            accountType,
            rateLimit,
            byteLimit,
            sessionLimit,
            srcIpLimit,
            isEnabled,
            customerId);
    }

    private async ValueTask<ByteQuotaRow?> ReadByteQuotaAsync(
        string commandText,
        string accountName,
        MySqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.Parameters.AddWithValue("@account_name", accountName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var accountType = ReadAccountType(reader, 0);
        var remaining = reader.IsDBNull(1) ? 0L : ConvertByteLimit(reader.GetValue(1));
        return new ByteQuotaRow(IsByteAccountType(accountType), remaining);
    }

    /// <summary>
    /// Converts a MySQL integer <c>account_byte_limit</c> to a non-negative <see cref="long"/>.
    /// Unsigned values above <see cref="long.MaxValue"/> saturate rather than wrap.
    /// Negative signed values become <c>0</c> (exhausted / never allowed).
    /// </summary>
    internal static long ConvertByteLimit(object? value) =>
        value switch
        {
            null or DBNull => 0,
            long signed64 => ClampNonNegative(signed64),
            ulong unsigned64 => unsigned64 > long.MaxValue ? long.MaxValue : (long)unsigned64,
            int signed32 => ClampNonNegative(signed32),
            uint unsigned32 => unsigned32,
            short signed16 => ClampNonNegative(signed16),
            ushort unsigned16 => unsigned16,
            sbyte signed8 => ClampNonNegative(signed8),
            byte unsigned8 => unsigned8,
            decimal dec when dec < 0 => 0,
            decimal dec when dec > long.MaxValue => long.MaxValue,
            decimal dec => (long)dec,
            _ => ClampNonNegative(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
        };

    private static long ClampNonNegative(long value) => value < 0 ? 0 : value;

    private static bool IsByteAccountType(char accountType) => accountType is 'B' or 'b';

    private readonly record struct ByteQuotaRow(bool IsByteAccount, long Remaining);

    private static bool IsYesFlag(MySqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        return string.Equals(Convert.ToString(reader.GetValue(ordinal)), "Y", StringComparison.OrdinalIgnoreCase);
    }

    private static char ReadAccountType(MySqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 'R';
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            char ch => ch,
            string text when text.Length > 0 => text[0],
            byte b => (char)b,
            _ => 'R',
        };
    }

    private static string ReadCustomerId(MySqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return string.Empty;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid guid => guid.ToString("D"),
            string text => text,
            _ => Convert.ToString(value) ?? string.Empty,
        };
    }

    private static ReadOnlyMemory<byte> ReadBinaryColumn(MySqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            byte[] bytes when bytes.Length == 0 => ReadOnlyMemory<byte>.Empty,
            byte[] bytes => bytes,
            ReadOnlyMemory<byte> memory => memory,
            _ => ReadOnlyMemory<byte>.Empty,
        };
    }

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
