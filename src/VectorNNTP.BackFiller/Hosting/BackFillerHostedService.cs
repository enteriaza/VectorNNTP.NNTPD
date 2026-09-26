namespace VectorNNTP.BackFiller.Hosting;

/// <summary>
/// Phase 0 placeholder hosted service that keeps the Generic Host running until shutdown.
/// </summary>
/// <remarks>
/// This service does not consume RabbitMQ, open NNTP sessions, or retain articles.
/// Those runtimes are added in later port phases.
/// </remarks>
internal sealed partial class BackFillerHostedService : BackgroundService
{
    private readonly ILogger<BackFillerHostedService> _logger;

    /// <summary>
    /// Initializes a new placeholder hosted service.
    /// </summary>
    /// <param name="logger">Structured logger for host lifetime events.</param>
    public BackFillerHostedService(ILogger<BackFillerHostedService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        LogStarted(_logger);
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        LogStopping(_logger);
        return base.StopAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "VectorNNTP.BackFiller host started (Phase 0 foundation; article-work runtime is not registered).")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "VectorNNTP.BackFiller host stopping.")]
    private static partial void LogStopping(ILogger logger);
}
