using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Storage.Engine.FileSegments;

/// <summary>
/// Filesystem-backed append-only SATA segment store under <see cref="ArticleStorageRuntimeOptions.SegmentDir"/>.
/// </summary>
/// <remarks>
/// <para>
/// Segment files: <c>seg-{id:D20}.{active|closed|retired}</c>. Exactly one Active writer.
/// Closed/Retired segments are immutable. Append durability uses
/// <see cref="FileStream.Flush(bool)"/> with <c>flushToDisk: true</c>.
/// </para>
/// <para>
/// Acceptance durability remains the NVMe journal. This store only provides physical
/// article persistence and location identity (<see cref="StoredArticleLocation"/>).
/// </para>
/// <para>
/// Active segment: incomplete final record is truncated on open. Closed/Retired: any
/// incomplete or corrupt record fails closed via <see cref="SegmentStoreCorruptException"/>.
/// </para>
/// </remarks>
public sealed class FileSegmentStore : ISegmentStore, IDisposable, IAsyncDisposable
{
    private readonly long _targetSegmentBytes;
    private readonly ILogger _logger;
    private readonly object _writeGate = new();
    private readonly Dictionary<ulong, SegmentRuntime> _segments = new();
    private readonly FileSegmentCatalogue _catalogue = new();
    private readonly string _root;
    private ulong _nextSegmentId = 1;
    private ulong? _activeSegmentId;
    private bool _disposed;

    private FileSegmentStore(
        string root,
        long targetSegmentBytes,
        ILogger logger)
    {
        _root = root;
        _targetSegmentBytes = targetSegmentBytes;
        _logger = logger;
    }

    /// <summary>Gets the segment root directory (CacheDir / SegmentDir).</summary>
    public string SegmentRoot => _root;

    /// <summary>Gets the in-memory catalogue reconstructed from segment files.</summary>
    public FileSegmentCatalogue Catalogue => _catalogue;

    /// <summary>Gets the next SegmentId that will be allocated.</summary>
    public ulong NextSegmentId
    {
        get
        {
            lock (_writeGate)
            {
                return _nextSegmentId;
            }
        }
    }

    /// <summary>
    /// Opens or creates the segment store under <paramref name="options"/>.SegmentDir,
    /// discovers existing segments, repairs only the active torn tail, and fail-closes on
    /// closed-segment corruption.
    /// </summary>
    public static FileSegmentStore Open(
        ArticleStorageRuntimeOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SegmentDir);

