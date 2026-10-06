using System.Buffers.Binary;
using System.IO.Hashing;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Engine.FileIndex;

/// <summary>
/// On-disk form of one compacted <see cref="FileArticleIndex"/> projection.
/// </summary>
/// <remarks>
/// Little-endian header, then one current index frame per row, then a trailer CRC.
/// Version 3 bodies are schema 3 frames. Version 2 bodies are schema 2 frames and are still read;
/// those rows have no arrival instant. The magic is ASCII <c>VNIS</c>, which is not a little-endian
/// frame length, so a snapshot cannot be mistaken for a legacy index frame.
/// <list type="table">
/// <item><term>0</term><description>4 ASCII magic <c>VNIS</c></description></item>
/// <item><term>4</term><description>u32 version (3; version 2 snapshots are still read)</description></item>
/// <item><term>8</term><description>u64 snapshot generation</description></item>
/// <item><term>16</term><description>u64 covered delta generation (0 while <c>article.index</c> has no generation header)</description></item>
/// <item><term>24</term><description>u64 covered <c>article.index</c> length at the dictionary copy</description></item>
/// <item><term>32</term><description>u64 record count</description></item>
/// <item><term>40</term><description>u32 CRC-32 of bytes [0, 40)</description></item>
/// </list>
/// The body is one current frame per article id. The trailer is the CRC-32 of the body.
/// A <c>.tmp</c> file is not a snapshot. This codec does not repair a short file.
/// </remarks>
internal static class ArticleIndexSnapshotCodec
{
    /// <summary>ASCII <c>VNIS</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "VNIS"u8;

    /// <summary>
    /// Current snapshot version. Version 2 snapshots store schema 2 frames and are still read.
    /// Version 1 snapshots use 88-byte records and are rejected.
    /// </summary>
    public const uint Version = 3;

    /// <summary>Snapshot version whose body frames are schema 2.</summary>
    public const uint Schema2Version = 2;

    /// <summary>
    /// Covered delta generation written while <c>article.index</c> is still an unheadered frame log.
    /// </summary>
    public const ulong LegacyDeltaGeneration = 0;

    /// <summary>Fixed header size, including the header CRC.</summary>
    public const int HeaderLength = 44;

    private const int HeaderCrcLength = 4;

    private const int TrailerLength = 4;

