using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Loads <c>backfiller-listener.pfx</c> from the configured certificate directory.
/// ACME issuance and renewal are deferred; this type only reads already-provisioned material.
/// </summary>
public sealed class DirectoryCacheListenerCertificateSource : ICacheListenerCertificateSource
{
    private readonly string _pfxPath;
    private readonly string _password;

    /// <summary>Initializes a directory-backed source.</summary>
    public DirectoryCacheListenerCertificateSource(BackFillerRuntimeOptions runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _pfxPath = Path.Combine(runtime.CertificateDirectory, ListenerProtocol.ListenerPfxFileName);
        _password = runtime.CertificatePassword;
    }

    /// <inheritdoc />
    public bool TryGetCurrent(out CacheListenerCertificateMaterial material)
    {
        material = null!;
        if (!File.Exists(_pfxPath))
        {
            return false;
        }

        X509Certificate2? certificate = null;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                _pfxPath,
                _password,
                CacheListenerCertificateMaterial.TlsServerKeyStorageFlags);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException(
                    $"Cache Listener certificate '{_pfxPath}' does not contain a private key.");
            }

            material = new CacheListenerCertificateMaterial(certificate);
            return true;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            certificate?.Dispose();
            throw new InvalidOperationException(
                $"Cache Listener certificate '{_pfxPath}' could not be loaded. The file may be invalid, inaccessible, or the PKCS#12 password may be incorrect.",
                ex);
        }
        catch (Exception)
        {
            certificate?.Dispose();
            throw new InvalidOperationException(
                $"Cache Listener certificate '{_pfxPath}' could not be loaded. The file may be invalid, inaccessible, or the PKCS#12 password may be incorrect.");
        }
    }
}
