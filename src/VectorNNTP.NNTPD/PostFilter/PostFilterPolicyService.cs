using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.PostFilter;

/// <summary>
/// Loads PostFilter policy from <c>Nntpd:PostFilter</c>, publishes an immutable snapshot,
/// and refreshes it every five minutes.
/// </summary>
/// <remarks>
/// Initial compile failure prevents <c>RUNNING</c>. Refresh failure retains last-known-good.
/// POST observes <see cref="Current"/> via a volatile snapshot read.
/// </remarks>
internal sealed class PostFilterPolicyService : IPostFilterPolicySource, IApplicationService, IAsyncDisposable
{
    /// <summary>Refresh interval after a successful initial load.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly IOptionsMonitor<NntpdOptions> _options;
    private readonly ILogger<PostFilterPolicyService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _runCts = new();
    private PostFilterPolicySnapshot? _current;
    private Task? _execution;
    private int _started;
    private int _refreshing;
    private int _disposed;

    /// <summary>Initializes a production policy service.</summary>
    public PostFilterPolicyService(
        IOptionsMonitor<NntpdOptions> options,
        ILogger<PostFilterPolicyService> logger)
        : this(options, logger, TimeProvider.System, RefreshInterval)
    {
    }

    /// <summary>Initializes a service with an explicit clock and interval (tests).</summary>
    internal PostFilterPolicyService(
        IOptionsMonitor<NntpdOptions> options,
        ILogger<PostFilterPolicyService> logger,
        TimeProvider timeProvider,
        TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
        _interval = interval;
    }

    /// <inheritdoc />
    public string Name => "PostFilterPolicy";

    /// <inheritdoc />
    public Task? Execution => _execution;

    /// <inheritdoc />
    public PostFilterPolicySnapshot Current
    {
        get
        {
            var snapshot = Volatile.Read(ref _current);
            if (snapshot is null)
            {
                throw new InvalidOperationException("PostFilter policy has not been published.");
            }

            return snapshot;
        }
    }

    /// <summary>Gets whether a snapshot has been published (tests).</summary>
    internal bool HasSnapshot => Volatile.Read(ref _current) is not null;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            Publish(CompileCurrent());
            var published = Current;
            PostFilterLogMessages.PolicyPublished(
                _logger,
                published.Gate.ToString(),
                published.SpamAssassinEnabled,
                published.SpamAssassinMaxArticleSize,
                published.SpamAssassinExcludeArtTypes.ToString(),
                published.SpamAssassinHosts.Count,
                published.SpamAssassinMaxConnections);
            _execution = RunAsync(_runCts.Token);
            await Task.CompletedTask.ConfigureAwait(false);
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
        await _runCts.CancelAsync().ConfigureAwait(false);
        if (_execution is not null)
        {
            try
            {
                await _execution.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _runCts.CancelAsync().ConfigureAwait(false);
        _runCts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Interlocked.Exchange(ref _refreshing, 1) == 1)
            {
                continue;
            }

            try
            {
                Publish(CompileCurrent());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PostFilterLogMessages.PolicyRefreshFailed(_logger, ex);
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        }
    }

    private PostFilterPolicySnapshot CompileCurrent()
    {
        var options = _options.CurrentValue.PostFilter ?? new PostFilterOptions();
        return PostFilterPolicyCompiler.Compile(options);
    }

    private void Publish(PostFilterPolicySnapshot snapshot) =>
        Volatile.Write(ref _current, snapshot);
}
