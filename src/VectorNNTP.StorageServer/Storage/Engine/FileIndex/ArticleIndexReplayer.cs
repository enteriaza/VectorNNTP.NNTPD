using System.Buffers.Binary;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Engine.FileIndex;

/// <summary>
/// Streams a legacy append-only <c>article.index</c> one fixed frame at a time.
/// </summary>
/// <remarks>
/// The frame layout is <see cref="ArticleIndexRecordCodec.RecordLength"/> bytes.
/// The file length stays a <see cref="long"/> and is never used as a buffer size.
/// A declared length other than the fixed record size fails closed before any further read.
/// </remarks>
internal static class ArticleIndexReplayer
{
    /// <summary>
    /// Replays frames from <paramref name="stream"/> into <paramref name="entries"/>.
    /// </summary>
    /// <param name="stream">Index bytes. Positioned at <paramref name="startOffset"/> when that offset is zero; otherwise seeked there.</param>
    /// <param name="fileLength">Byte length of the index file. Not narrowed to <see cref="int"/>.</param>
    /// <param name="entries">Last-write-wins destination. One row per <see cref="ArticleId"/>.</param>
    /// <param name="onFailure">
    /// Invoked once for a torn tail or a corrupt frame, then replay stops.
    /// Torn-tail handlers truncate and return. Corrupt handlers throw.
    /// </param>
    /// <param name="startOffset">Absolute index offset of the first frame to read. Zero replays the whole file.</param>
    public static void Replay(
        Stream stream,
        long fileLength,
        Dictionary<ArticleId, StoredArticleMetadata> entries,
        Action<long, long, ArticleIndexFrameError> onFailure,
        long startOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(onFailure);
        if (fileLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileLength));
        }

        if (startOffset < 0 || startOffset > fileLength)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset));
        }

        if (startOffset > 0)
        {
            if (!stream.CanSeek)
            {
                throw new ArgumentException("Index replay from a non-zero offset requires a seekable stream.", nameof(stream));
            }

            _ = stream.Seek(startOffset, SeekOrigin.Begin);
        }

        var frame = new byte[ArticleIndexRecordCodec.RecordLength];
        var offset = startOffset;
        while (offset < fileLength)
        {
            var remaining = fileLength - offset;
            if (remaining < 4)
            {
                onFailure(offset, fileLength, ArticleIndexFrameError.Incomplete);
                return;
            }

            ReadExact(stream, frame, 0, 4);
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(frame);
            if (declared != (uint)ArticleIndexRecordCodec.RecordLength)
            {
                onFailure(offset, fileLength, ArticleIndexFrameError.CorruptLength);
                return;
            }

            if (remaining < ArticleIndexRecordCodec.RecordLength)
            {
                onFailure(offset, fileLength, ArticleIndexFrameError.Incomplete);
                return;
            }

            ReadExact(stream, frame, 4, ArticleIndexRecordCodec.RecordLength - 4);
            if (!ArticleIndexRecordCodec.TryDecode(
                    frame,
                    out var frameLength,
                    out var metadata,
                    out var error))
            {
                onFailure(offset, fileLength, error);
                return;
            }

            entries[metadata.ArtId] = metadata;
            offset += frameLength;
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        var filled = 0;
        while (filled < count)
        {
            var read = stream.Read(buffer, offset + filled, count - filled);
            if (read == 0)
            {
                throw new IOException(
                    $"Short read replaying article index (filled {filled} of {count} bytes).");
            }

            filled += read;
        }
    }
}
