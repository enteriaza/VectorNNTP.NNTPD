using VectorNNTP.NNTPD.NntpDb;

namespace VectorNNTP.NNTPD.PostFilter;

/// <summary>Loads the published PostFilter revision through the existing NntpDB connection pool.</summary>
public sealed class MySqlPostFilterPolicyRepository : IPostFilterPolicyRepository
{
    private readonly NntpDbService _nntpDb;
    private readonly ILogger<MySqlPostFilterPolicyRepository> _logger;

    /// <summary>Initializes a new instance of the <see cref="MySqlPostFilterPolicyRepository"/> class.</summary>
    public MySqlPostFilterPolicyRepository(
        NntpDbService nntpDb,
        ILogger<MySqlPostFilterPolicyRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(nntpDb);
        ArgumentNullException.ThrowIfNull(logger);
        _nntpDb = nntpDb;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<PostFilterPolicyRecord> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _nntpDb.OpenAsync(cancellationToken).ConfigureAwait(false);
            var record = await connection.QueryPostFilterPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                throw new InvalidOperationException(
                    "nntppostfiltercurrent is missing policy_id = 1. NNTPD will not invent a local PostFilter policy.");
            }

            return record;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (NntpDbUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PostFilterLogMessages.PolicyRepositoryFailed(_logger, ex);
            throw new NntpDbUnavailableException("MySQL nntppostfilterpolicy lookup failed.", ex);
        }
    }
}
