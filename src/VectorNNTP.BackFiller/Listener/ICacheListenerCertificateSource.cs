using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Disposable server certificate used for one Listener TLS handshake or service lifetime.
/// </summary>
public sealed class CacheListenerCertificateMaterial : IDisposable
{
    private int _disposed;

    /// <summary>Initializes owned certificate material.</summary>
    public CacheListenerCertificateMaterial(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("Cache Listener TLS requires a certificate with a private key.");
        }

        Certificate = certificate;
        try
        {
            Context = SslStreamCertificateContext.Create(certificate, additionalCertificates: null, offline: true);
        }
        catch (Exception)
        {
            Context = null;
        }
    }

    /// <summary>
    /// Key storage flags that keep the private key usable by the platform TLS stack.
    /// Windows Schannel cannot authenticate with <see cref="X509KeyStorageFlags.EphemeralKeySet"/>.
    /// </summary>
    public static X509KeyStorageFlags TlsServerKeyStorageFlags { get; } =
        OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;

    /// <summary>Gets the server certificate. Never log this object or its private key.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>Gets the Schannel-ready server certificate context, when one could be built.</summary>
    public SslStreamCertificateContext? Context { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Certificate.Dispose();
    }
}

/// <summary>
/// Supplies the active Listener TLS certificate. This is not an ACME or renewal subsystem.
/// </summary>
public interface ICacheListenerCertificateSource
{
    /// <summary>
    /// Attempts to obtain the current server certificate.
    /// </summary>
    /// <param name="material">Owned material when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a usable certificate is available.</returns>
    bool TryGetCurrent(out CacheListenerCertificateMaterial material);
}
