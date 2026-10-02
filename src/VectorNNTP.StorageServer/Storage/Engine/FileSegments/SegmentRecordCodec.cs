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

    private static readonly AsyncLocal<EncodeCallScope?> EncodeScope = new();

    /// <summary>
    /// Times <see cref="Encode"/> has been entered on this execution context since <see cref="ResetEncodeCalls"/>.
    /// Other tests do not affect the count. The successful production append path does not call <see cref="Encode"/>.
    /// </summary>
    internal static long EncodeCalls => EncodeScope.Value?.Count ?? 0;

    /// <summary>Starts or zeroes <see cref="EncodeCalls"/> for this execution context.</summary>
    internal static void ResetEncodeCalls()
    {
        var scope = EncodeScope.Value;
        if (scope is null)
        {
            scope = new EncodeCallScope();
            EncodeScope.Value = scope;
        }

        scope.Count = 0;
    }

    /// <summary>Computes on-disk record length for an article of <paramref name="artSize"/>.</summary>
    public static int RecordLengthForArtSize(int artSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(artSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(artSize, MaxArtDataBytes);
        return FixedHeaderLength + artSize + 4;
    }

    /// <summary>
    /// Encodes one physical record into a new buffer.
    /// Production append does not call this; it writes the header, the existing payload span, and the CRC.
    /// </summary>
    public static byte[] Encode(
        ArticleId artId,
        ulong artHash,
        ReadOnlySpan<byte> artData)
    {
        var encodeScope = EncodeScope.Value;
        if (encodeScope is not null)
        {
            encodeScope.Count++;
        }
        var artSize = artData.Length;
        if (artSize is < 1 or > MaxArtDataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(artData), "ArtData length out of range.");
        }

        var total = RecordLengthForArtSize(artSize);
        var allocStart = PhysicalProofProbe.MarkAppend();
        var buffer = new byte[total];
        PhysicalProofProbe.AddAppendEncodeAlloc(allocStart, total);
        WriteHeader(buffer.AsSpan(0, FixedHeaderLength), artId, artHash, artSize);
        var copyStart = PhysicalProofProbe.MarkAppend();
        artData.CopyTo(buffer.AsSpan(FixedHeaderLength, artSize));
        PhysicalProofProbe.AddAppendEncodeCopy(copyStart);
        var crcStart = PhysicalProofProbe.MarkAppend();
        var crc = Crc32.HashToUInt32(buffer.AsSpan(0, total - 4));
        PhysicalProofProbe.AddAppendEncodeCrc(crcStart);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(total - 4, 4), crc);
        return buffer;
    }

    /// <summary>
    /// Fills a 52-byte header and 4-byte CRC for one record.
    /// Does not copy the payload and does not allocate a payload buffer.
    /// <paramref name="framedHash"/> is XxHash3 over the header, payload, and CRC.
    /// </summary>
    internal static void PrepareProductionFrame(
        ArticleId artId,
        ulong artHash,
        ReadOnlySpan<byte> artData,
        Span<byte> header,
        Span<byte> crc,
        out ulong framedHash)
    {
        if (header.Length != FixedHeaderLength)
        {
            throw new ArgumentException("Header buffer must be the fixed header length.", nameof(header));
        }

        if (crc.Length != 4)
        {
            throw new ArgumentException("CRC buffer must be 4 bytes.", nameof(crc));
        }

        WriteHeader(header, artId, artHash, artData.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Checksum(header, artData));
        framedHash = FramedHash(header, artData, crc);
    }

    /// <summary>Writes the fixed record header, including the full-record length.</summary>
    internal static void WriteHeader(Span<byte> header, ArticleId artId, ulong artHash, int artSize)
    {
        if (header.Length != FixedHeaderLength)
        {
            throw new ArgumentException("Header buffer must be the fixed header length.", nameof(header));
        }

        var total = RecordLengthForArtSize(artSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)total);
        header[4] = SchemaVersion;
        header[5] = 0;
        header[6] = 0;
        header[7] = 0;
        artId.CopyTo(header.Slice(8, ArticleId.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(8 + ArticleId.Length, 8), artHash);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8 + ArticleId.Length + 8, 4), artSize);
    }

    /// <summary>CRC32 over the header and then the article payload, matching a contiguous record checksum.</summary>
    internal static uint Checksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> artData)
    {
        var crc = new Crc32();
        crc.Append(header);
        crc.Append(artData);
        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>XxHash3 over the header, payload, and CRC, matching a contiguous framed record.</summary>
    internal static ulong FramedHash(ReadOnlySpan<byte> header, ReadOnlySpan<byte> artData, ReadOnlySpan<byte> crc)
    {
        var hash = new XxHash3();
        hash.Append(header);
        hash.Append(artData);
        hash.Append(crc);
        return hash.GetCurrentHashAsUInt64();
    }

    private sealed class EncodeCallScope
    {
        public long Count { get; set; }
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
        var crcStart = PhysicalProofProbe.Mark();
        var actualCrc = Crc32.HashToUInt32(record[..(recordLength - 4)]);
        PhysicalProofProbe.AddCrc(crcStart);
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

        var headerStart = PhysicalProofProbe.Mark();
        artId = ArticleId.FromSpan(record.Slice(8, ArticleId.Length));
        artHash = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(8 + ArticleId.Length, 8));
        artSize = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8 + ArticleId.Length + 8, 4));
        var headerValid = artSize is >= 1 and <= MaxArtDataBytes && FixedHeaderLength + artSize + 4 == recordLength;
        PhysicalProofProbe.AddHeader(headerStart);
        if (!headerValid)
        {
            error = DecodeError.Corrupt;
            return false;
        }

        var copyStart = PhysicalProofProbe.Mark();
        var payload = record.Slice(FixedHeaderLength, artSize).ToArray();
        PhysicalProofProbe.AddCopy(copyStart, artSize);
        PhysicalProofProbe.PushInner();
        var proved = ArticleStorageIntegrity.TryProve(payload, artId, artHash, artSize);
        PhysicalProofProbe.PopLayer();
        if (!proved)
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

        var artIdStart = PhysicalProofProbe.Mark();
        if (expectedArtId is { } wantId && wantId != artId)
        {
            PhysicalProofProbe.AddArtIdCompare(artIdStart);
            return false;
        }

        PhysicalProofProbe.AddArtIdCompare(artIdStart);
        var artHashStart = PhysicalProofProbe.Mark();
        if (expectedArtHash is { } wantHash && wantHash != artHash)
        {
            PhysicalProofProbe.AddArtHashCompare(artHashStart);
            return false;
        }

        PhysicalProofProbe.AddArtHashCompare(artHashStart);
        var artSizeStart = PhysicalProofProbe.Mark();
        if (expectedArtSize is { } wantSize && wantSize != artSize)
        {
            PhysicalProofProbe.AddArtSizeCompare(artSizeStart);
            return false;
        }

        PhysicalProofProbe.AddArtSizeCompare(artSizeStart);

        return true;
    }

    /// <summary>
    /// Proves one complete physical record against the expected Accept identity.
    /// CRC, schema, header identity, XxHash3, and Message-ID each run once over
    /// <paramref name="record"/>. The payload is not copied.
    /// </summary>
    internal static bool TryProveExactRecord(
        ReadOnlySpan<byte> record,
        ArticleId expectedArtId,
        ulong expectedArtHash,
        int expectedArtSize)
    {
        if (record.Length < MinimumRecordLength || record.Length > MaxRecordLength)
        {
            return false;
        }

        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(record);
        if (declaredLength != (uint)record.Length)
        {
            return false;
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(record.Length - 4, 4));
        if (Crc32.HashToUInt32(record[..^4]) != expectedCrc)
        {
            return false;
        }

        if (record[4] != SchemaVersion || record[5] != 0 || record[6] != 0 || record[7] != 0)
        {
            return false;
        }

        var artId = ArticleId.FromSpan(record.Slice(8, ArticleId.Length));
        var artHash = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(8 + ArticleId.Length, 8));
        var artSize = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8 + ArticleId.Length + 8, 4));
        if (artSize is < 1 or > MaxArtDataBytes || FixedHeaderLength + artSize + 4 != record.Length)
        {
            return false;
        }

        if (artId != expectedArtId || artHash != expectedArtHash || artSize != expectedArtSize)
        {
            return false;
        }

        var payload = record.Slice(FixedHeaderLength, artSize);
        if (XxHash3.HashToUInt64(payload) != expectedArtHash)
        {
            return false;
        }

        if (!ArticleStorageIntegrity.TryExtractMessageIdValue(payload, out var messageId))
        {
            return false;
        }

        return ArticleId.FromMessageId(messageId) == expectedArtId;
    }
}
