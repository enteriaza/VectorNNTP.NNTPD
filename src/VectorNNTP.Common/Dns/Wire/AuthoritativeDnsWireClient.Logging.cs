// <copyright file="AuthoritativeDnsWireClient.Logging.cs" company="Usenet Ninja">
// Copyright (c) Chris Knipe &lt;cknipe@opticnetworks.net&gt;. Licensed under the Apache License, Version 2.0 (see LICENSE).
// </copyright>
// AuthoritativeDnsWireClient.Logging.cs -- Source-generated [LoggerMessage] static partial methods.

using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Dns.Wire
{
    /// <summary>
    /// Source-generated <see cref="LoggerMessageAttribute"/> partial methods for <see cref="AuthoritativeDnsWireClient"/>.
    /// </summary>
    internal static partial class AuthoritativeDnsWireClient
    {
        /// <summary>
        /// Logs that a UDP TXT query to an authoritative nameserver timed out.
        /// </summary>
        /// <param name="logger">Logger for DNS wire client diagnostics.</param>
        /// <param name="nameserver">Authoritative nameserver address.</param>
        /// <param name="recordName">Queried record name.</param>
        [LoggerMessage(EventId = 420, Level = LogLevel.Debug,
            Message = "Certificates: Authoritative DNS UDP query timed out for {RecordName} at {Nameserver}")]
        internal static partial void LogAuthoritativeDnsUdpTimeout(ILogger logger, string nameserver, string recordName);

        /// <summary>
        /// Logs that a TCP TXT query to an authoritative nameserver timed out.
        /// </summary>
        /// <param name="logger">Logger for DNS wire client diagnostics.</param>
        /// <param name="nameserver">Authoritative nameserver address.</param>
        /// <param name="recordName">Queried record name.</param>
        [LoggerMessage(EventId = 421, Level = LogLevel.Debug,
            Message = "Certificates: Authoritative DNS TCP query timed out for {RecordName} at {Nameserver}")]
        internal static partial void LogAuthoritativeDnsTcpTimeout(ILogger logger, string nameserver, string recordName);
    }
}
