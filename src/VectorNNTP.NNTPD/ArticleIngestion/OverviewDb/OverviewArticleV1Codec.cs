using System.Text;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;

/// <summary>
/// Compact protobuf encoder/decoder for <see cref="OverviewArticleV1"/>.
/// </summary>
/// <remarks>
/// Encodes from <see cref="ArticleRecord"/> field ranges without copying
/// <see cref="ArticleRecord.ArtData"/> or reparsing the article. Text fields
/// are the canonical header value bytes already stored in ArtData, written as
/// protobuf UTF-8 strings. Newsgroups are tokenized from the Newsgroups value
/// range only (comma-separated, SP/HTAB/CR/LF skipped).
/// </remarks>
internal static class OverviewArticleV1Codec
{
    private const int WireVarint = 0;
    private const int WireLengthDelimited = 2;
    private const int FieldSchemaVersion = 1;
    private const int FieldArticleId = 2;
    private const int FieldMessageId = 3;
    private const int FieldNewsgroups = 4;
    private const int FieldSubject = 5;
    private const int FieldFrom = 6;
    private const int FieldDate = 7;
    private const int FieldReferences = 8;
    private const int FieldBytes = 9;
    private const int FieldLines = 10;
    private const int TagOverhead = 10;
    private const int FixedScalarOverhead = 64;

    /// <summary>
    /// Upper bound of the encoded payload for <paramref name="record"/>.
    /// </summary>
    /// <param name="record">Canonical article whose overview fields will be encoded.</param>
    /// <returns>A rent size large enough for <see cref="Encode(in ArticleRecord, Span{byte})"/>.</returns>
    public static int GetMaxEncodedSize(in ArticleRecord record)
    {
        var newsgroups = record.Newsgroups;
        var groupTagOverhead = 0;
        var remaining = newsgroups;
        while (!remaining.IsEmpty)
        {
            groupTagOverhead += TagOverhead;
            var comma = remaining.IndexOf((byte)',');
            remaining = comma < 0 ? default : remaining[(comma + 1)..];
        }

        return FixedScalarOverhead
            + ArticleId.Length
            + record.MessageId.Length
            + newsgroups.Length
            + groupTagOverhead
            + record.Subject.Length
            + record.From.Length
            + record.Date.Length
            + record.References.Length;
    }

    /// <summary>
    /// Encodes overview metadata from <paramref name="record"/> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="record">CanonicalV1 article. The body is not copied.</param>
    /// <param name="destination">Caller-provided buffer; see <see cref="GetMaxEncodedSize"/>.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">Thrown when the destination is too small.</exception>
    public static int Encode(in ArticleRecord record, Span<byte> destination)
    {
        var written = 0;
        written += WriteVarintField(destination[written..], FieldSchemaVersion, OverviewArticleV1.CurrentSchemaVersion);

        Span<byte> artId = stackalloc byte[ArticleId.Length];
        record.ArtId.CopyTo(artId);
        written += WriteBytesField(destination[written..], FieldArticleId, artId);

        written += WriteBytesField(destination[written..], FieldMessageId, record.MessageId);
        written += WriteNewsgroups(destination[written..], record.Newsgroups);
        written += WriteBytesField(destination[written..], FieldSubject, record.Subject);
        written += WriteBytesField(destination[written..], FieldFrom, record.From);
        written += WriteBytesField(destination[written..], FieldDate, record.Date);
        written += WriteBytesField(destination[written..], FieldReferences, record.References);
        written += WriteVarintField(destination[written..], FieldBytes, (uint)Math.Max(record.ArtSize, 0));
        written += WriteVarintField(destination[written..], FieldLines, (uint)Math.Max(record.ArtLines, 0));
        return written;
    }

    /// <summary>
    /// Allocates a compact encoded payload for tests and diagnostics.
    /// </summary>
    /// <param name="record">Canonical article to encode.</param>
    /// <returns>The protobuf bytes, sized exactly to the encoded length.</returns>
    public static byte[] Encode(in ArticleRecord record)
    {
        var buffer = new byte[GetMaxEncodedSize(record)];
        var written = Encode(record, buffer);
        if (written == buffer.Length)
        {
            return buffer;
        }

        var exact = new byte[written];
        buffer.AsSpan(0, written).CopyTo(exact);
        return exact;
    }

