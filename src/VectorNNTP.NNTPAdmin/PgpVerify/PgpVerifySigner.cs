using System.Globalization;
using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Security;

namespace VectorNNTP.NNTPAdmin.PgpVerify;

/// <summary>
/// Loads an OpenPGP secret key and produces PGPVERIFY <c>X-PGP-Sig</c> detached signatures.
/// </summary>
/// <remarks>
/// FORMAT allows either clearsign parsing or a detached signature (<c>pgp -b</c> /
/// <c>gpg --detach-sign --armor</c>). INN <c>pgpverify</c> 1.24+ verifies the detached
/// form. This type emits that detached ASCII-armored signature, then places the
/// radix64 body into <c>X-PGP-Sig</c> exactly as FORMAT describes.
/// Secret material is never logged.
/// </remarks>
internal sealed class PgpVerifySigner : IDisposable
{
    private readonly PgpSecretKey _secretKey;
    private readonly PgpPrivateKey _privateKey;
    private readonly char[] _passphrase;

    private PgpVerifySigner(PgpSecretKey secretKey, PgpPrivateKey privateKey, char[] passphrase, PgpVerifySigningIdentity identity)
    {
        _secretKey = secretKey;
        _privateKey = privateKey;
        _passphrase = passphrase;
        Identity = identity;
    }

    public PgpVerifySigningIdentity Identity { get; }

    public static bool TryCreate(PgpOptions options, out PgpVerifySigner signer, out string error)
    {
        signer = null!;
        error = string.Empty;
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            error = "CANCEL requires PGP signing to be configured.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            error = "NntpAdmin:Pgp:PrivateKeyPath is required when PGP is enabled.";
            return false;
        }

        var path = options.PrivateKeyPath.Trim();
        if (!File.Exists(path))
        {
            error = "NntpAdmin:Pgp:PrivateKeyPath does not exist.";
            return false;
        }

        if (!TryValidateKeyFilePermissions(path, out error))
        {
            return false;
        }

        PgpSecretKeyRingBundle bundle;
        try
        {
            using var stream = File.OpenRead(path);
            using var decoder = PgpUtilities.GetDecoderStream(stream);
            bundle = new PgpSecretKeyRingBundle(decoder);
        }
        catch (IOException)
        {
            error = "NntpAdmin:Pgp:PrivateKeyPath could not be read or parsed as an OpenPGP secret key.";
            return false;
        }
        catch (PgpException)
        {
            error = "NntpAdmin:Pgp:PrivateKeyPath could not be parsed as an OpenPGP secret key.";
            return false;
        }

        var candidates = new List<PgpSecretKey>();
        foreach (PgpSecretKeyRing ring in bundle.GetKeyRings())
        {
            foreach (PgpSecretKey key in ring.GetSecretKeys())
            {
                if (key.IsSigningKey && !key.PublicKey.HasRevocation())
                {
                    candidates.Add(key);
                }
            }
        }

        if (candidates.Count == 0)
        {
            error = bundle.Count == 0
                ? "NntpAdmin:Pgp:PrivateKeyPath could not be parsed as an OpenPGP secret key."
                : "No signing-capable OpenPGP secret key was found.";
            return false;
        }

        PgpSecretKey selected;
        var wanted = (options.KeyId ?? string.Empty).Trim();
        if (wanted.Length == 0)
        {
            if (candidates.Count != 1)
            {
                error =
                    "NntpAdmin:Pgp:KeyId is required because the key file contains more than one signing-capable secret key.";
                return false;
            }

            selected = candidates[0];
        }
        else
        {
            selected = null!;
            foreach (var key in candidates)
            {
                if (KeyMatches(key, wanted))
                {
                    selected = key;
                    break;
                }
            }

            if (selected is null)
            {
                error = "NntpAdmin:Pgp:KeyId did not match a signing-capable secret key in the file.";
                return false;
            }
        }

        var passphrase = (options.PrivateKeyPassphrase ?? string.Empty).ToCharArray();
        PgpPrivateKey privateKey;
        try
        {
            privateKey = selected.ExtractPrivateKey(passphrase);
        }
        catch (PgpException)
        {
            Array.Clear(passphrase);
            error = "NntpAdmin:Pgp:PrivateKeyPassphrase did not unlock the selected secret key.";
            return false;
        }

        if (privateKey is null)
        {
            Array.Clear(passphrase);
            error = "NntpAdmin:Pgp:PrivateKeyPassphrase did not unlock the selected secret key.";
            return false;
        }

