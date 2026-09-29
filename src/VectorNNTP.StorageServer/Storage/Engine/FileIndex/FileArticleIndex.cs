using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Storage.Engine.FileIndex;

/// <summary>
/// Filesystem-backed durable article index under <see cref="ArticleStorageRuntimeOptions.ControlDir"/>.
/// </summary>
/// <remarks>
/// <para>
/// Single append-only file <c>article.index</c>. Each durable mutation appends a full
/// <see cref="StoredArticleMetadata"/> snapshot; replay is last-write-wins per
/// <see cref="ArticleId"/>. Durability uses <see cref="FileStream.Flush(bool)"/> with
/// <c>flushToDisk: true</c>.
/// </para>
/// <para>
/// <see cref="TouchHint"/> updates in-memory LastAccess only and must not perform a durable
/// write. Index durability is distinct from journal Accept durability and does not prove
/// physical segment bytes — <see cref="ISegmentStore"/> must still validate ArtData on read.
/// </para>
/// <para>
/// Evicted/Invalid are logical states only; physical SATA reclamation is a later phase.
/// Relocation changes index metadata only and never mutates segment bytes.
/// </para>
/// </remarks>
public sealed class FileArticleIndex : IArticleIndex, IDisposable, IAsyncDisposable
{
    /// <summary>Engine-owned index filename beneath ControlDir.</summary>
    public const string IndexFileName = "article.index";

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<ArticleId, StoredArticleMetadata> _entries = new();
    private readonly string _indexPath;
    private FileStream _stream;
    private long _durableWriteCount;
    private long _touchHintCount;
    private bool _disposed;

    private FileArticleIndex(string indexPath, FileStream stream, ILogger logger)
    {
        _indexPath = indexPath;
        _stream = stream;
        _logger = logger;
    }

    /// <summary>Gets the absolute index file path.</summary>
    public string IndexPath => _indexPath;

    /// <summary>Number of durable mutations appended (tests).</summary>
    public long DurableWriteCount
    {
        get
        {
            lock (_gate)
            {
                return _durableWriteCount;
            }
        }
    }

    /// <summary>Number of soft TouchHint calls (tests).</summary>
    public long TouchHintCount
    {
        get
        {
            lock (_gate)
            {
                return _touchHintCount;
            }
        }
    }

    /// <summary>
    /// Opens or creates <c>article.index</c> under <paramref name="options"/>.ControlDir and
    /// replays durable mutations (torn final frame may be truncated).
    /// </summary>
    public static FileArticleIndex Open(
        ArticleStorageRuntimeOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ControlDir);

