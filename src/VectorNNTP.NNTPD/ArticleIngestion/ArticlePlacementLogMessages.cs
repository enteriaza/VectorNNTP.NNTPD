namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>Source-generated single-copy placement logs. Attempt is always 1.</summary>
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
}
