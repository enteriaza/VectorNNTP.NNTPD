using VectorNNTP.NNTPD.Acme;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Supplies the Cache Listener TLS certificate from the shared ACME store.
/// </summary>
public sealed class AcmeCacheListenerCertificateSource : ICacheListenerCertificateSource
{
    private readonly IServerCertificateProvider _provider;

    /// <summary>Initializes a new ACME-backed source.</summary>
    public AcmeCacheListenerCertificateSource(IServerCertificateProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <inheritdoc />
    public bool TryGetCurrent(out CacheListenerCertificateMaterial material)
    {
        material = null!;
        if (!_provider.IsAvailable)
        {
            return false;
        }

        var certificate = _provider.GetCertificate();
        material = new CacheListenerCertificateMaterial(certificate);
        return true;
    }
}
