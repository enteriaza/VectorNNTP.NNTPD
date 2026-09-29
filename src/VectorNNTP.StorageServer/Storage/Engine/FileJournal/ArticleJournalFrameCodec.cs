using System.Buffers.Binary;
using System.IO.Hashing;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Engine.FileJournal;

/// <summary>Binary frame type tags for the durable article journal.</summary>
internal enum ArticleJournalFrameType : byte
{
    Accept = 1,
    PhysicalWritten = 2,
    IndexCommitted = 3,

    /// <summary>
    /// Durable next-sequence watermark retained across checkpoint truncation.
    /// </summary>
    SequenceFence = 4,
}

/// <summary>Frame decode classification for open/replay.</summary>
internal enum ArticleJournalFrameError
{
    None = 0,
    Incomplete = 1,
    CorruptChecksum = 2,
    CorruptLength = 3,
    Corrupt = 4,
}

/// <summary>
/// Append-only Model A journal framing (little-endian).
/// </summary>
/// <remarks>
/// Frame layout:
/// <c>u32 TotalLength</c> (includes this length field and trailing CRC),
/// <c>u8 Type</c>, <c>u8 SchemaVersion</c>, <c>u16 Reserved=0</c>, body,
/// <c>u32 CRC-32</c> (<see cref="Crc32"/>) over all preceding frame bytes.
/// </remarks>
internal static class ArticleJournalFrameCodec
{
    /// <summary>Current body/schema version for all frame types.</summary>
    public const byte SchemaVersion = 1;

    /// <summary>Minimum frame size (header + empty body + CRC).</summary>
    public const int MinimumFrameLength = 4 + 1 + 1 + 2 + 4;

    /// <summary>Maximum Accept ArtData size.</summary>
    public const int MaxArtDataBytes = ArticleResourceLimits.MaxArticleBytes;

    /// <summary>Hard ceiling on a single frame length.</summary>
    public const int MaxFrameLength =
        4 + 1 + 1 + 2 + 8 + 8 + 8 + 4 + ArticleId.Length + MaxArtDataBytes + 4;

    private const int HeaderAfterLength = 1 + 1 + 2;

    /// <summary>Encodes an Accept frame.</summary>
    public static byte[] EncodeAccept(JournalAcceptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ArtSize != record.ArtData.Length)
        {
            throw new ArgumentException("ArtSize must equal ArtData.Length.", nameof(record));
        }

