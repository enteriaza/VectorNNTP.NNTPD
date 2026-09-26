namespace VectorNNTP.BackFiller.Accounts;

/// <summary>Source-generated account control-plane logs. Never includes passwords or connection strings.</summary>
internal static partial class ProviderAccountLogMessages
{
    [LoggerMessage(
        EventId = 5600,
        Level = LogLevel.Information,
        Message = "Provider account snapshot loaded serverId={ServerId} providers={ProviderCount} rejectedRows={RejectedCount}")]
    public static partial void SnapshotLoaded(ILogger logger, int ServerId, int ProviderCount, int RejectedCount);

    [LoggerMessage(
        EventId = 5601,
        Level = LogLevel.Information,
        Message = "Provider added backbone={Backbone} host={Host} port={Port} tls={UseTls} minSessions={MinSessions} maxSessions={MaxSessions}")]
    public static partial void ProviderAdded(
        ILogger logger,
        string Backbone,
        string Host,
        int Port,
        bool UseTls,
        int MinSessions,
        int MaxSessions);

    [LoggerMessage(
        EventId = 5602,
        Level = LogLevel.Information,
        Message = "Provider removed backbone={Backbone}")]
    public static partial void ProviderRemoved(ILogger logger, string Backbone);

    [LoggerMessage(
        EventId = 5603,
        Level = LogLevel.Information,
        Message = "Provider configuration changed backbone={Backbone} host={Host} port={Port} tls={UseTls} minSessions={MinSessions} maxSessions={MaxSessions}")]
    public static partial void ProviderChanged(
        ILogger logger,
        string Backbone,
        string Host,
        int Port,
        bool UseTls,
        int MinSessions,
        int MaxSessions);

    [LoggerMessage(
        EventId = 5604,
        Level = LogLevel.Warning,
        Message = "Provider account refresh failed; retaining last known-good snapshot")]
    public static partial void RefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 5605,
        Level = LogLevel.Information,
        Message = "Provider account refresh recovered providers={ProviderCount}")]
    public static partial void RefreshRecovered(ILogger logger, int ProviderCount);

    [LoggerMessage(
        EventId = 5606,
        Level = LogLevel.Warning,
        Message = "Rejected provider account row backbone={Backbone} reason={Reason}")]
    public static partial void RowRejected(ILogger logger, string Backbone, string Reason);

    [LoggerMessage(
        EventId = 5607,
        Level = LogLevel.Debug,
        Message = "Provider account snapshot unchanged providers={ProviderCount}")]
    public static partial void SnapshotUnchanged(ILogger logger, int ProviderCount);

    [LoggerMessage(
        EventId = 5608,
        Level = LogLevel.Information,
        Message = "Provider account control plane starting serverId={ServerId}")]
    public static partial void Starting(ILogger logger, int ServerId);

    [LoggerMessage(
        EventId = 5609,
        Level = LogLevel.Information,
        Message = "Provider account control plane stopped")]
    public static partial void Stopped(ILogger logger);
}
