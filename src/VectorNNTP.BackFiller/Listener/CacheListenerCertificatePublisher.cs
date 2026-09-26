using VectorNNTP.NNTPD.Acme;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// No-op publisher: ACME persist/load is owned by <see cref="IServerCertificateProvider"/>.
/// </summary>
/// <remarks>
/// The shared ACME service still invokes publish after issuance/renewal. The listener
/// reads the current store through <see cref="AcmeCacheListenerCertificateSource"/>.
/// </remarks>
public sealed class CacheListenerCertificatePublisher : IAcmeCertificatePublisher
{
    /// <inheritdoc />
    public void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        _ = pfxBytes;
    }
}
