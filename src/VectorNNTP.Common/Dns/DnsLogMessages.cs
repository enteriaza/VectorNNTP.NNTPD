namespace VectorNNTP.Common.Dns
{
    /// <summary>Source-generated log messages for the owned DNS wire stack.</summary>
    internal static partial class DnsLogMessages
    {
        /// <summary>
        /// Emitted once, on the first discovery call, when enumerating OS DNS servers threw and public resolvers are used instead.
        /// </summary>
        /// <param name="logger">Discovery logger supplied by the caller. The event is not emitted when that logger is null.</param>
        /// <param name="ExceptionType">Runtime type name of the discovery failure.</param>
        /// <param name="exception">The exception thrown while reading NIC DNS addresses.</param>
        [LoggerMessage(
            EventId = 1950,
            Level = LogLevel.Debug,
            Message = "OS recursive resolver discovery failed ({ExceptionType}); using public fallback resolvers")]
        internal static partial void RecursiveResolverDiscoveryFallback(
            ILogger logger,
            string ExceptionType,
            Exception exception);

        /// <summary>
        /// Emitted when every configured recursive resolver failed to yield at least one apex nameserver address.
        /// </summary>
        /// <param name="logger">Discovery logger supplied by the caller.</param>
        /// <param name="ZoneApex">Zone apex that was queried, as passed by the caller.</param>
        [LoggerMessage(
            EventId = 1951,
            Level = LogLevel.Debug,
            Message = "Zone-apex NS discovery found no nameservers for {ZoneApex}")]
        internal static partial void ZoneApexNsDiscoveryFailed(ILogger logger, string ZoneApex);

        /// <summary>
        /// Emitted when the OS stub resolver throws for an NS hostname after wire A/AAAA resolution also produced no address.
        /// Cancellation is not logged; it propagates.
        /// </summary>
        /// <param name="logger">Discovery logger supplied by the caller.</param>
        /// <param name="Host">NS hostname passed to <see cref="System.Net.Dns.GetHostAddressesAsync(string, System.Threading.CancellationToken)"/>.</param>
        /// <param name="ExceptionType">Runtime type name of the stub-resolver failure.</param>
        /// <param name="exception">The stub-resolver failure. An empty address list is returned to the caller.</param>
        [LoggerMessage(
            EventId = 1952,
            Level = LogLevel.Debug,
            Message = "OS stub resolver failed for NS hostname {Host} ({ExceptionType})")]
        internal static partial void NsHostnameOsResolveFailed(
            ILogger logger,
            string Host,
            string ExceptionType,
            Exception exception);

        /// <summary>
        /// Emitted when the UDP TXT exchange to an authoritative nameserver times out.
        /// A socket failure returns no buffer and does not emit this event.
        /// </summary>
        /// <param name="logger">TXT-client logger supplied by the caller.</param>
        /// <param name="Nameserver">String form of the nameserver address that was queried.</param>
        /// <param name="RecordName">TXT owner name that was queried.</param>
        [LoggerMessage(
            EventId = 1953,
            Level = LogLevel.Debug,
            Message = "Authoritative DNS UDP query timed out for {RecordName} at {Nameserver}")]
        internal static partial void AuthoritativeUdpTimeout(ILogger logger, string Nameserver, string RecordName);

        /// <summary>
        /// Emitted when the TCP TXT exchange hits its receive timeout without the caller's token being canceled.
        /// Socket, disposal, and IO failures return no buffer and do not emit this event.
        /// </summary>
        /// <param name="logger">TXT-client logger supplied by the caller.</param>
        /// <param name="Nameserver">String form of the nameserver address that was queried.</param>
        /// <param name="RecordName">TXT owner name that was queried.</param>
        [LoggerMessage(
            EventId = 1954,
            Level = LogLevel.Debug,
            Message = "Authoritative DNS TCP query timed out for {RecordName} at {Nameserver}")]
        internal static partial void AuthoritativeTcpTimeout(ILogger logger, string Nameserver, string RecordName);
    }
}
