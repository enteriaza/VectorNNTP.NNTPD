namespace VectorNNTP.NNTPD.NntpDb;

/// <summary>Source-generated NntpDB lifecycle log messages. Never includes credentials.</summary>
internal static partial class NntpDbLogMessages
{
    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Information,
        Message = "Initializing NntpDB")]
    public static partial void Initializing(ILogger logger);

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "NntpDB startup connectivity check succeeded (SELECT 1)")]
    public static partial void StartupCheckSucceeded(ILogger logger);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Error,
        Message = "NntpDB startup connectivity check failed; application will not start")]
    public static partial void StartupCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Information,
        Message = "NntpDB shutting down")]
    public static partial void ShuttingDown(ILogger logger);

    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Information,
        Message = "NntpDB stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(
        EventId = 2205,
        Level = LogLevel.Warning,
        Message = "NntpDB startup connectivity retry {Attempt} after a transient failure")]
    public static partial void StartupRetry(ILogger logger, int Attempt, Exception exception);

    [LoggerMessage(
        EventId = 2206,
        Level = LogLevel.Error,
        Message = "NntpDB connection string is invalid: {Reason}")]
    public static partial void InvalidConnectionString(ILogger logger, string Reason, Exception exception);
}
