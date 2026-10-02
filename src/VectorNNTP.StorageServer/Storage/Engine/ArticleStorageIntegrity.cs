using System.Buffers.Binary;
using System.IO.Hashing;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>
/// Proves stored ArtData against authoritative <see cref="ArticleRecord"/> identity fields.
/// </summary>
/// <remarks>
/// Uses ArtId, ArtHash, ArtSize, and ArtData only. Does not introduce another checksum scheme.
/// </remarks>
public static class ArticleStorageIntegrity
{
    private static ReadOnlySpan<byte> MessageIdHeaderPrefix => "Message-ID:"u8;

    /// <summary>
    /// Returns whether <paramref name="artData"/> matches the expected identity and fingerprint.
    /// </summary>
    public static bool TryProve(
        ReadOnlySpan<byte> artData,
        ArticleId expectedArtId,
        ulong expectedArtHash,
        int expectedArtSize)
    {
        if (artData.Length != expectedArtSize)
        {
            return false;
        }

        var xxStart = PhysicalProofProbe.Mark();
        var icXx = IndexCommittedProbe.MarkLayer();
        var actualHash = XxHash3.HashToUInt64(artData);
        PhysicalProofProbe.AddXx(xxStart);
        IndexCommittedProbe.AddLayerXx(icXx);
        if (actualHash != expectedArtHash)
        {
            return false;
        }

        var messageStart = PhysicalProofProbe.Mark();
        var icMessage = IndexCommittedProbe.MarkLayer();
        var extracted = TryExtractMessageIdValue(artData, out var messageId);
        PhysicalProofProbe.AddMessageId(messageStart);
        IndexCommittedProbe.AddLayerMessage(icMessage);
        if (!extracted)
        {
            return false;
        }

        var blakeStart = PhysicalProofProbe.Mark();
        var icBlake = IndexCommittedProbe.MarkLayer();
        var computedId = ArticleId.FromMessageId(messageId);
        PhysicalProofProbe.AddBlake(blakeStart);
        IndexCommittedProbe.AddLayerBlake(icBlake);
        return computedId == expectedArtId;
    }

    /// <summary>
    /// Extracts the first Message-ID header value octets (including angle brackets) from
    /// canonical ArtData. Returns false when the header is absent or malformed.
    /// </summary>
    public static bool TryExtractMessageIdValue(ReadOnlySpan<byte> artData, out ReadOnlySpan<byte> messageIdValue)
    {
        messageIdValue = default;
        var offset = 0;
        while (offset < artData.Length)
        {
            var lineEnd = artData[offset..].IndexOf((byte)'\n');
            ReadOnlySpan<byte> line;
            if (lineEnd < 0)
            {
                line = artData[offset..];
                offset = artData.Length;
            }
            else
            {
                line = artData.Slice(offset, lineEnd);
                offset += lineEnd + 1;
            }

            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.IsEmpty)
            {
                return false;
            }

            if (StartsWithAsciiIgnoreCase(line, MessageIdHeaderPrefix))
            {
                var value = line[MessageIdHeaderPrefix.Length..];
                while (!value.IsEmpty && value[0] is (byte)' ' or (byte)'\t')
                {
                    value = value[1..];
                }

                if (value.IsEmpty)
                {
                    return false;
                }

                messageIdValue = value;
                return true;
            }
        }

        return false;
    }

    /// <summary>Encodes a compact telemetry record for Accept outcomes (version 1).</summary>
    public static byte[] EncodeAcceptTelemetryV1(
        ArticleAcceptOutcome outcome,
        ArticleId artId,
        int artSize,
        DateTimeOffset utcNow)
    {
        Span<byte> buffer = stackalloc byte[1 + 1 + ArticleId.Length + 4 + 8];
        buffer[0] = 1;
        buffer[1] = (byte)outcome;
        artId.CopyTo(buffer.Slice(2, ArticleId.Length));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(2 + ArticleId.Length, 4), artSize);
        BinaryPrimitives.WriteInt64LittleEndian(
            buffer.Slice(2 + ArticleId.Length + 4, 8),
            utcNow.UtcTicks);
        return buffer.ToArray();
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> prefix)
    {
        if (haystack.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            var a = haystack[i];
            var b = prefix[i];
            if (a == b)
            {
                continue;
            }

            if (a is >= (byte)'A' and <= (byte)'Z')
            {
                a = (byte)(a + 32);
            }

            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                b = (byte)(b + 32);
            }

            if (a != b)
            {
                return false;
            }
        }

        return true;
    }
}
