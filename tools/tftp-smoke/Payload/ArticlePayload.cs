using System.Security.Cryptography;
using System.Text;

namespace VectorNNTP.TftpSmoke.Payload;

/// <summary>Deterministic article bodies for the benchmark. These bytes are not passed through the production article parser.</summary>
public static class ArticlePayload
{
    /// <summary>Planning average used by StorageServer capacity maths: 750 KiB.</summary>
    public const int DefaultBytes = 768_000;

    /// <summary>Fills <paramref name="size"/> bytes from a fixed xorshift64* stream. The same seed always returns the same bytes.</summary>
    public static byte[] CreatePseudorandom(int size, ulong seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (seed == 0)
        {
            seed = 0x9E3779B97F4A7C15UL;
        }

        var data = new byte[size];
        var state = seed;
        for (var offset = 0; offset < size;)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var mixed = state * 0x2545F4914F6CDD1DUL;
            var chunk = Math.Min(8, size - offset);
            for (var i = 0; i < chunk; i++)
            {
                data[offset++] = (byte)(mixed >> (56 - (i * 8)));
            }
        }

        return data;
    }

    /// <summary>
    /// Builds an article-shaped buffer of exactly <paramref name="size"/> bytes: a fixed ASCII header plus a pseudorandom body.
    /// It is not an <c>ArticleRecordFactory</c> article and is not evidence that NNTP would accept it.
    /// </summary>
    public static byte[] CreateArticle(int size, ulong seed)
    {
        var header = Encoding.ASCII.GetBytes(
            "Path: tftp.vectornntp.local\r\n" +
            "From: tftp@vectornntp.local\r\n" +
            "Newsgroups: misc.test\r\n" +
            "Subject: VectorNNTP TFTP prototype\r\n" +
            "Date: Thu, 01 Jan 2026 00:00:00 +0000\r\n" +
            "Message-ID: <tftp-" + seed.ToString("x16") + "@vectornntp.local>\r\n" +
            "\r\n");
        if (size < header.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Article-shaped payload must be at least the header length.");
        }

        var article = CreatePseudorandom(size, seed);
        header.CopyTo(article, 0);
        return article;
    }

    /// <summary>Lower-case hexadecimal SHA-256.</summary>
    public static string Sha256Hex(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
