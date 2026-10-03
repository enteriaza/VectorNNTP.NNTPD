using System.Net;

namespace VectorNNTP.Common.Acme
{
    /// <summary>Source-generated ACME certificate, issuer, and DNS-01 log messages.</summary>
    internal static partial class AcmeLogMessages
    {
        [LoggerMessage(
            EventId = 1900,
            Level = LogLevel.Information,
            Message = "TLS disabled (BindPortTls=0); ACME certificate service idle")]
        internal static partial void TlsDisabledIdle(ILogger logger);

        [LoggerMessage(
            EventId = 1901,
            Level = LogLevel.Information,
            Message = "Ensuring ACME certificate for TLS (directory={Directory})")]
        internal static partial void EnsuringCertificate(ILogger logger, string Directory);

        [LoggerMessage(
            EventId = 1902,
            Level = LogLevel.Error,
            Message = "ACME certificate provisioning failed ({Failure})")]
        internal static partial void ProvisioningFailed(ILogger logger, string Failure);

        [LoggerMessage(
            EventId = 1903,
            Level = LogLevel.Debug,
            Message = "ACME renewal loop ended with an error during stop")]
        internal static partial void RenewalLoopStopError(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1904,
            Level = LogLevel.Information,
            Message = "ACME renewal completed successfully")]
        internal static partial void RenewalCompleted(ILogger logger);

        [LoggerMessage(
            EventId = 1905,
            Level = LogLevel.Warning,
            Message = "ACME renewal check failed ({Failure}); will retry next interval")]
        internal static partial void RenewalCheckFailed(ILogger logger, string Failure);

        [LoggerMessage(
            EventId = 1906,
            Level = LogLevel.Debug,
            Message = "Authoritative TXT lookup failed for {Name} via {Server}")]
        internal static partial void AuthoritativeTxtLookupFailed(
            ILogger logger,
            Exception exception,
            string Name,
            IPEndPoint Server);

        [LoggerMessage(
            EventId = 1907,
            Level = LogLevel.Debug,
            Message = "Failed resolving authoritative NS address for {NsName}")]
        private static partial void AuthoritativeNsResolveFailed(ILogger logger, Exception exception, string NsName);

        [LoggerMessage(
            EventId = 1908,
            Level = LogLevel.Information,
            Message = "ACME DNS-01 TXT records placed for {DomainCount} identifier(s)")]
        internal static partial void Dns01RecordsPlaced(ILogger logger, int DomainCount);

        [LoggerMessage(
            EventId = 1909,
            Level = LogLevel.Information,
            Message = "ACME DNS-01 authoritative visibility confirmed")]
        internal static partial void Dns01VisibilityConfirmed(ILogger logger);

        [LoggerMessage(
            EventId = 1910,
            Level = LogLevel.Information,
            Message = "ACME DNS-01 challenges triggered; waiting for authorization (timeout={TimeoutSeconds}s)")]
        internal static partial void Dns01ChallengesTriggered(ILogger logger, int TimeoutSeconds);

        [LoggerMessage(
            EventId = 1911,
            Level = LogLevel.Information,
            Message = "ACME order ready for finalization")]
        internal static partial void OrderReady(ILogger logger);

        [LoggerMessage(
            EventId = 1912,
            Level = LogLevel.Information,
            Message = "ACME certificate finalized")]
        internal static partial void CertificateFinalized(ILogger logger);

        [LoggerMessage(
            EventId = 1913,
            Level = LogLevel.Information,
            Message = "ACME account registered (uri configured).")]
        internal static partial void AccountRegistered(ILogger logger);

        [LoggerMessage(
            EventId = 1914,
            Level = LogLevel.Information,
            Message = "Reusing existing server certificate; notAfter={NotAfter:o}")]
        internal static partial void ReusingExistingCertificate(ILogger logger, DateTimeOffset NotAfter);

        [LoggerMessage(
            EventId = 1915,
            Level = LogLevel.Information,
            Message = "Existing certificate due for renewal; issuing replacement")]
        internal static partial void ExistingCertificateDueForRenewal(ILogger logger);

        [LoggerMessage(
            EventId = 1916,
            Level = LogLevel.Information,
            Message = "No usable server certificate ({Reason}); requesting issuance")]
        internal static partial void NoUsableCertificate(ILogger logger, string Reason);

        [LoggerMessage(
            EventId = 1917,
            Level = LogLevel.Warning,
            Message = "Certificate renewal failed ({Failure}); preserving existing certificate")]
        internal static partial void RenewalFailedPreservingExisting(ILogger logger, string Failure);

        [LoggerMessage(
            EventId = 1918,
            Level = LogLevel.Information,
            Message = "Server certificate ready generation={Generation} notAfter={NotAfter:o}")]
        internal static partial void ServerCertificateReady(ILogger logger, int Generation, DateTimeOffset NotAfter);

        [LoggerMessage(
            EventId = 1919,
            Level = LogLevel.Debug,
            Message = "ACME protocol account registered accountUrl={AccountUrl}")]
        internal static partial void AcmeProtocolAccountRegistered(ILogger logger, string AccountUrl);

        [LoggerMessage(
            EventId = 1920,
            Level = LogLevel.Debug,
            Message = "ACME badNonce; retrying {Url}")]
        internal static partial void AcmeBadNonceRetry(ILogger logger, Uri Url);

        [LoggerMessage(
            EventId = 1921,
            Level = LogLevel.Debug,
            Message = "ACME transport error; retrying in {Delay}")]
        internal static partial void AcmeTransportRetry(ILogger logger, TimeSpan Delay, Exception exception);

        [LoggerMessage(
            EventId = 1922,
            Level = LogLevel.Debug,
            Message = "ACME server status={StatusCode}; retrying in {Delay}")]
        internal static partial void AcmeServerErrorRetry(ILogger logger, int StatusCode, TimeSpan Delay);
    }
}
