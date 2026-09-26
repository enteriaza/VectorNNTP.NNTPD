namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>Source-generated RabbitMQ infrastructure log messages. Never includes credentials.</summary>
internal static partial class RabbitMqLogMessages
{
    [LoggerMessage(
        EventId = 2800,
        Level = LogLevel.Information,
        Message = "Connecting to RabbitMQ ({Hosts}, port {Port}, vhost {VirtualHost}, name {ConnectionName}, ssl {EnableSsl})")]
    public static partial void Connecting(
        ILogger logger,
        string Hosts,
        int Port,
        string VirtualHost,
        string ConnectionName,
        bool EnableSsl);

    [LoggerMessage(
        EventId = 2801,
        Level = LogLevel.Information,
        Message = "RabbitMQ connection established ({Host}:{Port}, vhost {VirtualHost}, name {ConnectionName}, generation {Generation}, {ElapsedMs} ms)")]
    public static partial void Connected(
        ILogger logger,
        string Host,
        int Port,
        string VirtualHost,
        string ConnectionName,
        long Generation,
        double ElapsedMs);

    [LoggerMessage(
        EventId = 2802,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection failed ({Hosts}, port {Port}, vhost {VirtualHost}, name {ConnectionName}, {ElapsedMs} ms)")]
    public static partial void ConnectionFailed(
        ILogger logger,
        Exception exception,
        string Hosts,
        int Port,
        string VirtualHost,
        string ConnectionName,
        double ElapsedMs);

    [LoggerMessage(
        EventId = 2803,
        Level = LogLevel.Warning,
        Message = "RabbitMQ connection lost generation={Generation} replyCode={ReplyCode} replyText={ReplyText} initiator={Initiator}")]
    public static partial void ConnectionLost(
        ILogger logger,
        long Generation,
        ushort ReplyCode,
        string ReplyText,
        string Initiator);

    [LoggerMessage(
        EventId = 2804,
        Level = LogLevel.Warning,
        Message = "RabbitMQ callback exception: {Message}")]
    public static partial void CallbackException(ILogger logger, string Message);

    [LoggerMessage(
        EventId = 2805,
        Level = LogLevel.Warning,
        Message = "RabbitMQ broker blocked the connection: {Reason}")]
    public static partial void ConnectionBlocked(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 2806,
        Level = LogLevel.Information,
        Message = "RabbitMQ broker unblocked the connection")]
    public static partial void ConnectionUnblocked(ILogger logger);

    [LoggerMessage(
        EventId = 2808,
        Level = LogLevel.Information,
        Message = "RabbitMQ reconnect attempt {Attempt} starting in {BackoffMs} ms (last generation {Generation})")]
    public static partial void ReconnectStarting(ILogger logger, int Attempt, double BackoffMs, long Generation);

    [LoggerMessage(
        EventId = 2809,
        Level = LogLevel.Information,
        Message = "RabbitMQ reconnect succeeded after {Attempt} attempt(s); generation {Generation} is current")]
    public static partial void ReconnectSucceeded(ILogger logger, int Attempt, long Generation);

    [LoggerMessage(
        EventId = 2810,
        Level = LogLevel.Error,
        Message = "RabbitMQ reconnect attempt {Attempt} failed ({ConsecutiveFailures} consecutive): {Reason}")]
    public static partial void ReconnectFailed(ILogger logger, int Attempt, int ConsecutiveFailures, string Reason);

    [LoggerMessage(
        EventId = 2811,
        Level = LogLevel.Error,
        Message = "RabbitMQ reconnect abandoned after {ConsecutiveFailures} consecutive failures")]
    public static partial void ReconnectAbandoned(ILogger logger, int ConsecutiveFailures);

    [LoggerMessage(
        EventId = 2815,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection disposal failed")]
    public static partial void ConnectionDisposeFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2816,
        Level = LogLevel.Information,
        Message = "RabbitMQ stopped")]
    public static partial void Stopped(ILogger logger);

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

    [LoggerMessage(
        EventId = 2819,
        Level = LogLevel.Warning,
        Message = "RabbitMQ connection close failed")]
    public static partial void ConnectionCloseFailed(ILogger logger, Exception exception);
}
