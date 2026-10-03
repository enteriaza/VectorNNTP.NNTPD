using VectorNNTP.Common.Cloudflare;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.Acme
{
    /// <summary>
    /// Creates ACME components only when TLS is enabled so non-TLS startups never touch ACME state.
    /// </summary>
    internal class AcmeComponentFactory
    {
        /// <summary>ACME and Cloudflare options read on each factory call.</summary>
        private readonly IOptions<AcmeCloudflareOptions> _options;

        /// <summary>DNS client passed to the <see cref="Dns01Solver"/> created with the manager.</summary>
        private readonly ICloudflareDnsClient _cloudflareDnsClient;

        /// <summary>HTTP client factory passed to <see cref="AcmeIssuer"/>.</summary>
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>Creates loggers for the resolver, issuer, and certificate manager.</summary>
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>Guards one-time construction of <see cref="_manager"/> and <see cref="_provider"/>.</summary>
        private readonly Lock _sync = new();

        /// <summary>Manager created on the first TLS-enabled request. <see langword="null"/> until then.</summary>
        private CertificateManager? _manager;

        /// <summary>Provider created with <see cref="_manager"/>. <see langword="null"/> until the manager exists.</summary>
        private IServerCertificateProvider? _provider;

        /// <summary>Stores the options, DNS client, HTTP factory, and logger factory used when TLS is enabled.</summary>
        /// <param name="options">ACME and Cloudflare options.</param>
        /// <param name="cloudflareDnsClient">Client that creates and deletes DNS-01 TXT records.</param>
        /// <param name="httpClientFactory">Factory for the ACME directory HTTP client.</param>
        /// <param name="loggerFactory">Factory for component loggers.</param>
        internal AcmeComponentFactory(
            IOptions<AcmeCloudflareOptions> options,
            ICloudflareDnsClient cloudflareDnsClient,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(cloudflareDnsClient);
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            ArgumentNullException.ThrowIfNull(loggerFactory);
            _options = options;
            _cloudflareDnsClient = cloudflareDnsClient;
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Returns a certificate manager when TLS is enabled; otherwise <see langword="null"/>.
        /// The first TLS-enabled call builds the account store, certificate store, DNS-01 solver, issuer, journal, and manager under <see cref="_sync"/>.
        /// </summary>
        /// <returns>The cached manager, or <see langword="null"/> when <see cref="AcmeCloudflareOptions.IsTlsListenerEnabled"/> is false.</returns>
        internal virtual CertificateManager? GetOrCreateManager()
        {
            var options = _options.Value;
            if (!options.IsTlsListenerEnabled)
            {
                return null;
            }

            lock (_sync)
            {
                if (_manager is not null)
                {
                    return _manager;
                }

                var stateDir = AcmeCloudflareOptionsValidator.ResolveAcmeStateDir(
                    options.AcmeStateDir,
                    AppContext.BaseDirectory);
                var fqdn = CertificateIdentities.NormalizeFqdn(options.Fqdn);
                var domains = CertificateIdentities.ForFqdn(fqdn, options.IncludeNewsHostnameInCertificate);
                var renewalThreshold = TimeSpan.FromDays(options.AcmeRenewalThresholdDays);
                var accountStore = new AccountStore(stateDir);
                var certificateStore = new CertificateStore(
                    stateDir,
                    fqdn,
                    options.AcmeCertificatePassword,
                    domains,
                    renewalThreshold);
                var resolver = new AuthoritativeTxtResolver(
                    options.DnsSuffix,
                    _loggerFactory.CreateLogger<AuthoritativeTxtResolver>());
                var journal = new AcmeTransactionJournal(stateDir, fqdn);
                var dnsSolver = new Dns01Solver(
                    _cloudflareDnsClient,
                    options.CloudFlareZoneId,
                    resolver,
                    fqdn,
                    stateDir,
                    historyJournal: journal);
                var issuer = new AcmeIssuer(
                    _options,
                    accountStore,
                    dnsSolver,
                    _httpClientFactory,
                    _loggerFactory.CreateLogger<AcmeIssuer>(),
                    journal);
                _manager = new CertificateManager(
                    fqdn,
                    stateDir,
                    options.AcmeDirectoryUrl,
                    certificateStore,
                    issuer,
                    options.AcmeCertificatePassword,
                    renewalThreshold,
                    _loggerFactory.CreateLogger<CertificateManager>(),
                    options.IncludeNewsHostnameInCertificate,
                    journal);
                _provider = new ServerCertificateProvider(_manager);
                return _manager;
            }
        }

        /// <summary>
        /// Returns a certificate provider when TLS is enabled; otherwise an unavailable stub.
        /// </summary>
        /// <returns>
        /// <see cref="DisabledServerCertificateProvider.Instance"/> when TLS is disabled or the manager was not created.
        /// Otherwise the provider created with the manager.
        /// </returns>
        internal IServerCertificateProvider GetCertificateProvider()
        {
            if (!_options.Value.IsTlsListenerEnabled)
            {
                return DisabledServerCertificateProvider.Instance;
            }

            _ = GetOrCreateManager();
            return _provider ?? DisabledServerCertificateProvider.Instance;
        }
    }

    /// <summary>Stub provider used when TLS / ACME is disabled.</summary>
    internal sealed class DisabledServerCertificateProvider : IServerCertificateProvider
    {
        /// <summary>Process-wide stub returned when the TLS listener is disabled.</summary>
        internal static DisabledServerCertificateProvider Instance { get; } = new();

        /// <summary>Always <see langword="false"/>. No certificate is loaded while TLS is disabled.</summary>
        public bool IsAvailable => false;

        /// <summary>Always throws. TLS is disabled, so there is no certificate to return.</summary>
        /// <returns>This method does not return.</returns>
        /// <exception cref="InvalidOperationException">Always thrown.</exception>
        public System.Security.Cryptography.X509Certificates.X509Certificate2 GetCertificate() =>
            throw new InvalidOperationException("TLS is disabled; no server certificate is available.");
    }
}