        signer = new PgpVerifySigner(selected, privateKey, passphrase, ReadIdentity(selected));
        return true;
    }

    public bool TrySignArticle(string unsignedArticle, out string signedArticle, out string error)
    {
        signedArticle = string.Empty;
        error = string.Empty;
        try
        {
            var parsed = PgpVerifyArticleParser.Parse(unsignedArticle);
            if (parsed.Headers.ContainsKey("X-PGP-Sig"))
            {
                error = "CANCEL article already contained X-PGP-Sig; the admin utility owns the signature.";
                return false;
            }

            var canonical = PgpVerifyCanonicalizer.Canonicalize(
                PgpVerifyCanonicalizer.CancelSignedHeaderNames,
                parsed.Headers,
                parsed.Body);
            var armor = CreateArmoredDetachedSignature(canonical);
            var header = FormatXPgpSig(armor);
            signedArticle = InsertHeader(unsignedArticle, header);
            return true;
        }
        catch (Exception ex) when (ex is PgpException or IOException or FormatException)
        {
            error = "PGPVERIFY signature generation failed.";
            return false;
        }
    }

    public void Dispose()
    {
        Array.Clear(_passphrase);
        GC.KeepAlive(_secretKey);
        GC.KeepAlive(_privateKey);
    }

    internal byte[] CreateArmoredDetachedSignature(byte[] canonical)
    {
        var generator = new PgpSignatureGenerator(_secretKey.PublicKey.Algorithm, HashAlgorithmTag.Sha256);
        generator.InitSign(PgpSignature.BinaryDocument, _privateKey);
        generator.Update(canonical);
        var signature = generator.Generate();

        using var buffer = new MemoryStream();
        using (var armored = new ArmoredOutputStream(buffer))
        {
            signature.Encode(armored);
        }

        return buffer.ToArray();
    }

    internal static string FormatXPgpSig(byte[] armoredSignature)
    {
        var text = Encoding.ASCII.GetString(armoredSignature).Replace("\r\n", "\n", StringComparison.Ordinal);
        var version = "BouncyCastle";
        var payload = new List<string>();
        var inBody = false;
        foreach (var raw in text.Split('\n'))
        {
            if (raw.StartsWith("-----BEGIN", StringComparison.Ordinal))
            {
                continue;
            }

            if (raw.StartsWith("-----END", StringComparison.Ordinal))
            {
                break;
            }

            if (raw.StartsWith("Version:", StringComparison.Ordinal))
            {
                version = raw["Version:".Length..].Trim().Replace(' ', '_');
                continue;
            }

            if (raw.StartsWith("Comment:", StringComparison.Ordinal) || raw.StartsWith("Hash:", StringComparison.Ordinal))
            {
                continue;
            }

            if (raw.Length == 0)
            {
                inBody = true;
                continue;
            }

            if (inBody || LooksLikeRadix64(raw))
            {
                inBody = true;
                payload.Add(raw.Trim());
            }
        }

        if (payload.Count == 0)
        {
            throw new PgpException("OpenPGP armor did not contain a signature body.");
        }

        var sb = new StringBuilder();
        sb.Append("X-PGP-Sig: ");
        sb.Append(version);
        sb.Append(' ');
        sb.Append(PgpVerifyCanonicalizer.CancelSignedHeaderList);
        sb.Append("\r\n");
        foreach (var line in payload)
        {
            sb.Append('\t');
            sb.Append(line);
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    internal static bool TryExtractSignatureBlock(string article, out string version, out string headerList, out string armoredBody)
    {
        version = string.Empty;
        headerList = string.Empty;
        armoredBody = string.Empty;
        var parsed = PgpVerifyArticleParser.Parse(article);
        if (!parsed.Headers.TryGetValue("X-PGP-Sig", out var raw) || raw.Length == 0)
        {
            return false;
        }

        var unfolded = raw.Replace("\n", " ", StringComparison.Ordinal).Replace('\t', ' ');
        var parts = unfolded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        version = parts[0];
        headerList = parts[1];
        var radix = new StringBuilder();
        foreach (var token in parts.Skip(2))
        {
            radix.Append(token);
            radix.Append('\n');
        }

        if (radix.Length == 0)
        {
            return false;
        }

        armoredBody =
            "-----BEGIN PGP SIGNATURE-----\r\n" +
            "Version: " + version.Replace('_', ' ') + "\r\n" +
            "\r\n" +
            radix.ToString().Replace("\n", "\r\n", StringComparison.Ordinal) +
            "-----END PGP SIGNATURE-----\r\n";
        return true;
    }

    private static string InsertHeader(string article, string headerBlock)
    {
        var separator = article.Contains("\r\n\r\n", StringComparison.Ordinal) ? "\r\n\r\n" : "\n\n";
        var index = article.IndexOf(separator, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new FormatException("Article has no header/body separator.");
        }

        return string.Concat(
            article[..index],
            "\r\n",
            headerBlock,
            "\r\n",
            article[(index + separator.Length)..]);
    }

    private static bool LooksLikeRadix64(string line)
    {
        if (line.Length == 0)
        {
            return false;
        }

        if (line[0] == '=' && line.Length == 5)
        {
            return true;
        }

        foreach (var c in line)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '=')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryValidateKeyFilePermissions(string path, out string error)
    {
        error = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode shared =
                UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute;
            if ((mode & shared) != 0)
            {
                error = "NntpAdmin:Pgp:PrivateKeyPath must not be group- or world-accessible.";
                return false;
            }
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
        {
            error = "NntpAdmin:Pgp:PrivateKeyPath permissions could not be verified.";
            return false;
        }

        return true;
    }

    private static bool KeyMatches(PgpSecretKey key, string wanted)
    {
        var normalized = wanted.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        var keyId = key.KeyId.ToString("X16", CultureInfo.InvariantCulture);
        if (normalized.Equals(keyId, StringComparison.OrdinalIgnoreCase)
            || keyId.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fingerprint = Convert.ToHexString(key.PublicKey.GetFingerprint());
        return fingerprint.Equals(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static PgpVerifySigningIdentity ReadIdentity(PgpSecretKey key)
    {
        string? userId = null;
        foreach (string id in key.PublicKey.GetUserIds())
        {
            userId = id;
            break;
        }

        return new PgpVerifySigningIdentity
        {
            Fingerprint = Convert.ToHexString(key.PublicKey.GetFingerprint()),
            UserId = userId,
            KeyIdHex = key.KeyId.ToString("X16", CultureInfo.InvariantCulture),
        };
    }
}
