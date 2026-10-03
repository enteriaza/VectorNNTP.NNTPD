using System.Diagnostics;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Core;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Periodically invokes <see cref="StorageMaintenanceCoordinator.RunOnceAsync"/> (Phase 5C).
/// </summary>
/// <remarks>
/// <para>
/// Owns scheduling, cancellation, and recoverable error handling only. Policy, compaction,
/// retirement, and reclamation remain in the coordinator and storage primitives.
/// </para>
/// <para>
/// Must be registered after <see cref="StorageEngineApplicationService"/> so Open+Recover
/// completes before the first coordinator resolve. Interval is a delay after each run completes
/// (no overlapping runs, no backlog). The worker always starts. The first run is immediate.
/// Compaction is always part of each cycle.
/// </para>
/// </remarks>
public sealed class StorageMaintenanceService : IApplicationService
{
    private readonly Func<ulong, CancellationToken, Task<StorageMaintenanceResult>> _runOnce;
    private readonly IOptions<StorageServerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StorageMaintenanceService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private CancellationTokenSource? _shutdownCts;
    private Task? _loop;
    private int _started;
    private long _runAttemptCount;
    private ulong _nextMaintenanceRunId;

    /// <summary>
    /// Production constructor. <paramref name="coordinator"/> is resolved lazily on the first
    /// maintenance run, after <see cref="StorageEngineApplicationService"/> has started.
    /// </summary>
    public StorageMaintenanceService(
        Lazy<StorageMaintenanceCoordinator> coordinator,
        IOptions<StorageServerOptions> options,
        ILogger<StorageMaintenanceService> logger,
        TimeProvider? timeProvider = null)
        : this(
            (runId, ct) => coordinator.Value.RunOnceAsync(ct, runId),
            options,
            logger,
            delayAsync: null,
            timeProvider)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
    }

    /// <summary>Test constructor with injectable run/delay seams.</summary>
    internal StorageMaintenanceService(
        Func<CancellationToken, Task<StorageMaintenanceResult>> runOnce,
        IOptions<StorageServerOptions> options,
        ILogger<StorageMaintenanceService> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeProvider? timeProvider = null)
        : this(
            (runId, ct) => runOnce(ct),
            options,
            logger,
            delayAsync,
            timeProvider)
    {
        ArgumentNullException.ThrowIfNull(runOnce);
    }

    private StorageMaintenanceService(
        Func<ulong, CancellationToken, Task<StorageMaintenanceResult>> runOnce,
        IOptions<StorageServerOptions> options,
        ILogger<StorageMaintenanceService> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(runOnce);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _runOnce = runOnce;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delayAsync = delayAsync
            ?? ((delay, ct) => Task.Delay(delay, _timeProvider, ct));
    }

    /// <inheritdoc />
    public string Name => "StorageMaintenance";

    /// <inheritdoc />
    public Task? Execution => _loop;

    /// <summary>Number of completed RunOnce attempts (tests; includes failures).</summary>
    internal long RunAttemptCount => Volatile.Read(ref _runAttemptCount);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        var compaction = _options.Value.Storage?.Compaction ?? new ArticleCompactionPolicyOptions();
        if (compaction.Interval <= TimeSpan.Zero)
        {
            Interlocked.Exchange(ref _started, 0);
            throw new InvalidOperationException(
                "StorageServer:Storage:Compaction:Interval must be greater than zero.");
        }

        try
        {
            _shutdownCts = new CancellationTokenSource();
            _loop = RunLoopAsync(compaction.Interval, _shutdownCts.Token);
            StorageMaintenanceLogMessages.Started(_logger, compaction.Interval);
            return Task.CompletedTask;
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        StorageMaintenanceLogMessages.Stopping(_logger);
        var cts = Interlocked.Exchange(ref _shutdownCts, null);
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        var loop = Interlocked.Exchange(ref _loop, null);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Interlocked.Exchange(ref _started, 0);
        StorageMaintenanceLogMessages.Stopped(_logger);
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RunOnceSafeAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    await _delayAsync(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOnceSafeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maintenanceRunId = Interlocked.Increment(ref _nextMaintenanceRunId);
        var started = Stopwatch.GetTimestamp();
        StorageMaintenanceLogMessages.RunStarting(_logger, maintenanceRunId);
        try
        {
            var result = await _runOnce(maintenanceRunId, cancellationToken).ConfigureAwait(false);
            _ = Interlocked.Increment(ref _runAttemptCount);

            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            StorageMaintenanceRunLogging.LogRunOutcome(_logger, maintenanceRunId, in result, durationMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _ = Interlocked.Increment(ref _runAttemptCount);
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            StorageMaintenanceLogMessages.RunFailed(_logger, maintenanceRunId, durationMs, ex);
        }
    }
}
