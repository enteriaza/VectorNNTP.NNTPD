using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VectorNNTP.Common.Acme
{
    /// <summary>
    /// PKCS#12/PFX helpers for the TLS server credential.
    /// </summary>
    /// <remarks>
    /// The Windows Certificate Store is never used. PFX passwords must not be logged.
    /// </remarks>
    internal static class PfxCrypto
    {
        /// <summary>Load flags: ephemeral key set, and exportable so the leaf can be re-exported into a PFX.</summary>
        private static readonly X509KeyStorageFlags LoadFlags =
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;

        /// <summary>
        /// Builds a password-protected PKCS#12 blob containing the leaf (with private key) and intermediates.
        /// </summary>
        /// <param name="leafWithPrivateKey">Leaf that already has a private key. Not disposed.</param>
        /// <param name="intermediateCertificates">Chain certificates appended after the leaf. Not disposed.</param>
        /// <param name="password">PFX password. Not included in exception text.</param>
        /// <returns>The PKCS#12 bytes.</returns>
        /// <exception cref="AcmeCertificateException">Thrown when the leaf has no private key or the export fails or is empty.</exception>
        internal static byte[] ExportPfx(
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
        /// <param name="pfxBytes">PFX bytes. Empty input fails.</param>
        /// <param name="password">PFX password. Not included in exception text.</param>
        /// <returns>The loaded certificate. The caller owns disposal.</returns>
        /// <exception cref="AcmeCertificateException">Thrown when the bytes are empty or the load fails. Category <c>malformed_pfx</c>.</exception>
        internal static X509Certificate2 LoadCertificate(ReadOnlySpan<byte> pfxBytes, string password)
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
        /// <param name="pfxBytes">PFX bytes. Empty input fails.</param>
        /// <param name="password">PFX password. Not included in exception text.</param>
        /// <returns>The certificate collection. The caller owns disposal of its certificates.</returns>
        /// <exception cref="AcmeCertificateException">Thrown when the bytes are empty or the load fails. Category <c>malformed_pfx</c>.</exception>
        private static X509Certificate2Collection LoadCollection(ReadOnlySpan<byte> pfxBytes, string password)
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
}
