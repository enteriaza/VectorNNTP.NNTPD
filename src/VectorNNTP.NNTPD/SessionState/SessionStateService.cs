using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.SessionState;

/// <summary>
/// Periodic distributed session-state maintenance for locally authenticated NNTP sessions.
/// </summary>
/// <remarks>
/// This service is a scheduler. It does not accept connections, authenticate clients,
/// or keep its own session registry. Every renewal interval it asks
/// <see cref="ISessionStateLeaseManager"/> to renew currently active local ownership.
/// Each account with distributed ownership is one Redis RENEW EVAL covering session
/// count and every locally owned source IP. Renewal is node liveness, not client
/// activity: idle authenticated sessions continue to consume cluster capacity while
/// this process can renew. <see cref="StopAsync"/> stops the loop and then releases
/// leftover incarnation ownership. Listeners are registered after this service so
/// they stop first and sessions can finalize before renewal ends. Process crash is
/// detected by lease expiry after renewals stop.
/// </remarks>
public sealed class SessionStateService : IApplicationService
{
    private readonly ISessionStateLeaseManager _leases;
    private readonly ILogger<SessionStateService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _runCts = new();
    private Task? _execution;
    private int _started;

    /// <summary>Initializes a new instance of the <see cref="SessionStateService"/> class.</summary>
    public SessionStateService(
        ISessionStateLeaseManager leases,
        ILogger<SessionStateService> logger)
        : this(leases, logger, TimeProvider.System, SessionStateDefaults.RenewalPeriod)
    {
    }

    /// <summary>Initializes a new instance with an explicit interval (tests).</summary>
    internal SessionStateService(
        ISessionStateLeaseManager leases,
        ILogger<SessionStateService> logger,
        TimeProvider timeProvider,
        TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _leases = leases;
        _logger = logger;
        _timeProvider = timeProvider;
        _interval = interval;
    }

    /// <inheritdoc />
    public string Name => "SessionState";

    /// <inheritdoc />
    public Task? Execution => _execution;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _execution = Task.Run(() => RunAsync(_runCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _runCts.CancelAsync().ConfigureAwait(false);
        var execution = _execution;
        if (execution is not null)
        {
            try
            {
                await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        try
        {
            await _leases.ReleaseAllOwnershipAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        SessionStateLogMessages.SessionStateRenewalStarted(_logger, _interval);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_interval, _timeProvider, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _leases.RenewLeasesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SessionStateLogMessages.SessionStateRenewalPassFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        SessionStateLogMessages.SessionStateRenewalStopped(_logger);
    }
}