    /// <summary>
    /// Decodes a protobuf payload produced by <see cref="Encode(in ArticleRecord, Span{byte})"/>.
    /// </summary>
    /// <param name="payload">OverviewDB handoff bytes.</param>
    /// <returns>The decoded message.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the payload is truncated or uses an unknown wire type.</exception>
    public static OverviewArticleV1 Decode(ReadOnlySpan<byte> payload)
    {
        var message = new OverviewArticleV1();
        var remaining = payload;
        while (!remaining.IsEmpty)
        {
            remaining = ReadVarint(remaining, out var key);
            var field = (int)(key >> 3);
            var wire = (int)(key & 0x7);
            switch (wire)
            {
                case WireVarint:
                    remaining = ReadVarint(remaining, out var number);
                    switch (field)
                    {
                        case FieldSchemaVersion:
                            message.SchemaVersion = (uint)number;
                            break;
                        case FieldBytes:
                            message.Bytes = (uint)number;
                            break;
                        case FieldLines:
                            message.Lines = (uint)number;
                            break;
                    }

                    break;
                case WireLengthDelimited:
                    remaining = ReadVarint(remaining, out var length);
                    if (length > (ulong)remaining.Length)
                    {
                        throw new InvalidOperationException("OverviewDB protobuf payload is truncated.");
                    }

                    var slice = remaining[..(int)length];
                    remaining = remaining[(int)length..];
                    switch (field)
                    {
                        case FieldArticleId:
                            message.ArticleId = slice.ToArray();
                            break;
                        case FieldMessageId:
                            message.MessageId = Encoding.UTF8.GetString(slice);
                            break;
                        case FieldNewsgroups:
                            message.Newsgroups.Add(Encoding.UTF8.GetString(slice));
                            break;
                        case FieldSubject:
                            message.Subject = Encoding.UTF8.GetString(slice);
                            break;
                        case FieldFrom:
                            message.From = Encoding.UTF8.GetString(slice);
                            break;
                        case FieldDate:
                            message.Date = Encoding.UTF8.GetString(slice);
                            break;
                        case FieldReferences:
                            message.References = Encoding.UTF8.GetString(slice);
                            break;
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unsupported protobuf wire type {wire}.");
            }
        }

        return message;
    }

    /// <summary>
    /// Returns whether <paramref name="payload"/> contains every byte of <paramref name="artData"/>.
    /// </summary>
    /// <param name="payload">Encoded overview message.</param>
    /// <param name="artData">Canonical article bytes.</param>
    /// <returns><see langword="true"/> when the full article is present as a contiguous span.</returns>
    public static bool ContainsCompleteArticle(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> artData)
        => artData.Length > 0 && payload.IndexOf(artData) >= 0;

    private static int WriteNewsgroups(Span<byte> destination, ReadOnlySpan<byte> newsgroups)
    {
        var written = 0;
        var remaining = newsgroups;
        while (!remaining.IsEmpty)
        {
            var comma = remaining.IndexOf((byte)',');
            var token = comma < 0 ? remaining : remaining[..comma];
            remaining = comma < 0 ? default : remaining[(comma + 1)..];
            token = TrimHeaderToken(token);
            if (token.IsEmpty)
            {
                continue;
            }

            written += WriteBytesField(destination[written..], FieldNewsgroups, token);
        }

        return written;
    }

    private static ReadOnlySpan<byte> TrimHeaderToken(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && IsHeaderWhitespace(value[0]))
        {
            value = value[1..];
        }

        while (!value.IsEmpty && IsHeaderWhitespace(value[^1]))
        {
            value = value[..^1];
        }

        return value;
    }

    private static bool IsHeaderWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static int WriteVarintField(Span<byte> destination, int fieldNumber, uint value)
    {
        var written = WriteKey(destination, fieldNumber, WireVarint);
        written += WriteVarint(destination[written..], value);
        return written;
    }

    private static int WriteBytesField(Span<byte> destination, int fieldNumber, ReadOnlySpan<byte> value)
    {
        var written = WriteKey(destination, fieldNumber, WireLengthDelimited);
        written += WriteVarint(destination[written..], (uint)value.Length);
        if (destination.Length - written < value.Length)
        {
            throw new ArgumentException("OverviewDB protobuf destination is too small.", nameof(destination));
        }

        value.CopyTo(destination[written..]);
        return written + value.Length;
    }

    private static int WriteKey(Span<byte> destination, int fieldNumber, int wireType) =>
        WriteVarint(destination, ((uint)fieldNumber << 3) | (uint)wireType);

    private static int WriteVarint(Span<byte> destination, uint value)
    {
        var written = 0;
        while (value >= 0x80)
        {
            if (written >= destination.Length)
            {
                throw new ArgumentException("OverviewDB protobuf destination is too small.", nameof(destination));
            }

            destination[written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        if (written >= destination.Length)
        {
            throw new ArgumentException("OverviewDB protobuf destination is too small.", nameof(destination));
        }

        destination[written++] = (byte)value;
        return written;
    }

    private static ReadOnlySpan<byte> ReadVarint(ReadOnlySpan<byte> source, out ulong value)
    {
        value = 0;
        var shift = 0;
        var index = 0;
        while (index < source.Length)
        {
            var b = source[index++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return source[index..];
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidOperationException("OverviewDB protobuf varint is too long.");
            }
        }

        throw new InvalidOperationException("OverviewDB protobuf payload is truncated.");
    }
}