    /// <summary>
    /// Encoded length of a snapshot with <paramref name="entryCount"/> records.
    /// This is the byte count <see cref="Write"/> emits for that count.
    /// </summary>
    public static long EncodedLength(int entryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryCount);
        return checked(HeaderLength + ((long)entryCount * ArticleIndexRecordCodec.RecordLength) + TrailerLength);
    }

    /// <summary>Writes one snapshot. Does not flush the stream.</summary>
    public static void Write(
        Stream stream,
        ulong generation,
        long coveredIndexLength,
        ReadOnlySpan<StoredArticleMetadata> rows)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (coveredIndexLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coveredIndexLength));
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], generation);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], LegacyDeltaGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], (ulong)coveredIndexLength);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)rows.Length);
        var headerCrc = Crc32.HashToUInt32(header[..(HeaderLength - HeaderCrcLength)]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(HeaderLength - HeaderCrcLength)..], headerCrc);
        stream.Write(header);

        var bodyCrc = new Crc32();
        foreach (var row in rows)
        {
            var frame = ArticleIndexRecordCodec.Encode(row);
            stream.Write(frame);
            bodyCrc.Append(frame);
        }

        Span<byte> trailer = stackalloc byte[TrailerLength];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, bodyCrc.GetCurrentHashAsUInt32());
        stream.Write(trailer);
    }

    /// <summary>Reads and validates an installed snapshot. Does not accept a partial file.</summary>
    public static ArticleIndexSnapshot Read(string path)
    {
        var entries = new List<StoredArticleMetadata>();
        var header = ReadRecords(path, entries.Add);
        return new ArticleIndexSnapshot(
            header.Generation,
            header.CoveredDeltaGeneration,
            header.CoveredIndexLength,
            entries);
    }

    /// <summary>
    /// Validates an installed snapshot and assigns each current row into <paramref name="entries"/>.
    /// One frame is buffered at a time.
    /// </summary>
    /// <returns>The validated header. Covered length is an <c>article.index</c> byte offset, not a journal sequence.</returns>
    public static ArticleIndexSnapshotHeader Apply(
        string path,
        Dictionary<ArticleId, StoredArticleMetadata> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return ReadRecords(path, row => entries[row.ArtId] = row);
    }

    private static ArticleIndexSnapshotHeader ReadRecords(string path, Action<StoredArticleMetadata> accept)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(accept);
        using var stream = OpenRead(path);
        var length = stream.Length;
        var header = ReadHeader(stream, length, path, out var frameLength);
        var count = checked((int)header.RecordCount);
        var frame = new byte[frameLength];
        var bodyCrc = new Crc32();
        for (var i = 0; i < count; i++)
        {
            var frameOffset = HeaderLength + ((long)i * frameLength);
            ReadExact(stream, frame, path, frameOffset);
            bodyCrc.Append(frame);
            if (!ArticleIndexRecordCodec.TryDecode(frame, out _, out var metadata, out var error))
            {
                throw new ArticleIndexCorruptException(
                    $"Article index snapshot corrupt frame at record {i} ({error}).",
                    frameOffset);
            }

            accept(metadata);
        }

        Span<byte> trailer = stackalloc byte[TrailerLength];
        ReadExact(stream, trailer, path, length - TrailerLength);
        var actualTrailer = bodyCrc.GetCurrentHashAsUInt32();
        var expectedTrailer = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        if (actualTrailer != expectedTrailer)
        {
            throw new ArticleIndexCorruptException(
                $"Article index snapshot trailer CRC mismatch at offset {length - TrailerLength}.",
                length - TrailerLength);
        }

        return header;
    }

    /// <summary>Reads the snapshot header only. Used to continue generation numbers without loading rows.</summary>
    public static ArticleIndexSnapshotHeader ReadHeader(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = OpenRead(path);
        return ReadHeader(stream, stream.Length, path, out _);
    }

    private static ArticleIndexSnapshotHeader ReadHeader(Stream stream, long length, string path, out int frameLength)
    {
        if (length < HeaderLength)
        {
            throw new ArticleIndexCorruptException(
                $"Article index snapshot truncated header ({length} bytes).",
                0);
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        ReadExact(stream, header, path, 0);
        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new ArticleIndexCorruptException("Article index snapshot magic mismatch.", 0);
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        frameLength = version switch
        {
            Version => ArticleIndexRecordCodec.RecordLength,
            Schema2Version => ArticleIndexRecordCodec.Schema2RecordLength,
            _ => 0,
        };
        if (frameLength == 0)
        {
            throw new ArticleIndexCorruptException(
                $"Article index snapshot version {version} is not supported.",
                4);
        }

        var expectedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[(HeaderLength - HeaderCrcLength)..]);
        var actualHeaderCrc = Crc32.HashToUInt32(header[..(HeaderLength - HeaderCrcLength)]);
        if (expectedHeaderCrc != actualHeaderCrc)
        {
            throw new ArticleIndexCorruptException("Article index snapshot header CRC mismatch.", HeaderLength - HeaderCrcLength);
        }

        var generation = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        var coveredDeltaGeneration = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        var coveredIndexLength = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
        var recordCount = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
        if (coveredIndexLength > long.MaxValue)
        {
            throw new ArticleIndexCorruptException(
                "Article index snapshot covered length is out of range.",
                24);
        }

        if (recordCount > int.MaxValue
            || HeaderLength + (recordCount * (ulong)frameLength) + TrailerLength != (ulong)length)
        {
            throw new ArticleIndexCorruptException(
                $"Article index snapshot length {length} does not match record count {recordCount}.",
                0);
        }

        return new ArticleIndexSnapshotHeader(
            generation,
            coveredDeltaGeneration,
            (long)coveredIndexLength,
            recordCount);
    }

    private static FileStream OpenRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.None);

    private static void ReadExact(Stream stream, Span<byte> buffer, string path, long offset)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = stream.Read(buffer[filled..]);
            if (read == 0)
            {
                throw new ArticleIndexCorruptException(
                    $"Article index snapshot truncated at offset {offset + filled} ({path}).",
                    offset + filled);
            }

            filled += read;
        }
    }
}

/// <summary>Validated snapshot header fields.</summary>
/// <param name="Generation">Snapshot generation. Not a journal sequence.</param>
/// <param name="CoveredDeltaGeneration">Delta-file generation covered by this snapshot. Zero for a legacy index log.</param>
/// <param name="CoveredIndexLength">Byte length of <c>article.index</c> included in the copied projection.</param>
/// <param name="RecordCount">Number of current article rows.</param>
internal readonly record struct ArticleIndexSnapshotHeader(
    ulong Generation,
    ulong CoveredDeltaGeneration,
    long CoveredIndexLength,
    ulong RecordCount);

/// <summary>Validated snapshot, including one current row per article id.</summary>
/// <param name="Generation">Snapshot generation. Not a journal sequence.</param>
/// <param name="CoveredDeltaGeneration">Delta-file generation covered by this snapshot. Zero for a legacy index log.</param>
/// <param name="CoveredIndexLength">Byte length of <c>article.index</c> included in the copied projection.</param>
/// <param name="Entries">Current projection. Historical duplicate frames are not present.</param>
internal sealed record ArticleIndexSnapshot(
    ulong Generation,
    ulong CoveredDeltaGeneration,
    long CoveredIndexLength,
    IReadOnlyList<StoredArticleMetadata> Entries);
