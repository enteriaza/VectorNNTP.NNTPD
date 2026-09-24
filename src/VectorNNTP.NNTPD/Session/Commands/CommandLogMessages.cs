using System.Net.Sockets;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Source-generated command RX/TX and handler log messages.</summary>
internal static partial class CommandLogMessages
{
    [LoggerMessage(
        EventId = 1600,
        Level = LogLevel.Information,
        Message = "[{Client}] RX: {Command}")]
    public static partial void CommandRx(ILogger logger, string Client, string Command);

    [LoggerMessage(
        EventId = 1601,
        Level = LogLevel.Information,
        Message = "[{Client}] RX rejected: verb={Verb} qualifier={Qualifier} status={Status} [{Detail}]")]
    public static partial void CommandRejected(
        ILogger logger,
        string Client,
        NntpVerb Verb,
        NntpVerb Qualifier,
        NntpParseStatus Status,
        string Detail);

    [LoggerMessage(
        EventId = 1602,
        Level = LogLevel.Information,
        Message = "[{Client}] TX: {Command} executed in {ElapsedSeconds:F3}s")]
    public static partial void CommandTx(ILogger logger, string Client, string Command, double ElapsedSeconds);

    [LoggerMessage(
        EventId = 1603,
        Level = LogLevel.Information,
        Message = "[{Client}] TX: {Command} executed in {ElapsedSeconds:F3}s [{Detail}]")]
    public static partial void CommandTxWithDetail(
        ILogger logger,
        string Client,
        string Command,
        double ElapsedSeconds,
        string Detail);

    [LoggerMessage(
        EventId = 1604,
        Level = LogLevel.Error,
        Message = "[{Client}] {Command} failed")]
    public static partial void CommandFailed(ILogger logger, Exception exception, string Client, string Command);

    [LoggerMessage(
        EventId = 1605,
        Level = LogLevel.Warning,
        Message = "[{Client}] TAKETHIS temporary failure; closing connection")]
    public static partial void TakeThisTemporaryFailure(ILogger logger, string Client);

    [LoggerMessage(
        EventId = 1606,
        Level = LogLevel.Error,
        Message = "[{Client}] STARTTLS handshake failed")]
    public static partial void StartTlsHandshakeFailed(ILogger logger, Exception exception, string Client);

    [LoggerMessage(
        EventId = 1607,
        Level = LogLevel.Warning,
        Message = "[{Client}] COMPRESS DEFLATE refused before activation")]
    public static partial void CompressRefusedBeforeActivation(ILogger logger, Exception exception, string Client);

    [LoggerMessage(
        EventId = 1608,
        Level = LogLevel.Error,
        Message = "[{Client}] COMPRESS DEFLATE activation failed after 206")]
    public static partial void CompressActivationFailed(ILogger logger, Exception exception, string Client);

    [LoggerMessage(
        EventId = 1609,
        Level = LogLevel.Debug,
        Message = "[{Client}] Peer disconnected during QUIT termination")]
    public static partial void QuitPeerDisconnected(ILogger logger, Exception exception, string Client);

    [LoggerMessage(
        EventId = 1610,
        Level = LogLevel.Error,
        Message = "[{Client}] QUIT uncaught during termination: {Type} socket={Socket}")]
    public static partial void QuitUncaught(
        ILogger logger,
        Exception exception,
        string Client,
        string? Type,
        SocketError? Socket);
}
