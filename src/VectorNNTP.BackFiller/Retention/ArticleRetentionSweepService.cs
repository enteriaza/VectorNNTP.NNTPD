namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Periodic TTL sweep. Isolated maintenance; not a generic lifecycle framework.
/// </summary>
public sealed class ArticleRetentionSweepService : BackgroundService
{
    private readonly IArticleRetentionAuthority _authority;
    private readonly ILogger<ArticleRetentionSweepService> _logger;
    private int _sweeping;

    /// <summary>Initializes the sweep hosted service.</summary>
    public ArticleRetentionSweepService(
        IArticleRetentionAuthority authority,
        ILogger<ArticleRetentionSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(logger);
        _authority = authority;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SweepOnce();
            try
            {
                await Task.Delay(_authority.SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _authority.BeginShutdown();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SweepOnce()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
        {
            return;
        }

        try
        {
            _ = _authority.SweepExpired();
        }
        catch (Exception ex)
        {
            ArticleRetentionSweepLogMessages.SweepFailed(_logger, ex);
            throw;
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }
}

/// <summary>Sweep failure log. Unexpected exceptions are not swallowed.</summary>
internal static partial class ArticleRetentionSweepLogMessages
{
    [LoggerMessage(
        EventId = 5503,
        Level = LogLevel.Error,
        Message = "Article retention sweep failed.")]
    public static partial void SweepFailed(ILogger logger, Exception exception);
}
