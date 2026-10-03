using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>Leaf certificate RSA private key used to build the ACME finalize CSR and PKCS#12.</summary>
    internal sealed class AcmeCertificateKey : IDisposable
    {
        private readonly RSA _rsa;

        private AcmeCertificateKey(RSA rsa) => _rsa = rsa;

        /// <summary>Creates a new RSA leaf key (minimum 2048 bits; VectorNNTP issuance uses 2048).</summary>
        public static AcmeCertificateKey CreateRsa(int keySize = 2048)
        {
            if (keySize < 2048)
            {
                throw new ArgumentOutOfRangeException(nameof(keySize), "RSA keys must be at least 2048 bits.");
            }

            return new AcmeCertificateKey(RSA.Create(keySize));
        }

        /// <summary>Exports PKCS#8 PEM for pairing with a PEM certificate chain (BCL <c>CreateFromPem</c>).</summary>
        public string ExportPem() => _rsa.ExportPkcs8PrivateKeyPem();

        /// <summary>Creates a PKCS#10 certificate request for <paramref name="subject"/>.</summary>
        public CertificateRequest CreateRequest(X500DistinguishedName subject) =>
            new(subject, _rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        /// <inheritdoc />
        public void Dispose() => _rsa.Dispose();
    }
}
