using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;

namespace VectorNNTP.NNTPAdmin.Tests;

/// <summary>Deterministic test-only OpenPGP keys. Not for production use.</summary>
internal static class TestPgpKeys
{
    public const string Passphrase = "unit-test-pgp-passphrase";

    public static TestKeyMaterial Shared { get; } = CreateRsa("VectorNNTP test <newsmaster@usenet.ninja>", seed: 1);

    public static TestKeyMaterial CreateRsa(string userId, int seed)
    {
        var random = new SecureRandom();
        random.SetSeed(BitConverter.GetBytes(seed));
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(random, 2048));
        var pair = generator.GenerateKeyPair();
        var pgpPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaSign, pair, DateTime.UtcNow);
        var secret = new PgpSecretKey(
            PgpSignature.DefaultCertification,
            pgpPair,
            userId,
            SymmetricKeyAlgorithmTag.Aes256,
            Passphrase.ToCharArray(),
            useSha1: true,
            hashedPackets: null,
            unhashedPackets: null,
            random);

        using var secretBuffer = new MemoryStream();
        using (var armored = new ArmoredOutputStream(secretBuffer))
        {
            secret.Encode(armored);
        }

        using var publicBuffer = new MemoryStream();
        using (var armored = new ArmoredOutputStream(publicBuffer))
        {
            secret.PublicKey.Encode(armored);
        }

        return new TestKeyMaterial(
            Encoding.ASCII.GetString(secretBuffer.ToArray()),
            Encoding.ASCII.GetString(publicBuffer.ToArray()),
            Convert.ToHexString(secret.PublicKey.GetFingerprint()),
            secret.KeyId.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
    }

    public static string WriteSecretFile(TestKeyMaterial key, string? extraSecret = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "vectornntp-pgp-" + Guid.NewGuid().ToString("N") + ".asc");
        if (extraSecret is null)
        {
            File.WriteAllText(path, key.SecretArmored);
        }
        else
        {
            using var buffer = new MemoryStream();
            EncodeRings(buffer, extraSecret);
            EncodeRings(buffer, key.SecretArmored);
            File.WriteAllBytes(path, buffer.ToArray());
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private static void EncodeRings(Stream destination, string armored)
    {
        using var decoder = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.ASCII.GetBytes(armored)));
        var bundle = new PgpSecretKeyRingBundle(decoder);
        foreach (PgpSecretKeyRing ring in bundle.GetKeyRings())
        {
            ring.Encode(destination);
        }
    }
}

public sealed record TestKeyMaterial(string SecretArmored, string PublicArmored, string Fingerprint, string KeyIdHex);

/// <summary>One temporary secret-key file shared by a test class.</summary>
public sealed class SharedPgpKeyFixture : IDisposable
{
    public SharedPgpKeyFixture()
    {
        Key = TestPgpKeys.Shared;
        Path = TestPgpKeys.WriteSecretFile(Key);
    }

    public TestKeyMaterial Key { get; }

    public string Path { get; }

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
