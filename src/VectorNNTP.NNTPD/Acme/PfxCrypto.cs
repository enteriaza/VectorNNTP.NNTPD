using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// PKCS#12/PFX helpers for the TLS server credential.
/// </summary>
/// <remarks>
/// The Windows Certificate Store is never used. PFX passwords must not be logged.
/// </remarks>
public static class PfxCrypto
{
    private static readonly X509KeyStorageFlags LoadFlags =
        X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;

    /// <summary>
    /// Builds a password-protected PKCS#12 blob containing the leaf (with private key) and intermediates.
    /// </summary>
    public static byte[] ExportPfx(
        X509Certificate2 leafWithPrivateKey,
        IEnumerable<X509Certificate2> intermediateCertificates,
        string password)
    {
        ArgumentNullException.ThrowIfNull(leafWithPrivateKey);
        ArgumentNullException.ThrowIfNull(intermediateCertificates);
        ArgumentNullException.ThrowIfNull(password);

        if (!leafWithPrivateKey.HasPrivateKey)
        {
            throw new AcmeCertificateException("key_mismatch", "leaf_missing_private_key");
        }

        var collection = new X509Certificate2Collection { leafWithPrivateKey };
        foreach (var intermediate in intermediateCertificates)
        {
            ArgumentNullException.ThrowIfNull(intermediate);
            collection.Add(intermediate);
        }

        try
        {
            return collection.Export(X509ContentType.Pkcs12, password)
                   ?? throw new AcmeCertificateException("pfx_export_failed", "empty_export");
        }
        catch (AcmeCertificateException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            // Never include password or PFX bytes in the message.
            throw new AcmeCertificateException("pfx_export_failed", ex.GetType().Name);
        }
    }

    /// <summary>Loads a PKCS#12/PFX into an <see cref="X509Certificate2"/> with private key.</summary>
    public static X509Certificate2 LoadCertificate(ReadOnlySpan<byte> pfxBytes, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (pfxBytes.IsEmpty)
        {
            throw new AcmeCertificateException("malformed_pfx", "empty_pfx");
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12(pfxBytes, password, LoadFlags);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new AcmeCertificateException("malformed_pfx", ex.GetType().Name);
        }
    }

    /// <summary>Loads the full PKCS#12 collection (leaf + chain).</summary>
    public static X509Certificate2Collection LoadCollection(ReadOnlySpan<byte> pfxBytes, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (pfxBytes.IsEmpty)
        {
            throw new AcmeCertificateException("malformed_pfx", "empty_pfx");
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12Collection(pfxBytes, password, LoadFlags);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new AcmeCertificateException("malformed_pfx", ex.GetType().Name);
        }
    }
}
