using System.Buffers.Binary;
using System.IO.Hashing;

namespace VectorNNTP.StorageServer.Storage.Engine.FileIndex;

/// <summary>
/// Header of an <c>article.index</c> file that replaced a covered historical prefix.
/// </summary>
/// <remarks>
/// <para>
/// Little-endian. The magic is ASCII <c>VNID</c>, which is not a little-endian frame length and is not
/// the snapshot magic <c>VNIS</c>. An unheadered index is a bare sequence of schema 2 or schema 3
/// frames and does not carry this header. Schema 1 files of 88-byte frames are rejected.
/// </para>
/// <list type="table">
/// <item><term>0</term><description>4 ASCII magic <c>VNID</c></description></item>
/// <item><term>4</term><description>u32 version (2)</description></item>
/// <item><term>8</term><description>u64 snapshot generation this file belongs to</description></item>
/// <item><term>16</term><description>u32 CRC-32 of bytes [0, 16)</description></item>
/// </list>
/// <para>
/// The payload is the raw schema 2 or schema 3 frames that were past the snapshot's covered length when
/// the replacement was installed. The snapshot generation is index-local. It is not a journal
/// sequence and it is not <see cref="ArticleIndexSnapshotCodec.LegacyDeltaGeneration"/>.
/// Startup compares this generation with the installed snapshot generation so a covered byte
/// offset from the pre-replacement file is never applied to the replacement file.
/// A file that begins with <c>VNID</c> and does not validate fails closed. It is not replayed
/// as a legacy log.
/// </para>
/// </remarks>
internal static class ArticleIndexDeltaFile
{
    /// <summary>ASCII <c>VNID</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "VNID"u8;

    /// <summary>Current replacement-header version. Version 1 replacements are rejected.</summary>
    public const uint Version = 2;

    /// <summary>Fixed header size, including the header CRC.</summary>
    public const int HeaderLength = 20;

    private const int HeaderCrcLength = 4;

    /// <summary>Writes the header. Does not flush the stream.</summary>
    public static void WriteHeader(Stream stream, ulong generation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], generation);
        var crc = Crc32.HashToUInt32(header[..(HeaderLength - HeaderCrcLength)]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(HeaderLength - HeaderCrcLength)..], crc);
        stream.Write(header);
    }

    /// <summary>
    /// Reads a replacement header.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the file is empty or does not start with <see cref="Magic"/>.
    /// The stream is then positioned at 0. <see langword="true"/> when the header validates.
    /// The stream is then positioned at <see cref="HeaderLength"/>.
    /// </returns>
    /// <exception cref="ArticleIndexCorruptException">
    /// The file starts with <see cref="Magic"/> and the header is truncated, an unknown version, or a bad CRC.
    /// </exception>
    public static bool TryReadHeader(Stream stream, long fileLength, out ulong generation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        generation = 0;
        if (fileLength < Magic.Length)
        {
            if (stream.CanSeek)
            {
                _ = stream.Seek(0, SeekOrigin.Begin);
            }

            return false;
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        _ = stream.Seek(0, SeekOrigin.Begin);
        ReadExact(stream, header[..Magic.Length]);
        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            _ = stream.Seek(0, SeekOrigin.Begin);
            return false;
        }

        if (fileLength < HeaderLength)
        {
            throw new ArticleIndexCorruptException(
                $"Article index replacement header is truncated ({fileLength} bytes).",
                0);
        }

        ReadExact(stream, header[Magic.Length..]);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (version != Version)
        {
            throw new ArticleIndexCorruptException(
                $"Article index replacement version {version} is not supported.",
                4);
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(header[(HeaderLength - HeaderCrcLength)..]);
        var actual = Crc32.HashToUInt32(header[..(HeaderLength - HeaderCrcLength)]);
        if (expected != actual)
        {
            throw new ArticleIndexCorruptException(
                "Article index replacement header CRC mismatch.",
                HeaderLength - HeaderCrcLength);
        }

        generation = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        return true;
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = stream.Read(buffer[filled..]);
            if (read == 0)
            {
                throw new ArticleIndexCorruptException(
                    "Article index replacement header ended early.",
                    filled);
            }

            filled += read;
        }
    }
}
