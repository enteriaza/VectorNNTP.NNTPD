using System.Security.Cryptography;
using System.Text;

namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>
    /// ACME account private key (JWK thumbprint, JWS signing, DNS-01 key authorization).
    /// VectorNNTP persists account keys as PKCS#8 DER; use <see cref="ImportPkcs8Der"/>.
    /// </summary>
    internal sealed class AcmeAccountKey : IDisposable
    {
        private readonly RSA _rsa;
        private readonly HashAlgorithmName _hash = HashAlgorithmName.SHA256;

        private AcmeAccountKey(RSA rsa)
        {
            _rsa = rsa;
            SignatureAlgorithm = "RS256";

            RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: false);
            Jwk = $"{{\"e\":\"{Base64Url.Encode(parameters.Exponent!)}\",\"kty\":\"RSA\",\"n\":\"{Base64Url.Encode(parameters.Modulus!)}\"}}";
            Thumbprint = ComputeThumbprint(Jwk);
        }

        /// <summary>Gets the JWS <c>alg</c> value (<c>RS256</c>).</summary>
        public string SignatureAlgorithm { get; }

        /// <summary>Gets the canonical JWK JSON object used for thumbprints and newAccount.</summary>
        public string Jwk { get; }

        /// <summary>Gets the JWK thumbprint (base64url SHA-256 of <see cref="Jwk"/>).</summary>
        public string Thumbprint { get; }

        /// <summary>Creates a new RSA account key (minimum 2048 bits).</summary>
        public static AcmeAccountKey CreateRsa(int keySize = 2048)
        {
            if (keySize < 2048)
            {
                throw new ArgumentOutOfRangeException(nameof(keySize), "RSA keys must be at least 2048 bits.");
            }

            return new AcmeAccountKey(RSA.Create(keySize));
        }

        /// <summary>
        /// Imports a PKCS#8 DER RSA private key. Consumes the entire buffer; preserves key material losslessly.
        /// </summary>
        public static AcmeAccountKey ImportPkcs8Der(ReadOnlySpan<byte> pkcs8Der)
        {
            if (pkcs8Der.IsEmpty)
            {
                throw new CryptographicException("PKCS#8 DER account key is empty.");
            }

            RSA rsa = RSA.Create();
            try
            {
                rsa.ImportPkcs8PrivateKey(pkcs8Der, out int bytesRead);
                if (bytesRead != pkcs8Der.Length)
                {
                    throw new CryptographicException(
                        $"PKCS#8 import consumed {bytesRead} of {pkcs8Der.Length} bytes.");
                }

                return new AcmeAccountKey(rsa);
            }
            catch
            {
                rsa.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Proves DER → <see cref="AcmeAccountKey"/> → DER preserves RSA private key material (modulus and D).
        /// </summary>
        public static void AssertPkcs8DerRoundTripPreservesRsaMaterial(ReadOnlySpan<byte> pkcs8Der)
        {
            using var original = RSA.Create();
            original.ImportPkcs8PrivateKey(pkcs8Der, out _);
            RSAParameters before = original.ExportParameters(includePrivateParameters: true);

            using AcmeAccountKey key = ImportPkcs8Der(pkcs8Der);
            using var verify = RSA.Create();
            verify.ImportPkcs8PrivateKey(key.ExportPkcs8Der(), out _);
            RSAParameters after = verify.ExportParameters(includePrivateParameters: true);

            if (!before.Modulus!.AsSpan().SequenceEqual(after.Modulus!)
                || !before.D!.AsSpan().SequenceEqual(after.D!))
            {
                throw new CryptographicException("PKCS#8 DER import did not preserve RSA private key material.");
            }
        }

        /// <summary>Exports the private key as PKCS#8 DER.</summary>
        public byte[] ExportPkcs8Der() => _rsa.ExportPkcs8PrivateKey();

        /// <summary>Signs <paramref name="data"/> for JWS (RSA PKCS#1 v1.5 / SHA-256).</summary>
        public byte[] Sign(ReadOnlySpan<byte> data) =>
            _rsa.SignData(data.ToArray(), _hash, RSASignaturePadding.Pkcs1);

        /// <summary>Builds the ACME key authorization string for <paramref name="token"/>.</summary>
        public string GetKeyAuthorization(string token) => token + "." + Thumbprint;

        /// <summary>Computes the DNS-01 TXT value (base64url SHA-256 of the key authorization).</summary>
        public string GetDnsRecordValue(string token)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(GetKeyAuthorization(token)));
            return Base64Url.Encode(digest);
        }

        /// <inheritdoc />
        public void Dispose() => _rsa.Dispose();

        private static string ComputeThumbprint(string canonicalJwk) =>
            Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJwk)));
    }
}
