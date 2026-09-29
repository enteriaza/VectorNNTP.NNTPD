namespace VectorNNTP.StorageServer.Listener;

/// <summary>Source-generated StorageServer VATP listener logs.</summary>
internal static partial class StorageVatpListenerLogMessages
{
    [LoggerMessage(
        EventId = 5400,
        Level = LogLevel.Information,
        Message = "Storage VATP Listener starting bindPortTls={BindPortTls} endpoints={EndpointCount}")]
    public static partial void Starting(ILogger logger, int BindPortTls, int EndpointCount);

    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Information,
        Message = "Storage VATP Listener bound endpoint={Endpoint} family={AddressFamily}")]
    public static partial void EndpointBound(ILogger logger, string Endpoint, string AddressFamily);

    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Information,
        Message = "Storage VATP Listener running sockets={SocketCount} bindPortTls={BindPortTls}")]
    public static partial void Running(ILogger logger, int SocketCount, int BindPortTls);

    [LoggerMessage(
        EventId = 5403,
        Level = LogLevel.Information,
        Message = "Storage VATP Listener retiring")]
    public static partial void Retiring(ILogger logger);

    [LoggerMessage(
        EventId = 5404,
        Level = LogLevel.Information,
        Message = "Storage VATP Listener stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(
        EventId = 5405,
        Level = LogLevel.Error,
        Message = "Storage VATP Listener failed to start: {Reason}")]
    public static partial void StartFailed(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 5406,
        Level = LogLevel.Warning,
        Message = "Storage VATP Listener TLS handshake failed remote={Remote}")]
    public static partial void HandshakeFailed(ILogger logger, string Remote);

    [LoggerMessage(
        EventId = 5407,
        Level = LogLevel.Warning,
        Message = "Storage VATP Listener connection timed out stage={Stage} remote={Remote}")]
    public static partial void ConnectionTimedOut(ILogger logger, string Stage, string Remote);

    [LoggerMessage(
        EventId = 5408,
        Level = LogLevel.Debug,
        Message = "Storage VATP Listener accepted remote={Remote} active={Active}")]
    public static partial void Accepted(ILogger logger, string Remote, int Active);

    [LoggerMessage(
        EventId = 5409,
        Level = LogLevel.Warning,
        Message = "Storage VATP Listener rejected connection at capacity remote={Remote} max={Max}")]
    public static partial void CapacityRejected(ILogger logger, string Remote, int Max);

    [LoggerMessage(
        EventId = 5410,
        Level = LogLevel.Warning,
        Message = "Storage VATP Listener skipped unsupported wildcard family family={AddressFamily}: {Reason}")]
    public static partial void WildcardFamilySkipped(ILogger logger, string AddressFamily, string Reason);

    [LoggerMessage(
        EventId = 5411,
        Level = LogLevel.Warning,
        Message = "Storage VATP Listener connection failed remote={Remote} error={ErrorType}")]
    public static partial void ConnectionFailed(ILogger logger, string Remote, string ErrorType);
}

/// <summary>Source-generated StorageServer VATP session logs.</summary>
internal static partial class StorageVatpSessionLogMessages
{
    [LoggerMessage(
        EventId = 5500,
        Level = LogLevel.Debug,
        Message = "Storage VATP client HELLO accepted maxFramePayload={MaxFramePayload}")]
    public static partial void ClientHelloAccepted(ILogger logger, uint MaxFramePayload);

    [LoggerMessage(
        EventId = 5501,
        Level = LogLevel.Debug,
        Message = "Storage VATP OPEN rejected streamId={StreamId} requestId={RequestId}")]
    public static partial void OpenRejected(ILogger logger, uint StreamId, Guid RequestId);

    [LoggerMessage(
        EventId = 5502,
        Level = LogLevel.Debug,
        Message = "Storage VATP session closed")]
    public static partial void ConnectionClosed(ILogger logger);
}
