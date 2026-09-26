namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>Source-generated RabbitMQ infrastructure log messages.</summary>
internal static partial class RabbitMqLogMessages
{
    [LoggerMessage(
        EventId = 2800,
        Level = LogLevel.Information,
        Message = "RabbitMQ connection attempt started Hosts={Hosts} Port={Port} VirtualHost={VirtualHost} ConnectionName={ConnectionName} EnableSsl={EnableSsl}")]
    public static partial void ConnectionAttempt(
        ILogger logger,
        string Hosts,
        int Port,
        string VirtualHost,
        string ConnectionName,
        bool EnableSsl);

    [LoggerMessage(
        EventId = 2801,
        Level = LogLevel.Information,
        Message = "RabbitMQ connection established Host={Host} Port={Port} VirtualHost={VirtualHost} ConnectionName={ConnectionName} Generation={Generation} DurationMs={DurationMs}")]
    public static partial void ConnectionSucceeded(
        ILogger logger,
        string Host,
        int Port,
        string VirtualHost,
        string ConnectionName,
        long Generation,
        double DurationMs);

    [LoggerMessage(
        EventId = 2802,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection attempt failed Hosts={Hosts} Port={Port} VirtualHost={VirtualHost} ConnectionName={ConnectionName} DurationMs={DurationMs}")]
    public static partial void ConnectionFailed(
        ILogger logger,
        Exception exception,
        string Hosts,
        int Port,
        string VirtualHost,
        string ConnectionName,
        double DurationMs);

    [LoggerMessage(
        EventId = 2803,
        Level = LogLevel.Warning,
        Message = "RabbitMQ connection shutdown observed ReplyCode={ReplyCode} ReplyText={ReplyText} Initiator={Initiator}")]
    public static partial void ConnectionShutdown(ILogger logger, ushort ReplyCode, string ReplyText, string Initiator);

    [LoggerMessage(
        EventId = 2804,
        Level = LogLevel.Warning,
        Message = "RabbitMQ callback exception observed Message={Message}")]
    public static partial void CallbackException(ILogger logger, string Message);

    [LoggerMessage(
        EventId = 2805,
        Level = LogLevel.Warning,
        Message = "RabbitMQ broker blocked the connection Reason={Reason}")]
    public static partial void ConnectionBlocked(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 2806,
        Level = LogLevel.Information,
        Message = "RabbitMQ broker unblocked the connection")]
    public static partial void ConnectionUnblocked(ILogger logger);

    [LoggerMessage(
        EventId = 2807,
        Level = LogLevel.Warning,
        Message = "RabbitMQ recovery queued Reason={Reason}")]
    public static partial void RecoveryQueued(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 2808,
        Level = LogLevel.Information,
        Message = "RabbitMQ recovery attempt starting Attempt={Attempt} BackoffMs={BackoffMs}")]
    public static partial void RecoveryStarting(ILogger logger, int Attempt, double BackoffMs);

    [LoggerMessage(
        EventId = 2809,
        Level = LogLevel.Information,
        Message = "RabbitMQ recovery attempt succeeded Attempt={Attempt} Generation={Generation}")]
    public static partial void RecoverySucceeded(ILogger logger, int Attempt, long Generation);

    [LoggerMessage(
        EventId = 2810,
        Level = LogLevel.Error,
        Message = "RabbitMQ recovery attempt failed Attempt={Attempt} ConsecutiveFailures={ConsecutiveFailures} Reason={Reason}")]
    public static partial void RecoveryFailed(ILogger logger, int Attempt, int ConsecutiveFailures, string Reason);

    [LoggerMessage(
        EventId = 2811,
        Level = LogLevel.Error,
        Message = "RabbitMQ recovery failure threshold reached ConsecutiveFailures={ConsecutiveFailures}")]
    public static partial void RecoveryFailureThresholdReached(ILogger logger, int ConsecutiveFailures);

    [LoggerMessage(
        EventId = 2812,
        Level = LogLevel.Warning,
        Message = "RabbitMQ client automatic recovery error observed ConsecutiveErrors={ConsecutiveErrors} Reason={Reason}")]
    public static partial void ClientAutomaticRecoveryError(ILogger logger, int ConsecutiveErrors, string Reason);

    [LoggerMessage(
        EventId = 2813,
        Level = LogLevel.Warning,
        Message = "RabbitMQ client automatic recovery error threshold reached ConsecutiveErrors={ConsecutiveErrors}")]
    public static partial void ClientAutomaticRecoveryThresholdReached(ILogger logger, int ConsecutiveErrors);

    [LoggerMessage(
        EventId = 2814,
        Level = LogLevel.Information,
        Message = "RabbitMQ client automatic recovery succeeded")]
    public static partial void ClientAutomaticRecoverySucceeded(ILogger logger);

    [LoggerMessage(
        EventId = 2815,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection disposal failed")]
    public static partial void ConnectionDisposeFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2816,
        Level = LogLevel.Information,
        Message = "RabbitMQ connection service shutdown completed")]
    public static partial void ShutdownCompleted(ILogger logger);

    [LoggerMessage(
        EventId = 2817,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection failed during startup")]
    public static partial void StartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2818,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection opened but is not usable")]
    public static partial void ConnectionNotUsable(ILogger logger);
}
