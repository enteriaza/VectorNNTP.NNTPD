namespace VectorNNTP.Common.Dns
{
    /// <summary>Source-generated log messages for the owned DNS wire stack.</summary>
    internal static partial class DnsLogMessages
    {
        [LoggerMessage(
            EventId = 1950,
            Level = LogLevel.Debug,
            Message = "OS recursive resolver discovery failed ({ExceptionType}); using public fallback resolvers")]
        public static partial void RecursiveResolverDiscoveryFallback(
            ILogger logger,
            string ExceptionType,
            Exception exception);

        [LoggerMessage(
            EventId = 1951,
            Level = LogLevel.Debug,
            Message = "Zone-apex NS discovery found no nameservers for {ZoneApex}")]
        public static partial void ZoneApexNsDiscoveryFailed(ILogger logger, string ZoneApex);

        [LoggerMessage(
            EventId = 1952,
            Level = LogLevel.Debug,
            Message = "OS stub resolver failed for NS hostname {Host} ({ExceptionType})")]
        public static partial void NsHostnameOsResolveFailed(
            ILogger logger,
            string Host,
            string ExceptionType,
            Exception exception);

        [LoggerMessage(
            EventId = 1953,
            Level = LogLevel.Debug,
            Message = "Authoritative DNS UDP query timed out for {RecordName} at {Nameserver}")]
        public static partial void AuthoritativeUdpTimeout(ILogger logger, string Nameserver, string RecordName);

        [LoggerMessage(
            EventId = 1954,
            Level = LogLevel.Debug,
            Message = "Authoritative DNS TCP query timed out for {RecordName} at {Nameserver}")]
        public static partial void AuthoritativeTcpTimeout(ILogger logger, string Nameserver, string RecordName);
    }
}