        var log = logger ?? NullLogger.Instance;
        Directory.CreateDirectory(options.SegmentDir);
        var store = new FileSegmentStore(options.SegmentDir, options.SegmentTargetSizeBytes, log);
        try
        {
            store.DiscoverAndRecoverUnlocked();
            store._catalogue.SetRetireHook(store.OnCatalogueRetire);
            FileSegmentStoreLogMessages.Opened(
                log,
                options.SegmentDir,
                store._segments.Count,
                store._activeSegmentId,
                store._nextSegmentId);
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask<IAppendableSegment> GetActiveAppenderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureActiveUnlocked();
            return ValueTask.FromResult<IAppendableSegment>(new FileAppendableSegment(this));
        }
    }

    /// <inheritdoc />
    public ValueTask CloseActiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeSegmentId is { } active)
            {
                CloseActiveUnlocked(active, DateTimeOffset.UtcNow);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public bool TryRead(in StoredArticleLocation location, out ReadOnlyMemory<byte> artData)
    {
        artData = default;
        if (location.Offset < 0 || location.Length < SegmentRecordCodec.MinimumRecordLength)
        {
            return false;
        }

        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_segments.TryGetValue(location.SegmentId.Value, out var runtime))
            {
                return false;
            }

            if (runtime.State == SegmentState.Retired)
            {
                return false;
            }

            return TryReadUnlocked(runtime, location, expectedArtId: null, expectedArtHash: null, expectedArtSize: null, out artData);
        }
    }

    /// <summary>
    /// Reads and proves the article at <paramref name="location"/> against expected identity.
    /// </summary>
    public bool TryReadProven(
        in StoredArticleLocation location,
        ArticleId expectedArtId,
        ulong expectedArtHash,
        int expectedArtSize,
        out ReadOnlyMemory<byte> artData)
    {
        artData = default;
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_segments.TryGetValue(location.SegmentId.Value, out var runtime)
                || runtime.State == SegmentState.Retired)
            {
                return false;
            }

            return TryReadUnlocked(
                runtime,
                location,
                expectedArtId,
                expectedArtHash,
                expectedArtSize,
                out artData);
        }
    }

    /// <summary>Gets catalogue info for tests.</summary>
    public bool TryGetSegmentInfo(SegmentId segmentId, out SegmentInfo info) =>
        _catalogue.TryGet(segmentId, out info);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var runtime in _segments.Values)
            {
                runtime.DisposeStream();
            }

            FileSegmentStoreLogMessages.StoreClosed(_logger, _root);
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal long GetActiveSizeBytes()
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeSegmentId is not { } id || !_segments.TryGetValue(id, out var runtime))
            {
                return 0;
            }

            return runtime.SizeBytes;
        }
    }

    internal SegmentId GetActiveSegmentId()
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureActiveUnlocked();
            return new SegmentId(_activeSegmentId!.Value);
        }
    }

    internal ValueTask<StoredArticleLocation> AppendToActiveAsync(
        ReadOnlyMemory<byte> artData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ValueTask.FromResult(AppendToActiveUnlocked(artData));
        }
    }

    private StoredArticleLocation AppendToActiveUnlocked(ReadOnlyMemory<byte> artData)
    {
        if (artData.Length is < 1 or > ArticleResourceLimits.MaxArticleBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(artData), "ArtData length out of range.");
        }

        if (!ArticleStorageIntegrity.TryExtractMessageIdValue(artData.Span, out var messageId))
        {
            throw new ArgumentException("ArtData must contain a Message-ID header value.", nameof(artData));
        }

        var artId = ArticleId.FromMessageId(messageId);
        var artHash = XxHash3.HashToUInt64(artData.Span);
        if (!ArticleStorageIntegrity.TryProve(artData.Span, artId, artHash, artData.Length))
        {
            throw new ArgumentException("ArtData failed article integrity proof.", nameof(artData));
        }

        var record = SegmentRecordCodec.Encode(artId, artHash, artData.Span);
        EnsureActiveUnlocked();
        var activeId = _activeSegmentId!.Value;
        var runtime = _segments[activeId];

        // Rotate when current + next would exceed target, unless the segment is empty
        // (a single oversized article may exceed the nominal target).
        if (runtime.SizeBytes > 0
            && runtime.SizeBytes + record.Length > _targetSegmentBytes)
        {
            var closedId = activeId;
            CloseActiveUnlocked(activeId, DateTimeOffset.UtcNow);
            EnsureActiveUnlocked();
            activeId = _activeSegmentId!.Value;
            runtime = _segments[activeId];
            FileSegmentStoreLogMessages.Rotated(_logger, closedId, activeId);
        }

        var offset = runtime.SizeBytes;
        runtime.Stream.Seek(0, SeekOrigin.End);
        runtime.Stream.Write(record, 0, record.Length);
        runtime.Stream.Flush(flushToDisk: true);
        runtime.SizeBytes = offset + record.Length;
        _catalogue.RecordAppend(runtime.SegmentId, record.Length, runtime.SizeBytes);
        return new StoredArticleLocation(runtime.SegmentId, offset, record.Length);
    }

    private void EnsureActiveUnlocked()
    {
        if (_activeSegmentId is { } existing && _segments.TryGetValue(existing, out var runtime)
            && runtime.State == SegmentState.Active)
        {
            return;
        }

        CreateActiveUnlocked(_nextSegmentId++, DateTimeOffset.UtcNow);
    }

    private void CreateActiveUnlocked(ulong id, DateTimeOffset utcNow)
    {
        var segmentId = new SegmentId(id);
        var path = Path.Combine(_root, SegmentFileNames.Format(segmentId, SegmentFileKind.Active));
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.None);
        var generation = _catalogue.AllocateGeneration();
        var info = new SegmentInfo(
            segmentId,
            SegmentState.Active,
            generation,
            SizeBytes: 0,
            LiveBytes: 0,
            DeadBytes: 0,
            CreatedUtc: utcNow,
            ClosedUtc: null);
        _catalogue.Upsert(info);
        _segments[id] = new SegmentRuntime(segmentId, SegmentState.Active, path, stream, sizeBytes: 0);
        _activeSegmentId = id;
        if (id >= _nextSegmentId)
        {
            _nextSegmentId = id + 1;
        }

        FileSegmentStoreLogMessages.ActiveSelected(_logger, id, 0);
    }

    private void CloseActiveUnlocked(ulong activeId, DateTimeOffset utcNow)
    {
        if (!_segments.TryGetValue(activeId, out var runtime) || runtime.State != SegmentState.Active)
        {
            _activeSegmentId = null;
            return;
        }

        runtime.Stream.Flush(flushToDisk: true);
        runtime.DisposeStream();
        var closedPath = Path.Combine(_root, SegmentFileNames.Format(runtime.SegmentId, SegmentFileKind.Closed));
        File.Move(runtime.Path, closedPath, overwrite: false);
        runtime.Path = closedPath;
        runtime.State = SegmentState.Closed;
        runtime.OpenReadOnly();

        if (!_catalogue.TryGet(runtime.SegmentId, out var info))
        {
            throw new InvalidOperationException($"Catalogue missing segment {runtime.SegmentId}.");
        }

        var generation = _catalogue.AllocateGeneration();
        _catalogue.Upsert(info with
        {
            State = SegmentState.Closed,
            Generation = generation,
            SizeBytes = runtime.SizeBytes,
            ClosedUtc = utcNow,
        });
        _activeSegmentId = null;
        FileSegmentStoreLogMessages.Closed(_logger, activeId, runtime.SizeBytes);
    }

    private void OnCatalogueRetire(SegmentId segmentId, ulong expectedGeneration, DateTimeOffset utcNow)
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_segments.TryGetValue(segmentId.Value, out var runtime))
            {
                return;
            }

            if (runtime.State == SegmentState.Active)
            {
                throw new InvalidOperationException("Cannot retire the active segment.");
            }

            if (runtime.State == SegmentState.Retired)
            {
                return;
            }

            runtime.DisposeStream();
            var retiredPath = Path.Combine(_root, SegmentFileNames.Format(segmentId, SegmentFileKind.Retired));
            File.Move(runtime.Path, retiredPath, overwrite: false);
            runtime.Path = retiredPath;
            runtime.State = SegmentState.Retired;
            FileSegmentStoreLogMessages.Retired(_logger, segmentId.Value);
            _ = utcNow;
            _ = expectedGeneration;
        }
    }

    private void DiscoverAndRecoverUnlocked()
    {
        var discovered = new List<(SegmentId Id, SegmentFileKind Kind, string Path, long Length)>();
        foreach (var path in Directory.EnumerateFiles(_root, "seg-*"))
        {
            var name = Path.GetFileName(path);
            if (!SegmentFileNames.TryParse(name, out var segmentId, out var kind))
            {
                continue;
            }

            var length = new FileInfo(path).Length;
            discovered.Add((segmentId, kind, path, length));
            FileSegmentStoreLogMessages.Discovered(_logger, segmentId.Value, kind.ToString(), length);
        }

        if (discovered.Count == 0)
        {
            return;
        }

        var activeCount = discovered.Count(static d => d.Kind == SegmentFileKind.Active);
        if (activeCount > 1)
        {
            throw new SegmentStoreCorruptException(
                $"Multiple active segment files found under '{_root}'.",
                _root);
        }

        var catalogueEntries = new List<SegmentInfo>();
        ulong maxId = 0;
        foreach (var item in discovered.OrderBy(static d => d.Id.Value))
        {
            maxId = Math.Max(maxId, item.Id.Value);
            var state = item.Kind switch
            {
                SegmentFileKind.Active => SegmentState.Active,
                SegmentFileKind.Closed => SegmentState.Closed,
                SegmentFileKind.Retired => SegmentState.Retired,
                _ => throw new SegmentStoreCorruptException($"Unknown segment kind for {item.Path}.", item.Path),
            };

            long validLength = item.Length;
            if (state == SegmentState.Active)
            {
                validLength = RepairActiveTail(item.Path, item.Id, item.Length);
            }
            else if (state is SegmentState.Closed or SegmentState.Retired)
            {
                ValidateImmutableSegmentOrThrow(item.Path, item.Id, item.Length);
            }

            var generation = _catalogue.AllocateGeneration();
            catalogueEntries.Add(new SegmentInfo(
                item.Id,
                state,
                generation,
                validLength,
                LiveBytes: validLength,
                DeadBytes: 0,
                CreatedUtc: DateTimeOffset.UtcNow,
                ClosedUtc: state == SegmentState.Active ? null : DateTimeOffset.UtcNow));

            FileStream? stream = null;
            if (state == SegmentState.Active)
            {
                stream = new FileStream(
                    item.Path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.None);
                stream.Seek(0, SeekOrigin.End);
                _activeSegmentId = item.Id.Value;
            }
            else if (state == SegmentState.Closed)
            {
                stream = new FileStream(
                    item.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 64 * 1024,
                    FileOptions.None);
            }

            _segments[item.Id.Value] = new SegmentRuntime(
                item.Id,
                state,
                item.Path,
                stream,
                validLength);
        }

        _catalogue.ReplaceAll(catalogueEntries);
        _nextSegmentId = maxId + 1;
        if (_activeSegmentId is { } active)
        {
            FileSegmentStoreLogMessages.ActiveSelected(
                _logger,
                active,
                _segments[active].SizeBytes);
        }
    }

    private long RepairActiveTail(string path, SegmentId segmentId, long fileLength)
    {
        if (fileLength == 0)
        {
            return 0;
        }

        if (fileLength > int.MaxValue)
        {
            throw new SegmentStoreCorruptException(
                $"Active segment {segmentId} exceeds supported size.",
                path);
        }

        var bytes = File.ReadAllBytes(path);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var span = bytes.AsSpan(offset);
            if (SegmentRecordCodec.TryDecode(
                    span,
                    out var recordLength,
                    out _,
                    out _,
                    out _,
                    out _,
                    out var error))
            {
                offset += recordLength;
                continue;
            }

            if (error == SegmentRecordCodec.DecodeError.Incomplete
                || (error is SegmentRecordCodec.DecodeError.CorruptChecksum
                    or SegmentRecordCodec.DecodeError.Corrupt
                    && offset + Math.Max(recordLength, 0) >= bytes.Length))
            {
                FileSegmentStoreLogMessages.TruncatingTornTail(
                    _logger,
                    segmentId.Value,
                    offset,
                    bytes.Length,
                    error.ToString());
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.None);
                stream.SetLength(offset);
                stream.Flush(flushToDisk: true);
                return offset;
            }

            // CorruptLength / mid-file corruption on active: fail closed (do not guess).
            FileSegmentStoreLogMessages.ClosedCorrupt(_logger, path, offset, error.ToString());
            throw new SegmentStoreCorruptException(
                $"Active segment {segmentId} corrupt at offset {offset} ({error}).",
                path);
        }

        return offset;
    }

    private void ValidateImmutableSegmentOrThrow(string path, SegmentId segmentId, long fileLength)
    {
        if (fileLength == 0)
        {
            return;
        }

        if (fileLength > int.MaxValue)
        {
            throw new SegmentStoreCorruptException(
                $"Closed segment {segmentId} exceeds supported size.",
                path);
        }

        var bytes = File.ReadAllBytes(path);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var span = bytes.AsSpan(offset);
            if (!SegmentRecordCodec.TryDecode(
                    span,
                    out var recordLength,
                    out _,
                    out _,
                    out _,
                    out _,
                    out var error))
            {
                FileSegmentStoreLogMessages.ClosedCorrupt(_logger, path, offset, error.ToString());
                throw new SegmentStoreCorruptException(
                    $"Closed/retired segment {segmentId} corrupt at offset {offset} ({error}).",
                    path);
            }

            offset += recordLength;
        }
    }

    private static bool TryReadUnlocked(
        SegmentRuntime runtime,
        in StoredArticleLocation location,
        ArticleId? expectedArtId,
        ulong? expectedArtHash,
        int? expectedArtSize,
        out ReadOnlyMemory<byte> artData)
    {
        artData = default;
        if (location.Offset < 0
            || location.Length < SegmentRecordCodec.MinimumRecordLength
            || location.Offset + location.Length > runtime.SizeBytes)
        {
            return false;
        }

        var buffer = new byte[location.Length];
        runtime.EnsureReadable();
        runtime.Stream.Seek(location.Offset, SeekOrigin.Begin);
        var read = runtime.Stream.Read(buffer, 0, buffer.Length);
        if (read != buffer.Length)
        {
            return false;
        }

        if (expectedArtId is null && expectedArtHash is null && expectedArtSize is null)
        {
            if (!SegmentRecordCodec.TryDecode(
                    buffer,
                    out var recordLength,
                    out _,
                    out _,
                    out _,
                    out artData,
                    out _))
            {
                return false;
            }

            return recordLength == buffer.Length;
        }

        return SegmentRecordCodec.TryValidateLocated(
            buffer,
            expectedArtId,
            expectedArtHash,
            expectedArtSize,
            out artData);
    }

    private sealed class FileAppendableSegment(FileSegmentStore owner) : IAppendableSegment
    {
        public SegmentId SegmentId => owner.GetActiveSegmentId();

        public long SizeBytes => owner.GetActiveSizeBytes();

        public ValueTask<StoredArticleLocation> AppendAsync(
            ReadOnlyMemory<byte> artData,
            CancellationToken cancellationToken) =>
            owner.AppendToActiveAsync(artData, cancellationToken);
    }

    private sealed class SegmentRuntime(
        SegmentId segmentId,
        SegmentState state,
        string path,
        FileStream? stream,
        long sizeBytes)
    {
        public SegmentId SegmentId { get; } = segmentId;

        public SegmentState State { get; set; } = state;

        public string Path { get; set; } = path;

        public FileStream Stream => _stream ?? throw new ObjectDisposedException(nameof(SegmentRuntime));

        public long SizeBytes { get; set; } = sizeBytes;

        private FileStream? _stream = stream;

        public void EnsureReadable()
        {
            if (_stream is not null)
            {
                return;
            }

            if (State == SegmentState.Retired)
            {
                throw new InvalidOperationException($"Segment {SegmentId} is Retired.");
            }

            OpenReadOnly();
        }

        public void OpenReadOnly()
        {
            _stream?.Dispose();
            _stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.None);
        }

        public void DisposeStream()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
