using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.Hosting;

/// <summary>
/// Ensures application shutdown is initiated exactly once and coordinates with the generic host lifetime.
/// </summary>
public sealed class NntpdHostLifetime
{
    private readonly ApplicationLifecycle _lifecycle;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<NntpdHostLifetime> _logger;
    private readonly object _sync = new();
    private Task? _shutdownTask;
    private int _shutdownRequested;

    /// <summary>
    /// Initializes a new instance of the <see cref="NntpdHostLifetime"/> class.
    /// </summary>
    public NntpdHostLifetime(
        ApplicationLifecycle lifecycle,
        IHostApplicationLifetime hostApplicationLifetime,
        IOptions<NntpdOptions> options,
        ILogger<NntpdHostLifetime> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(hostApplicationLifetime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _lifecycle = lifecycle;
        _hostApplicationLifetime = hostApplicationLifetime;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Requests a single graceful application shutdown. Subsequent calls await the same operation.
    /// </summary>
    /// <param name="cancellationToken">Host-provided shutdown token.</param>
    public Task RequestShutdownAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
            {
                _logger.LogInformation(
                    "Application shutdown requested exactly once for {ApplicationName}.",
                    _options.Value.ApplicationName);
            }

            _shutdownTask = ShutdownCoreAsync(cancellationToken);
            return _shutdownTask;
        }
    }

    /// <summary>
    /// Stops the generic host after an unexpected application-service termination.
    /// </summary>
    public void NotifyUnexpectedTermination()
    {
        if (!_options.Value.StopHostOnUnexpectedServiceTermination)
        {
            _logger.LogWarning(
                "Unexpected service termination observed, but {Option} is disabled.",
                nameof(NntpdOptions.StopHostOnUnexpectedServiceTermination));
            return;
        }

        _logger.LogCritical(
            "Requesting host stop due to unexpected application-service termination.");

        _hostApplicationLifetime.StopApplication();
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Application shutdown completed with failure.");
            throw;
        }
    }
}
