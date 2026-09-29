using System.Security.Cryptography;
using VectorNNTP.NNTPD.Acme.Protocol;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Lossless PKCS#8 DER ↔ AutoHttps-style <see cref="AcmeKey"/> conversion for ACME account keys.
/// </summary>
/// <remarks>
/// On-disk format remains PKCS#8 DER. The vendored ACME stack imports PEM; conversion must preserve
/// exact RSA key material (modulus and private exponent).
/// </remarks>
internal static class AccountKeyCodec
{
    /// <summary>Imports a VectorNNTP PKCS#8 DER account key into an <see cref="AcmeKey"/>.</summary>
    public static AcmeKey ImportAcmeKeyFromPkcs8Der(ReadOnlySpan<byte> pkcs8Der)
    {
        if (pkcs8Der.IsEmpty)
        {
            throw new CryptographicException("PKCS#8 DER account key is empty.");
        }

        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(pkcs8Der, out int bytesRead);
        if (bytesRead != pkcs8Der.Length)
        {
            throw new CryptographicException(
                $"PKCS#8 import consumed {bytesRead} of {pkcs8Der.Length} bytes.");
        }

        // Transient PEM is required by the vendored ACME key type; DER remains the persisted form.
        return AcmeKey.ImportPem(rsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>
    /// Proves DER → RSA → PEM → <see cref="AcmeKey"/> preserves RSA private key material.
    /// </summary>
    public static void AssertDerPemRoundTripPreservesRsaMaterial(ReadOnlySpan<byte> pkcs8Der)
    {
        using var original = RSA.Create();
        original.ImportPkcs8PrivateKey(pkcs8Der, out _);
        RSAParameters before = original.ExportParameters(includePrivateParameters: true);

        using AcmeKey key = ImportAcmeKeyFromPkcs8Der(pkcs8Der);
        using var verify = RSA.Create();
        verify.ImportFromPem(key.ExportPem());
        RSAParameters after = verify.ExportParameters(includePrivateParameters: true);

        if (!before.Modulus!.AsSpan().SequenceEqual(after.Modulus!)
            || !before.D!.AsSpan().SequenceEqual(after.D!))
        {
            throw new CryptographicException("DER↔PEM conversion did not preserve RSA private key material.");
        }
    }
}
