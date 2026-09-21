using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.Hosting;

/// <summary>
/// Integrates <see cref="ApplicationLifecycle"/> with the .NET Generic Host.
/// </summary>
/// <remarks>
/// Startup runs during <see cref="StartAsync"/> so a failed initialization prevents the host from
/// reporting successful start. While running, the background execution waits for host shutdown or
/// unexpected service termination. Fatal failures stop the host via
/// <see cref="BackgroundServiceExceptionBehavior.StopHost"/>.
/// </remarks>
public sealed class NntpdHostedService : BackgroundService
{
    private readonly ApplicationLifecycle _lifecycle;
    private readonly NntpdHostLifetime _hostLifetime;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<NntpdHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NntpdHostedService"/> class.
    /// </summary>
    public NntpdHostedService(
        ApplicationLifecycle lifecycle,
        NntpdHostLifetime hostLifetime,
        IOptions<NntpdOptions> options,
        ILogger<NntpdHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(hostLifetime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _lifecycle = lifecycle;
        _hostLifetime = hostLifetime;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "Host starting application {ApplicationName}.",
            _options.Value.ApplicationName);

        await _lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Application entered Running state after {ElapsedMs} ms. Host startup continuing.",
            sw.ElapsedMilliseconds);

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
            _logger.LogInformation("Hosted service execution canceled due to host shutdown.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogCritical(
                ex,
                "Background hosted service observed unexpected application-service termination.");
            _hostLifetime.NotifyUnexpectedTermination();
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Host stopping application {ApplicationName}.",
            _options.Value.ApplicationName);

        try
        {
            await _hostLifetime.RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