        if (record.ArtSize is < 1 or > MaxArtDataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "ArtData length out of range.");
        }

        var bodyLength = 8 + 8 + 8 + 4 + ArticleId.Length + record.ArtSize;
        var total = 4 + HeaderAfterLength + bodyLength + 4;
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        buffer[4] = (byte)ArticleJournalFrameType.Accept;
        buffer[5] = SchemaVersion;
        buffer[6] = 0;
        buffer[7] = 0;
        var o = 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), record.Sequence);
        o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(o, 8), record.AcceptedUtc.UtcTicks);
        o += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), record.ArtHash);
        o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o, 4), record.ArtSize);
        o += 4;
        record.ArtId.CopyTo(buffer.AsSpan(o, ArticleId.Length));
        o += ArticleId.Length;
        record.ArtData.Span.CopyTo(buffer.AsSpan(o, record.ArtSize));
        o += record.ArtSize;
        WriteCrc(buffer, o);
        return buffer;
    }

    /// <summary>Encodes a PhysicalWritten frame.</summary>
    public static byte[] EncodePhysicalWritten(in JournalPhysicalWrittenRecord record)
    {
        const int bodyLength = 8 + 8 + 8 + 4;
        var total = 4 + HeaderAfterLength + bodyLength + 4;
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        buffer[4] = (byte)ArticleJournalFrameType.PhysicalWritten;
        buffer[5] = SchemaVersion;
        buffer[6] = 0;
        buffer[7] = 0;
        var o = 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), record.Sequence);
        o += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), record.Location.SegmentId.Value);
        o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(o, 8), record.Location.Offset);
        o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o, 4), record.Location.Length);
        o += 4;
        WriteCrc(buffer, o);
        return buffer;
    }

    /// <summary>Encodes an IndexCommitted frame.</summary>
    public static byte[] EncodeIndexCommitted(in JournalIndexCommittedRecord record)
    {
        const int bodyLength = 8;
        var total = 4 + HeaderAfterLength + bodyLength + 4;
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        buffer[4] = (byte)ArticleJournalFrameType.IndexCommitted;
        buffer[5] = SchemaVersion;
        buffer[6] = 0;
        buffer[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), record.Sequence);
        WriteCrc(buffer, 16);
        return buffer;
    }

    /// <summary>Encodes a sequence fence (next sequence to allocate).</summary>
    public static byte[] EncodeSequenceFence(ulong nextSequence)
    {
        const int bodyLength = 8;
        var total = 4 + HeaderAfterLength + bodyLength + 4;
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        buffer[4] = (byte)ArticleJournalFrameType.SequenceFence;
        buffer[5] = SchemaVersion;
        buffer[6] = 0;
        buffer[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), nextSequence);
        WriteCrc(buffer, 16);
        return buffer;
    }

    /// <summary>Tries to parse one frame at the start of <paramref name="span"/>.</summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> span,
        out ArticleJournalFrameType type,
        out int frameLength,
        out JournalAcceptRecord? accept,
        out JournalPhysicalWrittenRecord? physicalWritten,
        out JournalIndexCommittedRecord? indexCommitted,
        out ulong? sequenceFence,
        out ArticleJournalFrameError error)
    {
        type = default;
        frameLength = 0;
        accept = null;
        physicalWritten = null;
        indexCommitted = null;
        sequenceFence = null;
        error = ArticleJournalFrameError.None;

        if (span.Length < 4)
        {
            error = ArticleJournalFrameError.Incomplete;
            return false;
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (total < MinimumFrameLength || total > MaxFrameLength)
        {
            frameLength = (int)Math.Min(total, int.MaxValue);
            error = ArticleJournalFrameError.CorruptLength;
            return false;
        }

        frameLength = (int)total;
        if (span.Length < frameLength)
        {
            error = ArticleJournalFrameError.Incomplete;
            return false;
        }

        var frame = span[..frameLength];
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(frameLength - 4, 4));
        var actualCrc = Crc32.HashToUInt32(frame[..(frameLength - 4)]);
        if (expectedCrc != actualCrc)
        {
            error = ArticleJournalFrameError.CorruptChecksum;
            return false;
        }

        type = (ArticleJournalFrameType)frame[4];
        var schema = frame[5];
        if (schema != SchemaVersion || frame[6] != 0 || frame[7] != 0)
        {
            error = ArticleJournalFrameError.Corrupt;
            return false;
        }

        var body = frame.Slice(8, frameLength - 8 - 4);
        switch (type)
        {
            case ArticleJournalFrameType.Accept:
                if (!TryDecodeAcceptBody(body, schema, out accept))
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                return true;
            case ArticleJournalFrameType.PhysicalWritten:
                if (!TryDecodePhysicalWrittenBody(body, schema, out var pw))
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                physicalWritten = pw;
                return true;
            case ArticleJournalFrameType.IndexCommitted:
                if (body.Length != 8)
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                indexCommitted = new JournalIndexCommittedRecord(
                    schema,
                    BinaryPrimitives.ReadUInt64LittleEndian(body));
                return true;
            case ArticleJournalFrameType.SequenceFence:
                if (body.Length != 8)
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                sequenceFence = BinaryPrimitives.ReadUInt64LittleEndian(body);
                return true;
            default:
                error = ArticleJournalFrameError.Corrupt;
                return false;
        }
    }

    private static bool TryDecodeAcceptBody(ReadOnlySpan<byte> body, byte schema, out JournalAcceptRecord? accept)
    {
        accept = null;
        if (body.Length < 8 + 8 + 8 + 4 + ArticleId.Length)
        {
            return false;
        }

        var o = 0;
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(o, 8));
        o += 8;
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(o, 8));
        o += 8;
        var artHash = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(o, 8));
        o += 8;
        var artSize = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4));
        o += 4;
        if (artSize is < 1 or > MaxArtDataBytes || body.Length != o + ArticleId.Length + artSize)
        {
            return false;
        }

        var artId = ArticleId.FromSpan(body.Slice(o, ArticleId.Length));
        o += ArticleId.Length;
        var artData = body.Slice(o, artSize).ToArray();
        accept = new JournalAcceptRecord(
            schema,
            sequence,
            artId,
            artHash,
            artSize,
            new DateTimeOffset(ticks, TimeSpan.Zero),
            artData);
        return true;
    }

    private static bool TryDecodePhysicalWrittenBody(
        ReadOnlySpan<byte> body,
        byte schema,
        out JournalPhysicalWrittenRecord record)
    {
        record = default;
        if (body.Length != 8 + 8 + 8 + 4)
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(body);
        var segment = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(8, 8));
        var offset = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(16, 8));
        var length = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(24, 4));
        if (length < 0 || offset < 0)
        {
            return false;
        }

        record = new JournalPhysicalWrittenRecord(
            schema,
            sequence,
            new StoredArticleLocation(new SegmentId(segment), offset, length));
        return true;
    }

    private static void WriteCrc(byte[] buffer, int crcOffset)
    {
        var crc = Crc32.HashToUInt32(buffer.AsSpan(0, crcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(crcOffset, 4), crc);
    }
}
