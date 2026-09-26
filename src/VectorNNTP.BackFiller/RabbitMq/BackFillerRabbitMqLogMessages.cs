namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>Source-generated RabbitMQ infrastructure log messages. Never includes credentials.</summary>
internal static partial class BackFillerRabbitMqLogMessages
{
    [LoggerMessage(
        EventId = 5200,
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
        EventId = 5201,
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
        EventId = 5202,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection failed ({Hosts}, port {Port}, vhost {VirtualHost}, name {ConnectionName}, {ElapsedMs} ms): {Reason}")]
    public static partial void ConnectionFailed(
        ILogger logger,
        string Hosts,
        int Port,
        string VirtualHost,
        string ConnectionName,
        double ElapsedMs,
        string Reason);

    [LoggerMessage(
        EventId = 5203,
        Level = LogLevel.Warning,
        Message = "RabbitMQ connection lost generation={Generation} replyCode={ReplyCode} replyText={ReplyText} initiator={Initiator}")]
    public static partial void ConnectionLost(
        ILogger logger,
        long Generation,
        ushort ReplyCode,
        string ReplyText,
        string Initiator);

    [LoggerMessage(
        EventId = 5204,
        Level = LogLevel.Warning,
        Message = "RabbitMQ callback exception: {Message}")]
    public static partial void CallbackException(ILogger logger, string Message);

    [LoggerMessage(
        EventId = 5205,
        Level = LogLevel.Warning,
        Message = "RabbitMQ broker blocked the connection: {Reason}")]
    public static partial void ConnectionBlocked(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 5206,
        Level = LogLevel.Information,
        Message = "RabbitMQ broker unblocked the connection")]
    public static partial void ConnectionUnblocked(ILogger logger);

    [LoggerMessage(
        EventId = 5207,
        Level = LogLevel.Information,
        Message = "RabbitMQ reconnect attempt {Attempt} starting in {BackoffMs} ms (last generation {Generation})")]
    public static partial void ReconnectStarting(ILogger logger, int Attempt, double BackoffMs, long Generation);

    [LoggerMessage(
        EventId = 5208,
        Level = LogLevel.Information,
        Message = "RabbitMQ reconnect succeeded after {Attempt} attempt(s); generation {Generation} is current")]
    public static partial void ReconnectSucceeded(ILogger logger, int Attempt, long Generation);

    [LoggerMessage(
        EventId = 5209,
        Level = LogLevel.Error,
        Message = "RabbitMQ reconnect attempt {Attempt} failed: {Reason}")]
    public static partial void ReconnectFailed(ILogger logger, int Attempt, string Reason);

    [LoggerMessage(
        EventId = 5210,
        Level = LogLevel.Warning,
        Message = "RabbitMQ still reconnecting after {Attempt} attempt(s): {Reason}")]
    public static partial void ReconnectStillFailing(ILogger logger, int Attempt, string Reason);

    [LoggerMessage(
        EventId = 5211,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection disposal failed: {Reason}")]
    public static partial void ConnectionDisposeFailed(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 5212,
        Level = LogLevel.Information,
        Message = "RabbitMQ stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(
        EventId = 5213,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection failed during startup: {Reason}")]
    public static partial void StartupFailed(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 5214,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection opened but is not usable")]
    public static partial void ConnectionNotUsable(ILogger logger);

    [LoggerMessage(
        EventId = 5215,
        Level = LogLevel.Warning,
        Message = "RabbitMQ connection close failed: {Reason}")]
    public static partial void ConnectionCloseFailed(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 5216,
        Level = LogLevel.Debug,
        Message = "Ignoring RabbitMQ event from stale generation {Generation}")]
    public static partial void StaleGenerationIgnored(ILogger logger, long Generation);
}
