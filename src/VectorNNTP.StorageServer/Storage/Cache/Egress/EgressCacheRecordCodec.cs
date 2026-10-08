using System.Buffers.Binary;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Cache.Egress;

/// <summary>
/// On-disk identity header for one disposable egress-cache record.
/// </summary>
/// <remarks>
/// Little-endian, version 1, 64-byte header followed by ArtData. The header is not a
/// journal frame and is not consulted during startup recovery.
/// </remarks>
internal static class EgressCacheRecordCodec
{
    /// <summary>Header length in bytes.</summary>
    public const int HeaderBytes = 64;

    /// <summary>ASCII <c>VEGC</c> as a little-endian <c>u32</c>.</summary>
    public const uint Magic = 0x43474556;

    /// <summary>Record version stored in the header.</summary>
    public const ushort Version = 1;

    /// <summary>Returns the on-disk length of a record with <paramref name="artSize"/> payload bytes.</summary>
    /// <param name="artSize">Canonical ArtData length.</param>
    /// <returns>Header plus payload.</returns>
    public static int RecordLength(int artSize) => HeaderBytes + artSize;

    /// <summary>
    /// Writes one record into <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Buffer of <see cref="RecordLength"/> bytes.</param>
    /// <param name="artId">Article identity.</param>
    /// <param name="sequence">Journal Accept sequence of this incarnation.</param>
    /// <param name="artHash">XxHash3-64 of <paramref name="payload"/>.</param>
    /// <param name="artSize">Payload length. Must match <paramref name="payload"/>.</param>
    /// <param name="payload">Canonical ArtData.</param>
    public static void Write(
        Span<byte> destination,
        ArticleId artId,
        ulong sequence,
        ulong artHash,
        int artSize,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length != artSize || destination.Length != RecordLength(artSize))
        {
            throw new ArgumentException("Egress cache record buffer does not match ArtSize.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], HeaderBytes);
        artId.CopyTo(destination.Slice(8, ArticleId.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(destination[40..], sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[48..], artHash);
        BinaryPrimitives.WriteInt32LittleEndian(destination[56..], artSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[60..], 0);
        payload.CopyTo(destination[HeaderBytes..]);
    }

    /// <summary>
    /// Reads the identity header. Does not hash the payload.
    /// </summary>
    /// <param name="header">At least <see cref="HeaderBytes"/> bytes.</param>
    /// <param name="artId">Identity when this returns true.</param>
    /// <param name="sequence">Incarnation when this returns true.</param>
    /// <param name="artHash">Expected XxHash3 when this returns true.</param>
    /// <param name="artSize">Payload length when this returns true.</param>
    /// <returns>False when the magic, version, or declared length is not a version-1 record.</returns>
    public static bool TryReadHeader(
        ReadOnlySpan<byte> header,
        out ArticleId artId,
        out ulong sequence,
        out ulong artHash,
        out int artSize)
    {
        artId = default;
        sequence = 0;
        artHash = 0;
        artSize = 0;
        if (header.Length < HeaderBytes)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version
            || BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != HeaderBytes)
        {
            return false;
        }

        artId = ArticleId.FromSpan(header.Slice(8, ArticleId.Length));
        sequence = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
        artHash = BinaryPrimitives.ReadUInt64LittleEndian(header[48..]);
        artSize = BinaryPrimitives.ReadInt32LittleEndian(header[56..]);
        return artSize >= 1 && sequence >= 1;
    }
}
