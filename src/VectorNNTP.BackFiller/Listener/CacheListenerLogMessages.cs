namespace VectorNNTP.BackFiller.Listener;

/// <summary>Source-generated cache Listener logs. Never includes payloads, certificates, or secrets.</summary>
internal static partial class CacheListenerLogMessages
{
    [LoggerMessage(
        EventId = 5400,
        Level = LogLevel.Information,
        Message = "Cache Listener starting bindPortTls={BindPortTls} endpoints={EndpointCount}")]
    internal static partial void Starting(ILogger logger, int BindPortTls, int EndpointCount);

    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Information,
        Message = "Cache Listener bound endpoint={Endpoint} family={AddressFamily}")]
    internal static partial void EndpointBound(ILogger logger, string Endpoint, string AddressFamily);

    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Information,
        Message = "Cache Listener running sockets={SocketCount} bindPortTls={BindPortTls}")]
    internal static partial void Running(ILogger logger, int SocketCount, int BindPortTls);

    [LoggerMessage(
        EventId = 5403,
        Level = LogLevel.Information,
        Message = "Cache Listener retiring")]
    internal static partial void Retiring(ILogger logger);

    [LoggerMessage(
        EventId = 5404,
        Level = LogLevel.Information,
        Message = "Cache Listener stopped")]
    internal static partial void Stopped(ILogger logger);

    [LoggerMessage(
        EventId = 5405,
        Level = LogLevel.Error,
        Message = "Cache Listener failed to start: {Reason}")]
    internal static partial void StartFailed(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 5406,
        Level = LogLevel.Warning,
        Message = "Cache Listener TLS handshake failed remote={Remote}")]
    internal static partial void HandshakeFailed(ILogger logger, string Remote);

    [LoggerMessage(
        EventId = 5407,
        Level = LogLevel.Warning,
        Message = "Cache Listener connection timed out stage={Stage} remote={Remote}")]
    internal static partial void ConnectionTimedOut(ILogger logger, string Stage, string Remote);

    [LoggerMessage(
        EventId = 5408,
        Level = LogLevel.Debug,
        Message = "Cache Listener accepted remote={Remote} active={Active}")]
    internal static partial void Accepted(ILogger logger, string Remote, int Active);

    [LoggerMessage(
        EventId = 5409,
        Level = LogLevel.Warning,
        Message = "Cache Listener rejected connection at capacity remote={Remote} max={Max}")]
    internal static partial void CapacityRejected(ILogger logger, string Remote, int Max);

    [LoggerMessage(
        EventId = 5410,
        Level = LogLevel.Warning,
        Message = "Cache Listener skipped unsupported wildcard family family={AddressFamily}: {Reason}")]
    internal static partial void WildcardFamilySkipped(ILogger logger, string AddressFamily, string Reason);

    [LoggerMessage(
        EventId = 5411,
        Level = LogLevel.Warning,
        Message = "Cache Listener connection failed remote={Remote} error={ErrorType}")]
    internal static partial void ConnectionFailed(ILogger logger, string Remote, string ErrorType);
}
