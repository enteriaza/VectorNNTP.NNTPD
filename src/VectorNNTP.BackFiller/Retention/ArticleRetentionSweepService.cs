namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Periodic TTL sweep. Isolated maintenance; not a generic lifecycle framework.
/// </summary>
internal sealed class ArticleRetentionSweepService : BackgroundService
{
    /// <summary>Authority swept by <see cref="ExecuteAsync"/> and closed by <see cref="StopAsync"/>.</summary>
    private readonly IArticleRetentionAuthority _authority;

    /// <summary>Logger for <see cref="ArticleRetentionSweepLogMessages.SweepFailed"/>. Not the authority's retention logger.</summary>
    private readonly ILogger<ArticleRetentionSweepService> _logger;

    /// <summary>
    /// 1 after <see cref="SweepOnce"/> passes its reentrancy check and until that call's finally block.
    /// 0 otherwise. A nested call returns without sweeping.
    /// </summary>
    private int _sweeping;

    /// <summary>Initializes the sweep hosted service.</summary>
    /// <param name="authority">
    /// Retention authority. This service calls <see cref="IArticleRetentionAuthority.SweepExpired"/>
    /// and <see cref="IArticleRetentionAuthority.BeginShutdown"/> and does not dispose it.
    /// </param>
    /// <param name="logger">Logger used only when a sweep throws.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authority"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    internal ArticleRetentionSweepService(
        IArticleRetentionAuthority authority,
        ILogger<ArticleRetentionSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(logger);
        _authority = authority;
        _logger = logger;
    }

    /// <summary>
    /// Sweeps immediately, then waits <see cref="IArticleRetentionAuthority.SweepInterval"/> and repeats
    /// until <paramref name="stoppingToken"/> is canceled.
    /// </summary>
    /// <param name="stoppingToken">
    /// Host stop token. Cancels the delay between sweeps. Not passed into
    /// <see cref="IArticleRetentionAuthority.SweepExpired"/>.
    /// </param>
    /// <returns>A task that completes when the delay observes host cancellation.</returns>
    /// <remarks>
    /// <see cref="OperationCanceledException"/> from the delay is ignored when
    /// <paramref name="stoppingToken"/> is already canceled. An exception from
    /// <see cref="SweepOnce"/> is logged and rethrown, which faults this hosted service.
    /// </remarks>
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

    /// <summary>Closes retention admission, then stops the background loop.</summary>
    /// <param name="cancellationToken">
    /// Forwarded only to <see cref="BackgroundService.StopAsync(CancellationToken)"/>.
    /// It does not cancel <see cref="IArticleRetentionAuthority.BeginShutdown"/>.
    /// </param>
    /// <returns>A task that completes when the base stop finishes.</returns>
    /// <remarks>
    /// Calls <see cref="IArticleRetentionAuthority.BeginShutdown"/> before the base stop.
    /// Does not dispose the authority and does not by itself release retained entries.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _authority.BeginShutdown();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one <see cref="IArticleRetentionAuthority.SweepExpired"/> unless this method is already in that call.
    /// </summary>
    /// <remarks>
    /// Sets <see cref="_sweeping"/> for the call and clears it in a finally block, including when
    /// <see cref="IArticleRetentionAuthority.SweepExpired"/> throws. The caught exception is logged
    /// with <see cref="ArticleRetentionSweepLogMessages.SweepFailed"/> and then rethrown.
    /// </remarks>
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
    /// <summary>
    /// Writes event 5503 when <see cref="ArticleRetentionSweepService"/> catches a failure from
    /// <see cref="IArticleRetentionAuthority.SweepExpired"/>. The service rethrows that failure after this event.
    /// </summary>
    /// <param name="logger">Logger that receives the error.</param>
    /// <param name="exception">The sweep failure. Logged by this method; not thrown by this method.</param>
    [LoggerMessage(
        EventId = 5503,
        Level = LogLevel.Error,
        Message = "Article retention sweep failed.")]
    internal static partial void SweepFailed(ILogger logger, Exception exception);
}
