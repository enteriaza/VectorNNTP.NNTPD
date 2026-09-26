namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Receives a newly ensured or renewed PKCS#12 credential for application TLS use.
/// </summary>
public interface IAcmeCertificatePublisher
{
    /// <summary>
    /// Publishes TLS material from validated PFX bytes.
    /// </summary>
    /// <param name="pfxBytes">PKCS#12 bytes. Never log.</param>
    /// <param name="password">PKCS#12 password. Never log.</param>
    void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password);
}
