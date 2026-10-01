using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text.RegularExpressions;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Engine.FileSegments;

/// <summary>On-disk lifecycle suffix for segment files under CacheDir.</summary>
internal enum SegmentFileKind : byte
{
    Active = 1,
    Closed = 2,
    Retired = 3,
}

/// <summary>
/// Deterministic segment filenames: <c>seg-{SegmentId:D20}.{active|closed|retired}</c>.
/// </summary>
/// <remarks>
/// Twenty zero-padded decimal digits sort naturally by <see cref="SegmentId"/> and avoid
/// collisions. The lifecycle suffix is renamed on close/retire. SegmentId is never reused:
/// the journal SegmentIdFence is the durable high-water mark, including after the file is gone.
/// </remarks>
internal static partial class SegmentFileNames
{
    public const string Prefix = "seg-";
    public const string ActiveSuffix = ".active";
    public const string ClosedSuffix = ".closed";
    public const string RetiredSuffix = ".retired";

    /// <summary>Builds a segment file name for <paramref name="segmentId"/> and <paramref name="kind"/>.</summary>
    public static string Format(SegmentId segmentId, SegmentFileKind kind)
    {
        var id = segmentId.Value.ToString("D20", CultureInfo.InvariantCulture);
        return kind switch
        {
            SegmentFileKind.Active => Prefix + id + ActiveSuffix,
            SegmentFileKind.Closed => Prefix + id + ClosedSuffix,
            SegmentFileKind.Retired => Prefix + id + RetiredSuffix,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>Tries to parse a segment file name.</summary>
    public static bool TryParse(string fileName, out SegmentId segmentId, out SegmentFileKind kind)
    {
        segmentId = default;
        kind = default;
        var match = FileNameRegex().Match(fileName);
        if (!match.Success)
        {
            return false;
        }

        if (!ulong.TryParse(
                match.Groups[1].ValueSpan,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var id))
        {
            return false;
        }

        kind = match.Groups[2].ValueSpan switch
        {
            "active" => SegmentFileKind.Active,
            "closed" => SegmentFileKind.Closed,
            "retired" => SegmentFileKind.Retired,
            _ => default,
        };
        if (kind == default)
        {
            return false;
        }

        segmentId = new SegmentId(id);
        return true;
    }

    [GeneratedRegex(@"^seg-(\d{20})\.(active|closed|retired)$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();
}

/// <summary>Physical article record framing inside a segment file.</summary>
/// <remarks>
/// Little-endian layout:
/// <c>u32 TotalLength</c> (entire record including CRC),
/// <c>u8 SchemaVersion</c>, <c>u8 Flags=0</c>, <c>u16 Reserved=0</c>,
/// <c>ArticleId</c> (32), <c>u64 ArtHash</c>, <c>i32 ArtSize</c>,
/// ArtData, <c>u32 CRC-32</c> over all preceding record bytes.
/// <see cref="StoredArticleLocation.Offset"/> is the record start;
/// <see cref="StoredArticleLocation.Length"/> is <c>TotalLength</c> (full physical record).
/// </remarks>
internal static class SegmentRecordCodec
{
    public const byte SchemaVersion = 1;

    public const int FixedHeaderLength = 4 + 1 + 1 + 2 + ArticleId.Length + 8 + 4;

    public const int MinimumRecordLength = FixedHeaderLength + 1 + 4;

    public const int MaxArtDataBytes = ArticleResourceLimits.MaxArticleBytes;

    public const int MaxRecordLength = FixedHeaderLength + MaxArtDataBytes + 4;

    /// <summary>Computes on-disk record length for an article of <paramref name="artSize"/>.</summary>
    public static int RecordLengthForArtSize(int artSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(artSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(artSize, MaxArtDataBytes);
        return FixedHeaderLength + artSize + 4;
    }

    /// <summary>Encodes one physical record. Returns the buffer and copies identity out.</summary>
    public static byte[] Encode(
        ArticleId artId,
        ulong artHash,
        ReadOnlySpan<byte> artData)
    {
        var artSize = artData.Length;
        if (artSize is < 1 or > MaxArtDataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(artData), "ArtData length out of range.");
        }

        var total = RecordLengthForArtSize(artSize);
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        buffer[4] = SchemaVersion;
        buffer[5] = 0;
        buffer[6] = 0;
        buffer[7] = 0;
        artId.CopyTo(buffer.AsSpan(8, ArticleId.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8 + ArticleId.Length, 8), artHash);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8 + ArticleId.Length + 8, 4), artSize);
        artData.CopyTo(buffer.AsSpan(FixedHeaderLength, artSize));
        var crc = Crc32.HashToUInt32(buffer.AsSpan(0, total - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(total - 4, 4), crc);
        return buffer;
    }

    /// <summary>Decode error classification for active-tail repair vs fail-closed.</summary>
    public enum DecodeError
    {
        None = 0,
        Incomplete = 1,
        CorruptLength = 2,
        CorruptChecksum = 3,
        Corrupt = 4,
    }

    /// <summary>Tries to decode one record at the start of <paramref name="span"/>.</summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> span,
        out int recordLength,
        out ArticleId artId,
        out ulong artHash,
        out int artSize,
        out ReadOnlyMemory<byte> artData,
        out DecodeError error)
    {
        recordLength = 0;
        artId = default;
        artHash = 0;
        artSize = 0;
        artData = default;
        error = DecodeError.None;

        if (span.Length < 4)
        {
            error = DecodeError.Incomplete;
            return false;
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (total < MinimumRecordLength || total > MaxRecordLength)
        {
            recordLength = (int)Math.Min(total, int.MaxValue);
            error = DecodeError.CorruptLength;
            return false;
        }

        recordLength = (int)total;
        if (span.Length < recordLength)
        {
            error = DecodeError.Incomplete;
            return false;
        }

        var record = span[..recordLength];
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(recordLength - 4, 4));
        var actualCrc = Crc32.HashToUInt32(record[..(recordLength - 4)]);
        if (expectedCrc != actualCrc)
        {
            error = DecodeError.CorruptChecksum;
            return false;
        }

        if (record[4] != SchemaVersion || record[5] != 0 || record[6] != 0 || record[7] != 0)
        {
            error = DecodeError.Corrupt;
            return false;
        }

        artId = ArticleId.FromSpan(record.Slice(8, ArticleId.Length));
        artHash = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(8 + ArticleId.Length, 8));
        artSize = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8 + ArticleId.Length + 8, 4));
        if (artSize is < 1 or > MaxArtDataBytes || FixedHeaderLength + artSize + 4 != recordLength)
        {
            error = DecodeError.Corrupt;
            return false;
        }

        var payload = record.Slice(FixedHeaderLength, artSize).ToArray();
        if (!ArticleStorageIntegrity.TryProve(payload, artId, artHash, artSize))
        {
            error = DecodeError.Corrupt;
            return false;
        }

        artData = payload;
        return true;
    }

    /// <summary>
    /// Validates a previously located record against expected identity without requiring
    /// Message-ID re-parse when header fields already mismatch.
    /// </summary>
    public static bool TryValidateLocated(
        ReadOnlySpan<byte> recordBytes,
        ArticleId? expectedArtId,
        ulong? expectedArtHash,
        int? expectedArtSize,
        out ReadOnlyMemory<byte> artData)
    {
        artData = default;
        if (!TryDecode(
                recordBytes,
                out var recordLength,
                out var artId,
                out var artHash,
                out var artSize,
                out artData,
                out _))
        {
            return false;
        }

        if (recordLength != recordBytes.Length)
        {
            return false;
        }

        if (expectedArtId is { } wantId && wantId != artId)
        {
            return false;
        }

        if (expectedArtHash is { } wantHash && wantHash != artHash)
        {
            return false;
        }

        if (expectedArtSize is { } wantSize && wantSize != artSize)
        {
            return false;
        }

        return true;
    }
}
