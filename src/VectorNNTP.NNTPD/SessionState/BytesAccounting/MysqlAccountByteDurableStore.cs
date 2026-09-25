using VectorNNTP.NNTPD.NntpDb;

namespace VectorNNTP.NNTPD.SessionState.BytesAccounting;

/// <summary>Durable remaining-quota store over the existing NntpDB pool.</summary>
internal sealed class MysqlAccountByteDurableStore : IAccountByteDurableStore
{
    private readonly NntpDbService _nntpDb;

    /// <summary>Initializes a new instance of the <see cref="MysqlAccountByteDurableStore"/> class.</summary>
    public MysqlAccountByteDurableStore(NntpDbService nntpDb)
    {
        ArgumentNullException.ThrowIfNull(nntpDb);
        _nntpDb = nntpDb;
    }

    /// <inheritdoc />
    public async ValueTask<AccountByteConsumeResult> ConsumeAsync(
        string accountName,
        long bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        try
        {
            await using var connection = await _nntpDb.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await connection.ConsumeAccountBytesAsync(accountName, bytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NntpDbUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NntpDbUnavailableException("MySQL account byte-quota consume failed.", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask<AccountByteConsumeResult> QueryRemainingAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        try
        {
            await using var connection = await _nntpDb.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await connection.QueryAccountByteRemainingAsync(accountName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NntpDbUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NntpDbUnavailableException("MySQL account byte-quota query failed.", ex);
        }
    }
}