        var log = logger ?? NullLogger.Instance;
        Directory.CreateDirectory(options.ControlDir);
        var path = Path.Combine(options.ControlDir, IndexFileName);
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.None);

        var index = new FileArticleIndex(path, stream, log);
        try
        {
            index.ReplayAndRecoverUnlocked();
            FileArticleIndexLogMessages.Opened(
                log,
                path,
                stream.Length,
                index._entries.Count);
            return index;
        }
        catch
        {
            index.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryGet(ArticleId artId, out StoredArticleMetadata metadata)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.TryGetValue(artId, out metadata);
        }
    }

    /// <summary>
    /// Returns a snapshot of all index entries (Present, Evicted, and Invalid).
    /// </summary>
    /// <remarks>
    /// Used to rebuild segment Live/Dead accounting after open/recovery. Does not perform IO.
    /// </remarks>
    public IReadOnlyList<StoredArticleMetadata> Snapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.Values.ToArray();
        }
    }

    /// <inheritdoc />
    public bool TryCommitPresent(in StoredArticleMetadata metadata)
    {
        if (metadata.State != ArticleStorageState.Present)
        {
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(metadata.ArtId, out var existing)
                && existing.State == ArticleStorageState.Present)
            {
                if (existing.ArtHash == metadata.ArtHash
                    && existing.ArtSize == metadata.ArtSize
                    && LocationsEqual(existing.Location, metadata.Location))
                {
                    return true;
                }

                // Same identity + different location requires TryRelocate; hash/size conflict rejected.
                return false;
            }

            AppendDurableUnlocked(metadata);
            _entries[metadata.ArtId] = metadata;
            return true;
        }
    }

    /// <inheritdoc />
    public ArticleRelocateOutcome TryRelocate(
        ArticleId artId,
        in StoredArticleLocation expectedLocation,
        in StoredArticleLocation newLocation,
        ulong artHash,
        int artSize)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(artId, out var existing)
                || existing.State != ArticleStorageState.Present)
            {
                return ArticleRelocateOutcome.NotPresent;
            }

            if (existing.ArtHash != artHash || existing.ArtSize != artSize)
            {
                return ArticleRelocateOutcome.IdentityMismatch;
            }

            if (LocationsEqual(existing.Location, newLocation))
            {
                return ArticleRelocateOutcome.IdempotentNoOp;
            }

            if (!LocationsEqual(existing.Location, expectedLocation))
            {
                return ArticleRelocateOutcome.ExpectedLocationMismatch;
            }

            var updated = existing with { Location = newLocation };
            AppendDurableUnlocked(updated);
            _entries[artId] = updated;
            return ArticleRelocateOutcome.Relocated;
        }
    }

    /// <inheritdoc />
    public bool TrySetState(ArticleId artId, ArticleStorageState state, DateTimeOffset utcNow)
    {
        if (state is not (ArticleStorageState.Evicted or ArticleStorageState.Invalid))
        {
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(artId, out var existing))
            {
                return false;
            }

            if (existing.State == state)
            {
                return true;
            }

            if (existing.State != ArticleStorageState.Present)
            {
                return false;
            }

            var updated = existing with { State = state, LastAccessUtc = utcNow };
            AppendDurableUnlocked(updated);
            _entries[artId] = updated;
            return true;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Soft in-memory LastAccess hint only. Must not require a durable NVMe write; loss across
    /// crash is acceptable and not required for recovery correctness.
    /// </remarks>
    public void TouchHint(ArticleId artId, DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _touchHintCount++;
            if (!_entries.TryGetValue(artId, out var existing)
                || existing.State != ArticleStorageState.Present)
            {
                return;
            }

            _entries[artId] = existing with { LastAccessUtc = utcNow };
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Dispose();
            FileArticleIndexLogMessages.Closed(_logger, _indexPath);
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void AppendDurableUnlocked(in StoredArticleMetadata metadata)
    {
        var frame = ArticleIndexRecordCodec.Encode(metadata);
        _stream.Seek(0, SeekOrigin.End);
        _stream.Write(frame, 0, frame.Length);
        _stream.Flush(flushToDisk: true);
        _durableWriteCount++;
    }

    private void ReplayAndRecoverUnlocked()
    {
        var fileLength = _stream.Length;
        if (fileLength == 0)
        {
            _stream.Seek(0, SeekOrigin.End);
            return;
        }

        if (fileLength > int.MaxValue)
        {
            throw new ArticleIndexCorruptException(
                $"Article index exceeds supported size ({fileLength} bytes).",
                0);
        }

        var buffer = new byte[(int)fileLength];
        _stream.Seek(0, SeekOrigin.Begin);
        var read = _stream.Read(buffer, 0, buffer.Length);
        if (read != buffer.Length)
        {
            throw new IOException($"Short read replaying article index ({read}/{buffer.Length}).");
        }

        var offset = 0;
        while (offset < buffer.Length)
        {
            var span = buffer.AsSpan(offset);
            if (!ArticleIndexRecordCodec.TryDecode(
                    span,
                    out var frameLength,
                    out var metadata,
                    out var error))
            {
                HandleDecodeFailureUnlocked(offset, buffer.Length, error);
                break;
            }

            _entries[metadata.ArtId] = metadata;
            offset += frameLength;
        }

        _stream.Seek(0, SeekOrigin.End);
    }

    private void HandleDecodeFailureUnlocked(
        long offset,
        long fileLength,
        ArticleIndexFrameError error)
    {
        switch (error)
        {
            case ArticleIndexFrameError.Incomplete:
                // Fixed RecordLength: fewer than RecordLength bytes at EOF is a genuine torn write.
                TruncateTornTailUnlocked(offset, fileLength, "incomplete-final-frame");
                return;

            case ArticleIndexFrameError.CorruptChecksum:
                // A full fixed-size frame with a bad CRC is corruption, including at EOF.
                // EOF does not prove a torn write once RecordLength bytes are present.
                FileArticleIndexLogMessages.MidFileCorrupt(
                    _logger,
                    _indexPath,
                    offset,
                    error.ToString());
                throw new ArticleIndexCorruptException(
                    $"Article index corrupt checksum at offset {offset} (complete fixed-size frame).",
                    offset);

            case ArticleIndexFrameError.CorruptLength:
            case ArticleIndexFrameError.Corrupt:
                FileArticleIndexLogMessages.MidFileCorrupt(
                    _logger,
                    _indexPath,
                    offset,
                    error.ToString());
                throw new ArticleIndexCorruptException(
                    $"Article index corrupt at offset {offset} ({error}).",
                    offset);

            default:
                throw new ArticleIndexCorruptException(
                    $"Article index decode failed at offset {offset} ({error}).",
                    offset);
        }
    }

    private void TruncateTornTailUnlocked(long validEnd, long fileLength, string reason)
    {
        FileArticleIndexLogMessages.TruncatingTornTail(
            _logger,
            _indexPath,
            validEnd,
            fileLength,
            reason);
        _stream.SetLength(validEnd);
        _stream.Flush(flushToDisk: true);
    }

    private static bool LocationsEqual(in StoredArticleLocation left, in StoredArticleLocation right) =>
        left.SegmentId.Value == right.SegmentId.Value
        && left.Offset == right.Offset
        && left.Length == right.Length;
}
