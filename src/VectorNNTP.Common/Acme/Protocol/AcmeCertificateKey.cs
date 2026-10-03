using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>Leaf certificate RSA private key used to build the ACME finalize CSR and PKCS#12.</summary>
    internal sealed class AcmeCertificateKey : IDisposable
    {
        /// <summary>Leaf RSA key. Disposed by <see cref="Dispose"/>.</summary>
        private readonly RSA _rsa;

        /// <summary>Takes ownership of <paramref name="rsa"/>.</summary>
        /// <param name="rsa">RSA key this instance owns.</param>
        private AcmeCertificateKey(RSA rsa) => _rsa = rsa;

        /// <summary>Creates a new RSA leaf key (minimum 2048 bits; VectorNNTP issuance uses 2048).</summary>
        /// <param name="keySize">RSA modulus size in bits. Values below 2048 throw.</param>
        /// <returns>An owned leaf key. The caller disposes it.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="keySize"/> is below 2048.</exception>
        internal static AcmeCertificateKey CreateRsa(int keySize = 2048)
        {
            if (keySize < 2048)
            {
                throw new ArgumentOutOfRangeException(nameof(keySize), "RSA keys must be at least 2048 bits.");
            }

            return new AcmeCertificateKey(RSA.Create(keySize));
        }

        /// <summary>Exports PKCS#8 PEM for pairing with a PEM certificate chain (BCL <c>CreateFromPem</c>).</summary>
        /// <returns>PEM text of the private key.</returns>
        internal string ExportPem() => _rsa.ExportPkcs8PrivateKeyPem();

        /// <summary>Creates a PKCS#10 certificate request for <paramref name="subject"/>.</summary>
        /// <param name="subject">CSR subject. May be empty when the common name is longer than 64 characters.</param>
        /// <returns>An unsigned request builder using this key, SHA-256, and PKCS#1 padding.</returns>
        internal CertificateRequest CreateRequest(X500DistinguishedName subject) =>
            new(subject, _rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        /// <summary>Disposes the leaf RSA key.</summary>
        public void Dispose() => _rsa.Dispose();
    }
}
