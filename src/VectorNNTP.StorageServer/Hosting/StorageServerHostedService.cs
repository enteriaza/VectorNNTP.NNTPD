using System.Diagnostics;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Logging;

namespace VectorNNTP.StorageServer.Hosting;

/// <summary>
/// Integrates <see cref="ApplicationLifecycle"/> with the .NET Generic Host.
/// </summary>
public sealed class StorageServerHostedService : BackgroundService
{
    private readonly ApplicationLifecycle _lifecycle;
    private readonly StorageServerHostShutdown _hostShutdown;
    private readonly IOptions<StorageServerOptions> _options;
    private readonly ILogger<StorageServerHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageServerHostedService"/> class.
    /// </summary>
    public StorageServerHostedService(
        ApplicationLifecycle lifecycle,
        StorageServerHostShutdown hostShutdown,
        IOptions<StorageServerOptions> options,
        ILogger<StorageServerHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(hostShutdown);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _lifecycle = lifecycle;
        _hostShutdown = hostShutdown;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        HostingLogMessages.HostStartingApplication(_logger, _options.Value.ApplicationName);

        await _lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);

        HostingLogMessages.ApplicationEnteredRunning(_logger, sw.ElapsedMilliseconds);

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _lifecycle.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            HostingLogMessages.HostedServiceExecutionCanceled(_logger);
        }
        catch (InvalidOperationException ex)
        {
            HostingLogMessages.UnexpectedApplicationServiceTermination(_logger, ex);
            _hostShutdown.NotifyUnexpectedTermination();
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        HostingLogMessages.HostStoppingApplication(_logger, _options.Value.ApplicationName);

        try
        {
            await _hostShutdown.RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
