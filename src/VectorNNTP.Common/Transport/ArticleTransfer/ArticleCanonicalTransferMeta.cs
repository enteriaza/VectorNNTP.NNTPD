using System.Buffers.Binary;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.Common.Transport.ArticleTransfer;

/// <summary>
/// Fixed 76-byte VATP META fields describing a CanonicalV1 ArtData buffer.
/// </summary>
/// <remarks>
/// Does not carry ArtId, ArtType, CanonicalUtc, ParseStatus, or a header/body split.
/// ArtId is verified from OPEN + Message-ID. ArtType and CanonicalUtc are derived
/// by <see cref="ArticleRecordFactory.TryCreateFromCanonicalTransfer"/>.
/// </remarks>
public readonly struct ArticleCanonicalTransferMeta : IEquatable<ArticleCanonicalTransferMeta>
{
    /// <summary>Initializes META fields.</summary>
    public ArticleCanonicalTransferMeta(
        ulong artHash,
        int artLines,
        int artSize,
        NntpArticleHeaderName selectedDateHeaderName,
        ArticleFieldTable fields)
    {
        ArtHash = artHash;
        ArtLines = artLines;
        ArtSize = artSize;
        SelectedDateHeaderName = selectedDateHeaderName;
        Fields = fields;
    }

    /// <summary>Gets XXH3-64 of ArtData.</summary>
    public ulong ArtHash { get; }

    /// <summary>Gets body line count for overview <c>:lines</c>.</summary>
    public int ArtLines { get; }

    /// <summary>Gets advertised ArtData length.</summary>
    public int ArtSize { get; }

    /// <summary>Gets the Date-family header name used by <see cref="ArticleFieldTable.Locate"/>.</summary>
    public NntpArticleHeaderName SelectedDateHeaderName { get; }

    /// <summary>Gets the seven header-value ranges into ArtData.</summary>
    public ArticleFieldTable Fields { get; }

    /// <summary>Builds META from a live CanonicalV1 record and its selected Date header name.</summary>
    public static ArticleCanonicalTransferMeta FromRecord(
        in ArticleRecord record,
        NntpArticleHeaderName selectedDateHeaderName)
    {
        if (record.ParseStatus != ArticleParseStatus.CanonicalV1)
        {
            throw new ArgumentException("Record must be CanonicalV1.", nameof(record));
        }

        return new ArticleCanonicalTransferMeta(
            record.ArtHash,
            record.ArtLines,
            record.ArtSize,
            selectedDateHeaderName,
            record.Fields);
    }

    /// <inheritdoc />
    public bool Equals(ArticleCanonicalTransferMeta other) =>
        ArtHash == other.ArtHash
        && ArtLines == other.ArtLines
        && ArtSize == other.ArtSize
        && SelectedDateHeaderName == other.SelectedDateHeaderName
        && Fields.Equals(other.Fields);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ArticleCanonicalTransferMeta other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(ArtHash, ArtLines, ArtSize, SelectedDateHeaderName, Fields);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ArticleCanonicalTransferMeta left, ArticleCanonicalTransferMeta right) =>
        left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ArticleCanonicalTransferMeta left, ArticleCanonicalTransferMeta right) =>
        !left.Equals(right);
}

/// <summary>
/// Big-endian codec for the frozen 76-byte META payload.
/// </summary>
public static class VatpMetaCodec
{
    private const int FieldTableOffset = 20;
    private const int RangePairBytes = 8;

    /// <summary>Encodes <paramref name="meta"/> into <paramref name="destination"/> (exactly 76 bytes).</summary>
    public static void Encode(in ArticleCanonicalTransferMeta meta, Span<byte> destination)
    {
        if (destination.Length < VatpProtocol.MetaPayloadLength)
        {
            throw new ArgumentException("Destination is smaller than META.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination[..8], meta.ArtHash);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(8, 4), meta.ArtLines);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(12, 4), meta.ArtSize);
        destination[16] = (byte)meta.SelectedDateHeaderName;
        destination[17] = 0;
        destination[18] = 0;
        destination[19] = 0;
        WriteRange(destination, 0, meta.Fields.MessageId);
        WriteRange(destination, 1, meta.Fields.Newsgroups);
        WriteRange(destination, 2, meta.Fields.Subject);
        WriteRange(destination, 3, meta.Fields.From);
        WriteRange(destination, 4, meta.Fields.Date);
        WriteRange(destination, 5, meta.Fields.References);
        WriteRange(destination, 6, meta.Fields.Path);
    }

    /// <summary>Allocates an exact 76-byte META buffer.</summary>
    public static byte[] Encode(in ArticleCanonicalTransferMeta meta)
    {
        var buffer = new byte[VatpProtocol.MetaPayloadLength];
        Encode(in meta, buffer);
        return buffer;
    }

    /// <summary>Attempts to decode a contiguous META payload.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out ArticleCanonicalTransferMeta meta, out VatpErrorCode error)
    {
        meta = default;
        error = VatpErrorCode.None;
        if (payload.Length != VatpProtocol.MetaPayloadLength)
        {
            error = VatpErrorCode.InvalidMeta;
            return false;
        }

        if (payload[17] != 0 || payload[18] != 0 || payload[19] != 0)
        {
            error = VatpErrorCode.InvalidMeta;
            return false;
        }

        var artHash = BinaryPrimitives.ReadUInt64BigEndian(payload[..8]);
        var artLines = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(8, 4));
        var artSize = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(12, 4));
        var selected = (NntpArticleHeaderName)payload[16];
        if (!TryReadRange(payload, 0, out var messageId)
            || !TryReadRange(payload, 1, out var newsgroups)
            || !TryReadRange(payload, 2, out var subject)
            || !TryReadRange(payload, 3, out var from)
            || !TryReadRange(payload, 4, out var date)
            || !TryReadRange(payload, 5, out var references)
            || !TryReadRange(payload, 6, out var path))
        {
            error = VatpErrorCode.InvalidFieldRange;
            return false;
        }

        meta = new ArticleCanonicalTransferMeta(
            artHash,
            artLines,
            artSize,
            selected,
            new ArticleFieldTable(messageId, newsgroups, subject, from, date, references, path));
        return true;
    }

    private static void WriteRange(Span<byte> destination, int index, ArticleByteRange range)
    {
        var offset = FieldTableOffset + (index * RangePairBytes);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset, 4), range.Offset);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset + 4, 4), range.Length);
    }

    private static bool TryReadRange(ReadOnlySpan<byte> payload, int index, out ArticleByteRange range)
    {
        var offset = FieldTableOffset + (index * RangePairBytes);
        var rangeOffset = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(offset, 4));
        var rangeLength = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(offset + 4, 4));
        if (rangeOffset == -1)
        {
            if (rangeLength != 0)
            {
                range = default;
                return false;
            }

            range = ArticleByteRange.Absent;
            return true;
        }

        if (rangeOffset < 0 || rangeLength < 0)
        {
            range = default;
            return false;
        }

        range = new ArticleByteRange(rangeOffset, rangeLength);
        return true;
    }
}
