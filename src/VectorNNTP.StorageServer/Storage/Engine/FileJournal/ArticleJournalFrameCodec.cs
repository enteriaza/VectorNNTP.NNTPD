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

    CompactionBegin = 5,
    RelocationIntent = 6,
    RelocationWritten = 7,
    CompactionCommitted = 8,
    CompactionRetired = 9,
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

/// <summary>Decoded body payload for one journal frame (Accept-path or compaction).</summary>
internal readonly struct ArticleJournalDecodedFrame
{
    public ArticleJournalFrameType Type { get; init; }

    public JournalAcceptRecord? Accept { get; init; }

    public JournalPhysicalWrittenRecord? PhysicalWritten { get; init; }

    public JournalIndexCommittedRecord? IndexCommitted { get; init; }

    public ulong? SequenceFence { get; init; }

    public JournalCompactionBeginRecord? CompactionBegin { get; init; }

    public JournalRelocationIntentRecord? RelocationIntent { get; init; }

    public JournalRelocationWrittenRecord? RelocationWritten { get; init; }

    public JournalCompactionCommittedRecord? CompactionCommitted { get; init; }

    public JournalCompactionRetiredRecord? CompactionRetired { get; init; }
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

    /// <summary>Encodes a CompactionBegin frame.</summary>
    public static byte[] EncodeCompactionBegin(in JournalCompactionBeginRecord record)
    {
        if (record.CompactionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "CompactionId must be non-zero.");
        }

        const int bodyLength = 8 + 8 + 8;
        var total = 4 + HeaderAfterLength + bodyLength + 4;
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        buffer[4] = (byte)ArticleJournalFrameType.CompactionBegin;
        buffer[5] = SchemaVersion;
        buffer[6] = 0;
        buffer[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), record.CompactionId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16, 8), record.SourceSegmentId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24, 8), record.SourceGeneration);
        WriteCrc(buffer, 32);
        return buffer;
    }

    /// <summary>Encodes a RelocationIntent frame.</summary>
    public static byte[] EncodeRelocationIntent(in JournalRelocationIntentRecord record)
    {
        if (record.CompactionId == 0 || record.RelocationId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "CompactionId and RelocationId must be non-zero.");
        }

        if (record.ArtSize is < 1 or > MaxArtDataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "ArtSize out of range.");
        }

        if (record.ExpectedSourceLocation.Offset < 0 || record.ExpectedSourceLocation.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "Invalid source location.");
        }

        var bodyLength = 8 + 8 + 8 + 4 + 8 + 8 + 4 + ArticleId.Length;
        var total = 4 + HeaderAfterLength + bodyLength + 4;
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        buffer[4] = (byte)ArticleJournalFrameType.RelocationIntent;
        buffer[5] = SchemaVersion;
        buffer[6] = 0;
        buffer[7] = 0;
        var o = 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), record.CompactionId);
        o += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), record.RelocationId);
        o += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), record.ArtHash);
        o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o, 4), record.ArtSize);
        o += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), record.ExpectedSourceLocation.SegmentId.Value);
        o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(o, 8), record.ExpectedSourceLocation.Offset);
        o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o, 4), record.ExpectedSourceLocation.Length);
        o += 4;
        record.ArtId.CopyTo(buffer.AsSpan(o, ArticleId.Length));
        o += ArticleId.Length;
        WriteCrc(buffer, o);
        return buffer;
    }

    /// <summary>Encodes a RelocationWritten frame.</summary>
    public static byte[] EncodeRelocationWritten(in JournalRelocationWrittenRecord record)
    {
        if (record.CompactionId == 0 || record.RelocationId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "CompactionId and RelocationId must be non-zero.");
        }

        if (record.DestinationLocation.Offset < 0 || record.DestinationLocation.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "Invalid destination location.");
        }

        const int bodyLength = 8 + 8 + 8 + 8 + 4;
        var total = 4 + HeaderAfterLength + bodyLength + 4;
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        buffer[4] = (byte)ArticleJournalFrameType.RelocationWritten;
        buffer[5] = SchemaVersion;
        buffer[6] = 0;
        buffer[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), record.CompactionId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16, 8), record.RelocationId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24, 8), record.DestinationLocation.SegmentId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32, 8), record.DestinationLocation.Offset);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40, 4), record.DestinationLocation.Length);
        WriteCrc(buffer, 44);
        return buffer;
    }

    /// <summary>Encodes a CompactionCommitted frame.</summary>
    public static byte[] EncodeCompactionCommitted(in JournalCompactionCommittedRecord record)
    {
        if (record.CompactionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "CompactionId must be non-zero.");
        }

        const int bodyLength = 8;
        var total = 4 + HeaderAfterLength + bodyLength + 4;
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        buffer[4] = (byte)ArticleJournalFrameType.CompactionCommitted;
        buffer[5] = SchemaVersion;
        buffer[6] = 0;
        buffer[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), record.CompactionId);
        WriteCrc(buffer, 16);
        return buffer;
    }

    /// <summary>Encodes a CompactionRetired frame.</summary>
    public static byte[] EncodeCompactionRetired(in JournalCompactionRetiredRecord record)
    {
        if (record.CompactionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "CompactionId must be non-zero.");
        }

        const int bodyLength = 8 + 8 + 8;
        var total = 4 + HeaderAfterLength + bodyLength + 4;
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)total);
        buffer[4] = (byte)ArticleJournalFrameType.CompactionRetired;
        buffer[5] = SchemaVersion;
        buffer[6] = 0;
        buffer[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), record.CompactionId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16, 8), record.SourceSegmentId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24, 8), record.ExpectedGeneration);
        WriteCrc(buffer, 32);
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
        out ArticleJournalFrameError error) =>
        TryDecode(
            span,
            out type,
            out frameLength,
            out var decoded,
            out error)
            ? AssignLegacy(decoded, out accept, out physicalWritten, out indexCommitted, out sequenceFence)
            : ClearLegacy(out accept, out physicalWritten, out indexCommitted, out sequenceFence, false);

    /// <summary>Tries to parse one frame including compaction payloads.</summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> span,
        out ArticleJournalFrameType type,
        out int frameLength,
        out ArticleJournalDecodedFrame decoded,
        out ArticleJournalFrameError error)
    {
        type = default;
        frameLength = 0;
        decoded = default;
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
                if (!TryDecodeAcceptBody(body, schema, out var accept))
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                decoded = new ArticleJournalDecodedFrame { Type = type, Accept = accept };
                return true;
            case ArticleJournalFrameType.PhysicalWritten:
                if (!TryDecodePhysicalWrittenBody(body, schema, out var pw))
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                decoded = new ArticleJournalDecodedFrame { Type = type, PhysicalWritten = pw };
                return true;
            case ArticleJournalFrameType.IndexCommitted:
                if (body.Length != 8)
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                decoded = new ArticleJournalDecodedFrame
                {
                    Type = type,
                    IndexCommitted = new JournalIndexCommittedRecord(
                        schema,
                        BinaryPrimitives.ReadUInt64LittleEndian(body)),
                };
                return true;
            case ArticleJournalFrameType.SequenceFence:
                if (body.Length != 8)
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                decoded = new ArticleJournalDecodedFrame
                {
                    Type = type,
                    SequenceFence = BinaryPrimitives.ReadUInt64LittleEndian(body),
                };
                return true;
            case ArticleJournalFrameType.CompactionBegin:
                if (!TryDecodeCompactionBeginBody(body, schema, out var begin))
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                decoded = new ArticleJournalDecodedFrame { Type = type, CompactionBegin = begin };
                return true;
            case ArticleJournalFrameType.RelocationIntent:
                if (!TryDecodeRelocationIntentBody(body, schema, out var intent))
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                decoded = new ArticleJournalDecodedFrame { Type = type, RelocationIntent = intent };
                return true;
            case ArticleJournalFrameType.RelocationWritten:
                if (!TryDecodeRelocationWrittenBody(body, schema, out var written))
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                decoded = new ArticleJournalDecodedFrame { Type = type, RelocationWritten = written };
                return true;
            case ArticleJournalFrameType.CompactionCommitted:
                if (body.Length != 8)
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                var committedId = BinaryPrimitives.ReadUInt64LittleEndian(body);
                if (committedId == 0)
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                decoded = new ArticleJournalDecodedFrame
                {
                    Type = type,
                    CompactionCommitted = new JournalCompactionCommittedRecord(schema, committedId),
                };
                return true;
            case ArticleJournalFrameType.CompactionRetired:
                if (!TryDecodeCompactionRetiredBody(body, schema, out var retired))
                {
                    error = ArticleJournalFrameError.Corrupt;
                    return false;
                }

                decoded = new ArticleJournalDecodedFrame { Type = type, CompactionRetired = retired };
                return true;
            default:
                error = ArticleJournalFrameError.Corrupt;
                return false;
        }
    }

    private static bool AssignLegacy(
        ArticleJournalDecodedFrame decoded,
        out JournalAcceptRecord? accept,
        out JournalPhysicalWrittenRecord? physicalWritten,
        out JournalIndexCommittedRecord? indexCommitted,
        out ulong? sequenceFence)
    {
        accept = decoded.Accept;
        physicalWritten = decoded.PhysicalWritten;
        indexCommitted = decoded.IndexCommitted;
        sequenceFence = decoded.SequenceFence;
        return true;
    }

    private static bool ClearLegacy(
        out JournalAcceptRecord? accept,
        out JournalPhysicalWrittenRecord? physicalWritten,
        out JournalIndexCommittedRecord? indexCommitted,
        out ulong? sequenceFence,
        bool result)
    {
        accept = null;
        physicalWritten = null;
        indexCommitted = null;
        sequenceFence = null;
        return result;
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

    private static bool TryDecodeCompactionBeginBody(
        ReadOnlySpan<byte> body,
        byte schema,
        out JournalCompactionBeginRecord record)
    {
        record = default;
        if (body.Length != 24)
        {
            return false;
        }

        var id = BinaryPrimitives.ReadUInt64LittleEndian(body);
        var segment = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(8, 8));
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(16, 8));
        if (id == 0)
        {
            return false;
        }

        record = new JournalCompactionBeginRecord(schema, id, new SegmentId(segment), generation);
        return true;
    }

    private static bool TryDecodeRelocationIntentBody(
        ReadOnlySpan<byte> body,
        byte schema,
        out JournalRelocationIntentRecord record)
    {
        record = default;
        var expected = 8 + 8 + 8 + 4 + 8 + 8 + 4 + ArticleId.Length;
        if (body.Length != expected)
        {
            return false;
        }

        var o = 0;
        var compactionId = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(o, 8));
        o += 8;
        var relocationId = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(o, 8));
        o += 8;
        var artHash = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(o, 8));
        o += 8;
        var artSize = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4));
        o += 4;
        var segment = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(o, 8));
        o += 8;
        var offset = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(o, 8));
        o += 8;
        var length = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4));
        o += 4;
        if (compactionId == 0 || relocationId == 0 || artSize is < 1 or > MaxArtDataBytes || offset < 0 || length < 0)
        {
            return false;
        }

        var artId = ArticleId.FromSpan(body.Slice(o, ArticleId.Length));
        record = new JournalRelocationIntentRecord(
            schema,
            compactionId,
            relocationId,
            artId,
            artHash,
            artSize,
            new StoredArticleLocation(new SegmentId(segment), offset, length));
        return true;
    }

    private static bool TryDecodeRelocationWrittenBody(
        ReadOnlySpan<byte> body,
        byte schema,
        out JournalRelocationWrittenRecord record)
    {
        record = default;
        if (body.Length != 36)
        {
            return false;
        }

        var compactionId = BinaryPrimitives.ReadUInt64LittleEndian(body);
        var relocationId = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(8, 8));
        var segment = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(16, 8));
        var offset = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(24, 8));
        var length = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(32, 4));
        if (compactionId == 0 || relocationId == 0 || offset < 0 || length < 0)
        {
            return false;
        }

        record = new JournalRelocationWrittenRecord(
            schema,
            compactionId,
            relocationId,
            new StoredArticleLocation(new SegmentId(segment), offset, length));
        return true;
    }

    private static bool TryDecodeCompactionRetiredBody(
        ReadOnlySpan<byte> body,
        byte schema,
        out JournalCompactionRetiredRecord record)
    {
        record = default;
        if (body.Length != 24)
        {
            return false;
        }

        var id = BinaryPrimitives.ReadUInt64LittleEndian(body);
        var segment = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(8, 8));
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(16, 8));
        if (id == 0)
        {
            return false;
        }

        record = new JournalCompactionRetiredRecord(schema, id, new SegmentId(segment), generation);
        return true;
    }

    private static void WriteCrc(byte[] buffer, int crcOffset)
    {
        var crc = Crc32.HashToUInt32(buffer.AsSpan(0, crcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(crcOffset, 4), crc);
    }
}
