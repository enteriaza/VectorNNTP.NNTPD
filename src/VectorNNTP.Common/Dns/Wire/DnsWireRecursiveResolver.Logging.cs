// <copyright file="DnsWireRecursiveResolver.Logging.cs" company="Usenet Ninja">
// Copyright (c) Chris Knipe &lt;cknipe@opticnetworks.net&gt;. Licensed under the Apache License, Version 2.0 (see LICENSE).
// </copyright>
// DnsWireRecursiveResolver.Logging.cs -- Source-generated [LoggerMessage] static partial methods.

using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Dns.Wire
{
    /// <summary>
    /// Source-generated <see cref="LoggerMessageAttribute"/> partial methods for <see cref="DnsWireRecursiveResolver"/>.
    /// </summary>
    internal static partial class DnsWireRecursiveResolver
    {
        /// <summary>
        /// Logs that OS recursive resolver discovery failed and public resolvers are used instead.
        /// </summary>
        /// <param name="logger">Logger for DNS resolver diagnostics.</param>
        /// <param name="exceptionType">Exception type name from discovery failure.</param>
        /// <param name="ex">The exception observed during resolver discovery.</param>
        [LoggerMessage(EventId = 410, Level = LogLevel.Debug,
            Message = "Certificates: OS recursive resolver discovery failed ({ExceptionType}); using public fallback resolvers")]
        internal static partial void LogRecursiveResolverDiscoveryFallback(ILogger logger, string exceptionType, Exception ex);

        /// <summary>
        /// Logs that authoritative NS discovery did not find any nameservers for a record.
        /// </summary>
        /// <param name="logger">Logger for DNS resolver diagnostics.</param>
        /// <param name="recordFqdn">Challenge record or hostname queried.</param>
        [LoggerMessage(EventId = 411, Level = LogLevel.Debug,
            Message = "Certificates: Authoritative NS discovery found no nameservers for {RecordFqdn}")]
        internal static partial void LogAuthoritativeNsDiscoveryFailed(ILogger logger, string recordFqdn);

        /// <summary>
        /// Logs that OS stub resolver hostname lookup failed for an NS hostname.
        /// </summary>
        /// <param name="logger">Logger for DNS resolver diagnostics.</param>
        /// <param name="host">NS hostname that could not be resolved.</param>
        /// <param name="exceptionType">Exception type name from hostname resolution failure.</param>
        /// <param name="ex">The exception observed during hostname resolution.</param>
        [LoggerMessage(EventId = 412, Level = LogLevel.Debug,
            Message = "Certificates: OS stub resolver failed for NS hostname {Host} ({ExceptionType})")]
        internal static partial void LogNsHostnameOsResolveFailed(ILogger logger, string host, string exceptionType, Exception ex);
    }
}
