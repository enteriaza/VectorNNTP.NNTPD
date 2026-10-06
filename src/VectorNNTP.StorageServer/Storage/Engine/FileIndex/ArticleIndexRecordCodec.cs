using System.Buffers.Binary;
using System.IO.Hashing;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Engine.FileIndex;

/// <summary>Decode classification for durable article-index frames.</summary>
internal enum ArticleIndexFrameError
{
    None = 0,
    Incomplete = 1,
    CorruptChecksum = 2,
    CorruptLength = 3,
    Corrupt = 4,
}

/// <summary>
/// Append-only durable article-index framing (little-endian).
/// </summary>
/// <remarks>
/// Fixed-layout mutation record (latest write for an <see cref="ArticleId"/> wins on replay).
/// Each frame starts with <c>u32 TotalLength</c>. Schema 3 is 104 bytes:
/// <c>u8 SchemaVersion</c>, <c>u8 Reserved</c>, <c>u16 Reserved</c>,
/// ArtId[32], ArtHash u64, ArtSize i32, SegmentId u64, Offset i64, Length i32,
/// State u8, pad u8, pad u16, LastAccessUtcTicks i64, Sequence u64, AcceptedUtcTicks i64,
/// CRC-32 over preceding bytes. Schema 2 is the same prefix through Sequence and is 96 bytes;
/// it has no arrival instant and decodes as <see cref="DateTimeOffset.MinValue"/>.
/// Schema 1 records are 88 bytes and are rejected. Sequence is the journal Accept that established
/// the row. Relocation and death copy it; they do not allocate a new one.
/// AcceptedUtcTicks is that Accept's original UTC ticks. Relocation, eviction, and replay copy it.
/// </remarks>
internal static class ArticleIndexRecordCodec
{
    /// <summary>Current schema version written by <see cref="Encode"/>.</summary>
    public const byte SchemaVersion = 3;

    /// <summary>Schema 2 version. Those 96-byte frames are still read.</summary>
    public const byte Schema2Version = 2;

    /// <summary>Schema 2 encoded size, including CRC. Still accepted on replay.</summary>
    public const int Schema2RecordLength =
        4 + 1 + 1 + 2
        + ArticleId.Length
        + 8 + 4
        + 8 + 8 + 4
        + 1 + 1 + 2
        + 8
        + 8
        + 4;

    /// <summary>Current encoded mutation size including CRC. Schema 3 adds AcceptedUtcTicks.</summary>
    public const int RecordLength = Schema2RecordLength + 8;

    /// <summary>Shortest accepted frame (schema 2).</summary>
    public const int MinimumFrameLength = Schema2RecordLength;

    /// <summary>Longest accepted frame (schema 3).</summary>
    public const int MaxFrameLength = RecordLength;

    /// <summary>True when <paramref name="length"/> is a schema 2 or schema 3 frame.</summary>
    public static bool IsAcceptedFrameLength(uint length) =>
        length == Schema2RecordLength || length == RecordLength;

