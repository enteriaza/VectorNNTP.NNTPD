namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Source-generated placement logs. First placement is attempt 1.
/// The best-effort replica is attempt 2.
/// </summary>
internal static partial class ArticlePlacementLogMessages
{
    [LoggerMessage(
        EventId = 2620,
        Level = LogLevel.Information,
        Message = "Article placement accepted artId={ArtId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=Accepted elapsedMs={ElapsedMs} attempt=1")]
    public static partial void Accepted(ILogger logger, string ArtId, int ServerId, int VatpPort, string Fqdn, long ElapsedMs);

    [LoggerMessage(
        EventId = 2621,
        Level = LogLevel.Information,
        Message = "Article placement duplicate artId={ArtId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=Duplicate elapsedMs={ElapsedMs} attempt=1")]
    public static partial void Duplicate(ILogger logger, string ArtId, int ServerId, int VatpPort, string Fqdn, long ElapsedMs);

    [LoggerMessage(
        EventId = 2622,
        Level = LogLevel.Warning,
        Message = "Article placement conflict artId={ArtId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=Conflict elapsedMs={ElapsedMs} attempt=1")]
    public static partial void Conflict(ILogger logger, string ArtId, int ServerId, int VatpPort, string Fqdn, long ElapsedMs);

    [LoggerMessage(
        EventId = 2623,
        Level = LogLevel.Warning,
        Message = "Article placement rejected capacity artId={ArtId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=RejectedCapacity elapsedMs={ElapsedMs} attempt=1")]
    public static partial void RejectedCapacity(ILogger logger, string ArtId, int ServerId, int VatpPort, string Fqdn, long ElapsedMs);

    [LoggerMessage(
        EventId = 2624,
        Level = LogLevel.Warning,
        Message = "Article placement rejected pressure artId={ArtId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=RejectedPressure elapsedMs={ElapsedMs} attempt=1")]
    public static partial void RejectedPressure(ILogger logger, string ArtId, int ServerId, int VatpPort, string Fqdn, long ElapsedMs);

    [LoggerMessage(
        EventId = 2625,
        Level = LogLevel.Warning,
        Message = "Article placement rejected invalid artId={ArtId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=RejectedInvalid elapsedMs={ElapsedMs} attempt=1")]
    public static partial void RejectedInvalid(ILogger logger, string ArtId, int ServerId, int VatpPort, string Fqdn, long ElapsedMs);

    [LoggerMessage(
        EventId = 2626,
        Level = LogLevel.Warning,
        Message = "Article placement found no active server artId={ArtId} outcome=NoActiveServer elapsedMs={ElapsedMs} attempt=1")]
    public static partial void NoActiveServer(ILogger logger, string ArtId, long ElapsedMs);

    [LoggerMessage(
        EventId = 2627,
        Level = LogLevel.Warning,
        Message = "Article placement transport failure artId={ArtId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} failure={Failure} elapsedMs={ElapsedMs} attempt=1")]
    public static partial void TransportFailed(
        ILogger logger,
        string ArtId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        string Failure,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2628,
        Level = LogLevel.Information,
        Message = "Article placement cancelled artId={ArtId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} elapsedMs={ElapsedMs} attempt=1")]
    public static partial void Cancelled(ILogger logger, string ArtId, int ServerId, int VatpPort, string Fqdn, long ElapsedMs);

    [LoggerMessage(
        EventId = 2629,
        Level = LogLevel.Warning,
        Message = "Article placement acknowledgement not observed artId={ArtId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} elapsedMs={ElapsedMs} attempt=1")]
    public static partial void AcknowledgementNotObserved(
        ILogger logger,
        string ArtId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2630,
        Level = LogLevel.Information,
        Message = "Article replica accepted artId={ArtId} firstServerId={FirstServerId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=Accepted elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaAccepted(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2631,
        Level = LogLevel.Information,
        Message = "Article replica duplicate artId={ArtId} firstServerId={FirstServerId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=Duplicate elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaDuplicate(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2632,
        Level = LogLevel.Warning,
        Message = "Article replica conflict artId={ArtId} firstServerId={FirstServerId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=Conflict elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaConflict(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2633,
        Level = LogLevel.Warning,
        Message = "Article replica rejected capacity artId={ArtId} firstServerId={FirstServerId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=RejectedCapacity elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaRejectedCapacity(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2634,
        Level = LogLevel.Warning,
        Message = "Article replica rejected pressure artId={ArtId} firstServerId={FirstServerId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=RejectedPressure elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaRejectedPressure(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2635,
        Level = LogLevel.Warning,
        Message = "Article replica rejected invalid artId={ArtId} firstServerId={FirstServerId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=RejectedInvalid elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaRejectedInvalid(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2636,
        Level = LogLevel.Information,
        Message = "Article replica found no eligible target artId={ArtId} firstServerId={FirstServerId} outcome=NoEligibleReplica elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaNoTarget(ILogger logger, string ArtId, int FirstServerId, long ElapsedMs);

    [LoggerMessage(
        EventId = 2637,
        Level = LogLevel.Warning,
        Message = "Article replica transport failure artId={ArtId} firstServerId={FirstServerId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} failure={Failure} elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaTransportFailed(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        string Failure,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2638,
        Level = LogLevel.Warning,
        Message = "Article replica timed out artId={ArtId} firstServerId={FirstServerId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} failure={Failure} elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaTimedOut(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        string Failure,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2639,
        Level = LogLevel.Information,
        Message = "Article replica cancelled artId={ArtId} firstServerId={FirstServerId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=Cancelled elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaCancelled(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2640,
        Level = LogLevel.Warning,
        Message = "Article replica acknowledgement not observed artId={ArtId} firstServerId={FirstServerId} serverId={ServerId} fqdn={Fqdn} vatpPort={VatpPort} outcome=AcknowledgementNotObserved elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaAcknowledgementNotObserved(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int ServerId,
        int VatpPort,
        string Fqdn,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 2641,
        Level = LogLevel.Information,
        Message = "Article replica not started artId={ArtId} firstServerId={FirstServerId} outcome=Cancelled elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaNotStarted(ILogger logger, string ArtId, int FirstServerId, long ElapsedMs);

    [LoggerMessage(
        EventId = 2642,
        Level = LogLevel.Information,
        Message = "Article replica not started artId={ArtId} firstServerId={FirstServerId} outcome=SecondCopySenderDisabled elapsedMs={ElapsedMs} attempt=2")]
    public static partial void SecondCopySenderDisabled(ILogger logger, string ArtId, int FirstServerId, long ElapsedMs);

    [LoggerMessage(
        EventId = 2643,
        Level = LogLevel.Warning,
        Message = "Article replica not started artId={ArtId} firstServerId={FirstServerId} outcome=PinNotDurable elapsedMs={ElapsedMs} attempt=2")]
    public static partial void ReplicaPinNotDurable(ILogger logger, string ArtId, int FirstServerId, long ElapsedMs);

    [LoggerMessage(
        EventId = 2644,
        Level = LogLevel.Warning,
        Message = "Article replica not started artId={ArtId} firstServerId={FirstServerId} targetServerId={TargetServerId} outcome=PinnedTargetUndialable elapsedMs={ElapsedMs} attempt=2")]
    public static partial void PinnedTargetUndialable(
        ILogger logger,
        string ArtId,
        int FirstServerId,
        int TargetServerId,
        long ElapsedMs);
}
