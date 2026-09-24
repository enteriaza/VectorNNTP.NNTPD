using System.Buffers;
using VectorNNTP.NNTPD.ArticleIngestion;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Discovers and classifies stored articles under <c>.artifacts/Articles</c>.
/// Does not modify the corpus. Classification uses the production classifier.
/// </summary>
internal static class IhaveCorpusCatalog
{
    internal static readonly byte[] LeftoverCommand = "DATE\r\n"u8.ToArray();

    internal static readonly int[] StreamingChunkBytes = [4 * 1024, 16 * 1024, 64 * 1024, 256 * 1024];

    public static string FindArticlesRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, ".artifacts", "Articles");
                if (Directory.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                dir = dir.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Corpus not found. Expected .artifacts/Articles under the repository root.");
    }

    public static IhaveCorpusInventory Load(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Corpus directory missing: {root}");
        }

        var paths = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        var articles = new IhaveCorpusArticle[paths.Length];
        var rootFull = Path.GetFullPath(root);
        for (var i = 0; i < paths.Length; i++)
        {
            articles[i] = Inspect(rootFull, paths[i]);
        }

        return new IhaveCorpusInventory(rootFull, articles);
    }

    public static byte[] ToWireArticle(ReadOnlySpan<byte> stored)
    {
        if (!NeedsDotStuff(stored))
        {
            var extraCrLf = stored.Length < 2 || stored[^2] != (byte)'\r' || stored[^1] != (byte)'\n';
            var framed = new byte[stored.Length + (extraCrLf ? 2 : 0) + 3];
            stored.CopyTo(framed);
            var offset = stored.Length;
            if (extraCrLf)
            {
                framed[offset++] = (byte)'\r';
                framed[offset++] = (byte)'\n';
            }

            framed[offset++] = (byte)'.';
            framed[offset++] = (byte)'\r';
            framed[offset] = (byte)'\n';
            return framed;
        }

        var writer = new ArrayBufferWriter<byte>(stored.Length + 8);
        var atLineStart = true;
        for (var i = 0; i < stored.Length; i++)
        {
            var b = stored[i];
            if (atLineStart && b == (byte)'.')
            {
                writer.GetSpan(1)[0] = (byte)'.';
                writer.Advance(1);
            }

            writer.GetSpan(1)[0] = b;
            writer.Advance(1);
            atLineStart = b == (byte)'\n' && i > 0 && stored[i - 1] == (byte)'\r';
        }

        if (stored.Length < 2 || stored[^2] != (byte)'\r' || stored[^1] != (byte)'\n')
        {
            "\r\n"u8.CopyTo(writer.GetSpan(2));
            writer.Advance(2);
        }

        ".\r\n"u8.CopyTo(writer.GetSpan(3));
        writer.Advance(3);
        return writer.WrittenSpan.ToArray();
    }

    private static bool NeedsDotStuff(ReadOnlySpan<byte> stored)
    {
        if (stored.Length > 0 && stored[0] == (byte)'.')
        {
            return true;
        }

        var needle = "\r\n."u8;
        return stored.IndexOf(needle) >= 0;
    }

    public static byte[] ToWireWithLeftover(ReadOnlySpan<byte> stored)
    {
        var article = ToWireArticle(stored);
        var framed = new byte[article.Length + LeftoverCommand.Length];
        article.CopyTo(framed, 0);
        LeftoverCommand.CopyTo(framed.AsSpan(article.Length));
        return framed;
    }

    private static IhaveCorpusArticle Inspect(string root, string path)
    {
        var info = new FileInfo(path);
        var size = checked((int)info.Length);
        var prefixLen = Math.Min(size, 16 * 1024);
        var prefix = new byte[prefixLen];
        using (var stream = File.OpenRead(path))
        {
            var read = stream.Read(prefix, 0, prefix.Length);
            if (read != prefix.Length)
            {
                prefix = prefix[..read];
            }
        }

        var headerEnd = IndexOf(prefix, "\r\n\r\n"u8);
        var headers = headerEnd >= 0 ? prefix.AsSpan(0, headerEnd + 2) : prefix.AsSpan();
        var bodyPrefix = headerEnd >= 0 && headerEnd + 4 <= prefix.Length
            ? prefix.AsSpan(headerEnd + 4)
            : ReadOnlySpan<byte>.Empty;
        var type = ArticleTypeClassifier.Classify(headers, bodyPrefix);
        var contentLength = -1;
        var bytesHeader = -1;
        ScanHeaderInts(headers, ref contentLength, ref bytesHeader);
        var yencSize = FirstYencSize(bodyPrefix);
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return new IhaveCorpusArticle(
            relative,
            path,
            size,
            headerEnd >= 0 ? headerEnd + 4 : -1,
            type,
            contentLength,
            yencSize,
            bytesHeader);
    }

    private static void ScanHeaderInts(ReadOnlySpan<byte> headers, ref int contentLength, ref int bytesHeader)
    {
        var offset = 0;
        while (offset < headers.Length)
        {
            var remaining = headers[offset..];
            var crlf = remaining.IndexOf("\r\n"u8);
            var line = crlf < 0 ? remaining : remaining[..crlf];
            offset += crlf < 0 ? remaining.Length : crlf + 2;
            var parsed = ArticleTypeClassifier.TryParseContentLength(line);
            if (parsed >= 0)
            {
                contentLength = parsed;
            }

            if (StartsWithFolded(line, "BYTES:"u8))
            {
                bytesHeader = ParseTrailingInt(line["BYTES:".Length..]);
            }
        }
    }

    private static int FirstYencSize(ReadOnlySpan<byte> bodyPrefix)
    {
        var offset = 0;
        while (offset < bodyPrefix.Length)
        {
            var remaining = bodyPrefix[offset..];
            var crlf = remaining.IndexOf("\r\n"u8);
            var line = crlf < 0 ? remaining : remaining[..crlf];
            offset += crlf < 0 ? remaining.Length : crlf + 2;
            var size = ArticleTypeClassifier.TryParseYencSize(line);
            if (size >= 0)
            {
                return size;
            }

            if (ArticleTypeClassifier.IsYencBegin(line))
            {
                return -1;
            }
        }

        return -1;
    }

    private static int ParseTrailingInt(ReadOnlySpan<byte> text)
    {
        var i = 0;
        while (i < text.Length && (text[i] == (byte)' ' || text[i] == (byte)'\t'))
        {
            i++;
        }

        if (i >= text.Length || text[i] < (byte)'0' || text[i] > (byte)'9')
        {
            return -1;
        }

        var value = 0;
        while (i < text.Length && text[i] >= (byte)'0' && text[i] <= (byte)'9')
        {
            var digit = text[i] - (byte)'0';
            if (value > (int.MaxValue - digit) / 10)
            {
                return -1;
            }

            value = (value * 10) + digit;
            i++;
        }

        return value;
    }

    private static bool StartsWithFolded(ReadOnlySpan<byte> value, ReadOnlySpan<byte> upperAscii)
    {
        if (value.Length < upperAscii.Length)
        {
            return false;
        }

        for (var i = 0; i < upperAscii.Length; i++)
        {
            var b = value[i];
            if (b is >= (byte)'a' and <= (byte)'z')
            {
                b = (byte)(b - 32);
            }

            if (b != upperAscii[i])
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return -1;
        }

        return haystack.IndexOf(needle);
    }
}

internal readonly record struct IhaveCorpusArticle(
    string RelativePath,
    string FullPath,
    int FileBytes,
    int HeaderBytes,
    ArticleType Type,
    int ContentLength,
    int YencSize,
    int BytesHeader);

internal sealed class IhaveCorpusInventory
{
    public IhaveCorpusInventory(string root, IhaveCorpusArticle[] articles)
    {
        Root = root;
        Articles = articles;
    }

    public string Root { get; }

    public IhaveCorpusArticle[] Articles { get; }

    public int Count => Articles.Length;

    public long TotalBytes => Articles.Sum(static a => (long)a.FileBytes);
}