    /// <summary>Encodes one durable mutation snapshot for <paramref name="metadata"/>.</summary>
    public static byte[] Encode(in StoredArticleMetadata metadata)
    {
        ValidateMetadata(metadata);
        var buffer = new byte[RecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)RecordLength);
        buffer[4] = SchemaVersion;
        buffer[5] = 0;
        buffer[6] = 0;
        buffer[7] = 0;
        var o = 8;
        metadata.ArtId.CopyTo(buffer.AsSpan(o, ArticleId.Length));
        o += ArticleId.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), metadata.ArtHash);
        o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o, 4), metadata.ArtSize);
        o += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), metadata.Location.SegmentId.Value);
        o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(o, 8), metadata.Location.Offset);
        o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o, 4), metadata.Location.Length);
        o += 4;
        buffer[o++] = (byte)metadata.State;
        buffer[o++] = 0;
        buffer[o++] = 0;
        buffer[o++] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(o, 8), metadata.LastAccessUtc.UtcTicks);
        o += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), metadata.Sequence);
        o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(o, 8), metadata.AcceptedUtc.UtcTicks);
        o += 8;
        var crc = Crc32.HashToUInt32(buffer.AsSpan(0, o));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(o, 4), crc);
        return buffer;
    }

    /// <summary>Tries to decode one mutation frame at the start of <paramref name="span"/>.</summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> span,
        out int frameLength,
        out StoredArticleMetadata metadata,
        out ArticleIndexFrameError error)
    {
        frameLength = 0;
        metadata = default;
        error = ArticleIndexFrameError.None;

        if (span.Length < 4)
        {
            error = ArticleIndexFrameError.Incomplete;
            return false;
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (!IsAcceptedFrameLength(total))
        {
            frameLength = (int)Math.Min(total, int.MaxValue);
            error = ArticleIndexFrameError.CorruptLength;
            return false;
        }

        frameLength = (int)total;
        if (span.Length < frameLength)
        {
            error = ArticleIndexFrameError.Incomplete;
            return false;
        }

        var frame = span[..frameLength];
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(frameLength - 4, 4));
        var actualCrc = Crc32.HashToUInt32(frame[..(frameLength - 4)]);
        if (expectedCrc != actualCrc)
        {
            error = ArticleIndexFrameError.CorruptChecksum;
            return false;
        }

        var schema = frame[4];
        var schemaMatchesLength = (schema == SchemaVersion && frameLength == RecordLength)
            || (schema == Schema2Version && frameLength == Schema2RecordLength);
        if (!schemaMatchesLength || frame[5] != 0 || frame[6] != 0 || frame[7] != 0)
        {
            error = ArticleIndexFrameError.Corrupt;
            return false;
        }

        var o = 8;
        ArticleId artId;
        try
        {
            artId = ArticleId.FromSpan(frame.Slice(o, ArticleId.Length));
        }
        catch (ArgumentException)
        {
            error = ArticleIndexFrameError.Corrupt;
            return false;
        }

        o += ArticleId.Length;
        var artHash = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(o, 8));
        o += 8;
        var artSize = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(o, 4));
        o += 4;
        var segment = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(o, 8));
        o += 8;
        var offset = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(o, 8));
        o += 8;
        var length = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(o, 4));
        o += 4;
        var stateByte = frame[o++];
        if (frame[o++] != 0 || frame[o++] != 0 || frame[o++] != 0)
        {
            error = ArticleIndexFrameError.Corrupt;
            return false;
        }

        if (stateByte is not (
            (byte)ArticleStorageState.Present
            or (byte)ArticleStorageState.Evicted
            or (byte)ArticleStorageState.Invalid))
        {
            error = ArticleIndexFrameError.Corrupt;
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(o, 8));
        o += 8;
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(o, 8));
        o += 8;
        long acceptedTicks = 0;
        if (schema == SchemaVersion)
        {
            acceptedTicks = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(o, 8));
        }

        DateTimeOffset lastAccess;
        DateTimeOffset acceptedUtc = default;
        try
        {
            lastAccess = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (schema == SchemaVersion)
            {
                acceptedUtc = new DateTimeOffset(acceptedTicks, TimeSpan.Zero);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            error = ArticleIndexFrameError.Corrupt;
            return false;
        }

        if (artSize is < 0 or > ArticleResourceLimits.MaxArticleBytes
            || offset < 0
            || length < 0)
        {
            error = ArticleIndexFrameError.Corrupt;
            return false;
        }

        metadata = new StoredArticleMetadata(
            artId,
            artHash,
            artSize,
            new StoredArticleLocation(new SegmentId(segment), offset, length),
            (ArticleStorageState)stateByte,
            lastAccess,
            sequence,
            acceptedUtc);
        return true;
    }

    /// <summary>
    /// True when <c>[start, end)</c> is empty or a concatenation of complete schema 2 and schema 3 frames.
    /// Checksums are not verified. Restores <paramref name="stream"/> position.
    /// </summary>
    public static bool EndsOnFrameBoundary(Stream stream, long start, long end)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (end < start)
        {
            return false;
        }

        var restore = stream.Position;
        try
        {
            stream.Position = start;
            Span<byte> prefix = stackalloc byte[4];
            var offset = start;
            while (offset < end)
            {
                if (end - offset < 4 || !TryReadExact(stream, prefix))
                {
                    return false;
                }

                var declared = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
                if (!IsAcceptedFrameLength(declared) || end - offset < declared)
                {
                    return false;
                }

                stream.Position += declared - 4;
                offset += declared;
            }

            return true;
        }
        finally
        {
            stream.Position = restore;
        }
    }

    private static bool TryReadExact(Stream stream, Span<byte> buffer)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = stream.Read(buffer[filled..]);
            if (read == 0)
            {
                return false;
            }

            filled += read;
        }

        return true;
    }

    private static void ValidateMetadata(in StoredArticleMetadata metadata)
    {
        if (metadata.State is not (
            ArticleStorageState.Present
            or ArticleStorageState.Evicted
            or ArticleStorageState.Invalid))
        {
            throw new ArgumentOutOfRangeException(nameof(metadata), "Invalid ArticleStorageState.");
        }

        if (metadata.ArtSize is < 0 or > ArticleResourceLimits.MaxArticleBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata), "ArtSize out of range.");
        }

        if (metadata.Location.Offset < 0 || metadata.Location.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata), "Location Offset/Length invalid.");
        }
    }
}
