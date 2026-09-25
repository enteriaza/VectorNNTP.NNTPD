using Org.BouncyCastle.Bcpg.OpenPgp;

namespace VectorNNTP.NNTPCancelMessage.PgpVerify;

/// <summary>
/// Independently verifies a PGPVERIFY detached signature against a public key.
/// Used by tests; not a substitute for INN <c>pgpverify</c>.
/// </summary>
internal static class PgpVerifyVerifier
{
    public static bool TryVerify(string article, string publicKeyArmored, IReadOnlyList<string> headerNames)
    {
        ArgumentException.ThrowIfNullOrEmpty(article);
        ArgumentException.ThrowIfNullOrEmpty(publicKeyArmored);
        ArgumentNullException.ThrowIfNull(headerNames);

        if (!PgpVerifySigner.TryExtractSignatureBlock(article, out _, out var listed, out var armored))
        {
            return false;
        }

        var expected = string.Join(",", headerNames);
        if (!listed.Equals(expected, StringComparison.Ordinal))
        {
            return false;
        }

        var canonical = PgpVerifyCanonicalizer.CanonicalizeArticle(article, headerNames);
        return VerifyDetached(canonical, armored, publicKeyArmored);
    }

    public static bool VerifyDetached(byte[] canonical, string armoredSignature, string publicKeyArmored)
    {
        try
        {
            using var keyStream = PgpUtilities.GetDecoderStream(
                new MemoryStream(System.Text.Encoding.ASCII.GetBytes(publicKeyArmored)));
            var publicBundle = new PgpPublicKeyRingBundle(keyStream);
            using var sigStream = PgpUtilities.GetDecoderStream(
                new MemoryStream(System.Text.Encoding.ASCII.GetBytes(armoredSignature)));
            var factory = new PgpObjectFactory(sigStream);
            if (factory.NextPgpObject() is not PgpSignatureList list || list.Count == 0)
            {
                return false;
            }

            var signature = list[0];
            PgpPublicKey? key = null;
            foreach (PgpPublicKeyRing ring in publicBundle.GetKeyRings())
            {
                foreach (PgpPublicKey candidate in ring.GetPublicKeys())
                {
                    if (candidate.KeyId == signature.KeyId)
                    {
                        key = candidate;
                        break;
                    }
                }

                if (key is not null)
                {
                    break;
                }
            }

            if (key is null)
            {
                return false;
            }

            signature.InitVerify(key);
            signature.Update(canonical);
            return signature.Verify();
        }
        catch (Exception ex) when (ex is PgpException or IOException)
        {
            return false;
        }
    }
}
