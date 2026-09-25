namespace VectorNNTP.NNTPD.Telemetry;

/// <summary>Source-generated standard application telemetry messages.</summary>
internal static partial class ApplicationTelemetryLogMessages
{
    [LoggerMessage(
        EventId = 2400,
        Level = LogLevel.Information,
        Message = "HistoryDb lookups={Lookups} hits={Hits} misses={Misses} errors={Errors} wait_ms={WaitMs}")]
    public static partial void HistoryDb(
        ILogger logger,
        long Lookups,
        long Hits,
        long Misses,
        long Errors,
        long WaitMs);

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Information,
        Message = "TransitIngressQueue articles={Articles} bytes={Bytes} peak_articles={PeakArticles} peak_bytes={PeakBytes} waiting={Waiting} limit={Limit} admit_fail={AdmissionFailures} admission_wait_ms={AdmissionWaitMs}")]
    public static partial void TransitIngressQueue(
        ILogger logger,
        int Articles,
        long Bytes,
        int PeakArticles,
        long PeakBytes,
        int Waiting,
        long Limit,
        long AdmissionFailures,
        long AdmissionWaitMs);

    [LoggerMessage(
        EventId = 2402,
        Level = LogLevel.Information,
        Message = "ActiveSessions active={Active} established={Established} idle={Idle} receiving={Receiving} waiting_history={WaitingHistory} waiting_queue={WaitingQueue} waiting_window={WaitingWindow} completing={Completing}")]
    public static partial void ActiveSessions(
        ILogger logger,
        int Active,
        int Established,
        int Idle,
        int Receiving,
        int WaitingHistory,
        int WaitingQueue,
        int WaitingWindow,
        int Completing);

    [LoggerMessage(
        EventId = 2403,
        Level = LogLevel.Information,
        Message = "{PeerName} active={Active}/{MaxIncomingConnections} peak={Peak} accepted={Accepted} transmitted={Transmitted} rejected={Rejected} articles={Articles} bytes={Bytes} avg_article={AverageArticleBytes} avg_mbps={AverageMbps:F2} checks={Checks}")]
    public static partial void TransitPeer(
        ILogger logger,
        string PeerName,
        int Active,
        int MaxIncomingConnections,
        int Peak,
        long Accepted,
        long Transmitted,
        long Rejected,
        long Articles,
        long Bytes,
        long AverageArticleBytes,
        double AverageMbps,
        long Checks);
}
