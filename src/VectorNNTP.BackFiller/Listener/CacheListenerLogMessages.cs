namespace VectorNNTP.BackFiller.Listener;

/// <summary>Source-generated cache Listener logs. Never includes payloads, certificates, or secrets.</summary>
internal static partial class CacheListenerLogMessages
{
    [LoggerMessage(
        EventId = 5400,
        Level = LogLevel.Information,
        Message = "Cache Listener starting bindPort={BindPort} endpoints={EndpointCount}")]
    public static partial void Starting(ILogger logger, int BindPort, int EndpointCount);

    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Information,
        Message = "Cache Listener bound endpoint={Endpoint} family={AddressFamily}")]
    public static partial void EndpointBound(ILogger logger, string Endpoint, string AddressFamily);

    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Information,
        Message = "Cache Listener running sockets={SocketCount} bindPort={BindPort}")]
    public static partial void Running(ILogger logger, int SocketCount, int BindPort);

    [LoggerMessage(
        EventId = 5403,
        Level = LogLevel.Information,
        Message = "Cache Listener retiring")]
    public static partial void Retiring(ILogger logger);

    [LoggerMessage(
        EventId = 5404,
        Level = LogLevel.Information,
        Message = "Cache Listener stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(
        EventId = 5405,
        Level = LogLevel.Error,
        Message = "Cache Listener failed to start: {Reason}")]
    public static partial void StartFailed(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 5406,
        Level = LogLevel.Warning,
        Message = "Cache Listener TLS handshake failed remote={Remote}")]
    public static partial void HandshakeFailed(ILogger logger, string Remote);

    [LoggerMessage(
        EventId = 5407,
        Level = LogLevel.Warning,
        Message = "Cache Listener connection timed out stage={Stage} remote={Remote}")]
    public static partial void ConnectionTimedOut(ILogger logger, string Stage, string Remote);

    [LoggerMessage(
        EventId = 5408,
        Level = LogLevel.Debug,
        Message = "Cache Listener accepted remote={Remote} active={Active}")]
    public static partial void Accepted(ILogger logger, string Remote, int Active);

    [LoggerMessage(
        EventId = 5409,
        Level = LogLevel.Warning,
        Message = "Cache Listener rejected connection at capacity remote={Remote} max={Max}")]
    public static partial void CapacityRejected(ILogger logger, string Remote, int Max);

    [LoggerMessage(
        EventId = 5410,
        Level = LogLevel.Warning,
        Message = "Cache Listener skipped unsupported wildcard family family={AddressFamily}: {Reason}")]
    public static partial void WildcardFamilySkipped(ILogger logger, string AddressFamily, string Reason);

    [LoggerMessage(
        EventId = 5411,
        Level = LogLevel.Warning,
        Message = "Cache Listener connection failed remote={Remote} error={ErrorType}")]
    public static partial void ConnectionFailed(ILogger logger, string Remote, string ErrorType);
}
