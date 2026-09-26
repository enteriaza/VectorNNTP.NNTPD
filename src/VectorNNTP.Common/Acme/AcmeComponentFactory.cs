using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Creates ACME components only when TLS is enabled so non-TLS startups never touch ACME state.
/// </summary>
public class AcmeComponentFactory
{
    private readonly IOptions<AcmeCloudflareOptions> _options;
    private readonly ICloudflareDnsClient _cloudflareDnsClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _sync = new();
    private CertificateManager? _manager;
    private IServerCertificateProvider? _provider;

    /// <summary>Initializes a new instance of the <see cref="AcmeComponentFactory"/> class.</summary>
    public AcmeComponentFactory(
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
    /// </summary>
    public virtual CertificateManager? GetOrCreateManager()
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

            var stateDir = Path.GetFullPath(options.AcmeStateDir.Trim());
            var domains = CertificateIdentities.ForFqdn(options.Fqdn, options.IncludeNewsHostnameInCertificate);
            var renewalThreshold = TimeSpan.FromDays(options.AcmeRenewalThresholdDays);
            var accountStore = new AccountStore(stateDir);
            var certificateStore = new CertificateStore(
                stateDir,
                options.AcmeCertificatePassword,
                domains,
                renewalThreshold);
            var resolver = new AuthoritativeTxtResolver(
                options.DnsSuffix,
                _loggerFactory.CreateLogger<AuthoritativeTxtResolver>());
            var dnsSolver = new Dns01Solver(
                _cloudflareDnsClient,
                options.CloudFlareZoneId,
                resolver,
                stateDir);
            var issuer = new CertesAcmeIssuer(
                _options,
                accountStore,
                dnsSolver,
                _httpClientFactory,
                _loggerFactory.CreateLogger<CertesAcmeIssuer>());
            _manager = new CertificateManager(
                options.Fqdn,
                certificateStore,
                issuer,
                options.AcmeCertificatePassword,
                renewalThreshold,
                _loggerFactory.CreateLogger<CertificateManager>(),
                options.IncludeNewsHostnameInCertificate);
            _provider = new ServerCertificateProvider(_manager);
            return _manager;
        }
    }

    /// <summary>
    /// Returns a certificate provider when TLS is enabled; otherwise an unavailable stub.
    /// </summary>
    public IServerCertificateProvider GetCertificateProvider()
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
    public static DisabledServerCertificateProvider Instance { get; } = new();

    public bool IsAvailable => false;

    public System.Security.Cryptography.X509Certificates.X509Certificate2 GetCertificate() =>
        throw new InvalidOperationException("TLS is disabled; no server certificate is available.");
}
