using System.Buffers.Binary;
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
/// Active segment: incomplete final record is truncated on open, and corruption before
/// the valid end fails closed. Closed and retired segments are catalogued from filename,
/// lifecycle, and file length only; payload CRC is proved on the targeted read path.
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

    /// <summary>
    /// Bytes read while proving the active append offset. Closed and retired discovery
    /// must leave this at zero. Tests use it as a regression guard.
    /// </summary>
    internal long DiscoveryPayloadBytesRead { get; private set; }

    /// <summary>
    /// Optional replacement for the active-tail payload read. Tests only.
    /// Closed and retired discovery must not call it.
    /// </summary>
    internal Func<string, byte[]>? TestDiscoveryPayloadReader { get; set; }

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
    /// discovers segment identity and lifecycle, and repairs only the active torn tail.
    /// Closed and retired payload bytes are not scanned here.
    /// </summary>
    public static FileSegmentStore Open(
        ArticleStorageRuntimeOptions options,
        ILogger? logger = null)
        => OpenCore(options, logger, discoveryPayloadReader: null);

    /// <summary>
    /// Test entry that installs <paramref name="discoveryPayloadReader"/> before discovery.
    /// Closed and retired files must not be passed to the reader.
    /// </summary>
    internal static FileSegmentStore Open(
        ArticleStorageRuntimeOptions options,
        Func<string, byte[]> discoveryPayloadReader)
        => OpenCore(options, logger: null, discoveryPayloadReader);

    private static FileSegmentStore OpenCore(
        ArticleStorageRuntimeOptions options,
        ILogger? logger,
        Func<string, byte[]>? discoveryPayloadReader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SegmentDir);

        var log = logger ?? NullLogger.Instance;
        Directory.CreateDirectory(options.SegmentDir);
        var store = new FileSegmentStore(options.SegmentDir, options.SegmentTargetSizeBytes, log);
        store.TestDiscoveryPayloadReader = discoveryPayloadReader;
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

    /// <summary>Gets the segment directory root.</summary>
    public string RootPath => _root;

    /// <summary>Gets catalogue info for tests.</summary>
    public bool TryGetSegmentInfo(SegmentId segmentId, out SegmentInfo info) =>
        _catalogue.TryGet(segmentId, out info);

    /// <summary>
    /// Deletes the exact <c>.retired</c> file for <paramref name="segmentId"/> then removes the
    /// in-memory catalogue/runtime entry (Option A: physical delete first).
    /// </summary>
    /// <remarks>
    /// Catalogue is reconstructed from disk files on Open, so a crash after delete and before
    /// catalogue removal still yields a permanently absent segment on restart.
    /// </remarks>
    public bool TryReclaimRetired(
        SegmentId segmentId,
        out string? failureReason,
        Action? afterDeleteBeforeCatalogueRemove = null)
    {
        failureReason = null;
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var retiredPath = Path.Combine(_root, SegmentFileNames.Format(segmentId, SegmentFileKind.Retired));
            var activePath = Path.Combine(_root, SegmentFileNames.Format(segmentId, SegmentFileKind.Active));
            var closedPath = Path.Combine(_root, SegmentFileNames.Format(segmentId, SegmentFileKind.Closed));

            if (File.Exists(activePath) || File.Exists(closedPath))
            {
                failureReason = "unexpected-active-or-closed-file";
                return false;
            }

            if (!_catalogue.TryGet(segmentId, out var info))
            {
                if (!File.Exists(retiredPath))
                {
                    failureReason = "already-reclaimed";
                    return true;
                }

                failureReason = "catalogue-missing-with-retired-file";
                return false;
            }

            if (info.State == SegmentState.Active)
            {
                failureReason = "segment-active";
                return false;
            }

            if (info.State == SegmentState.Closed)
            {
                failureReason = "segment-closed";
                return false;
            }

            if (info.State != SegmentState.Retired)
            {
                failureReason = "segment-not-retired";
                return false;
            }

            if (_segments.TryGetValue(segmentId.Value, out var runtime))
            {
                if (runtime.State != SegmentState.Retired)
                {
                    failureReason = "runtime-not-retired";
                    return false;
                }

                if (!string.Equals(runtime.Path, retiredPath, StringComparison.OrdinalIgnoreCase))
                {
                    failureReason = "runtime-path-not-retired";
                    return false;
                }

                runtime.DisposeStream();
            }
            else if (!File.Exists(retiredPath))
            {
                // Catalogue Retired but runtime/file already gone — finish catalogue cleanup.
                return _catalogue.TryRemoveRetired(segmentId);
            }

            if (!File.Exists(retiredPath))
            {
                // Catalogue still has Retired but file already deleted (crash window).
                _ = _segments.Remove(segmentId.Value);
                return _catalogue.TryRemoveRetired(segmentId);
            }

            try
            {
                File.Delete(retiredPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failureReason = "delete-failed:" + ex.GetType().Name;
                return false;
            }

            afterDeleteBeforeCatalogueRemove?.Invoke();

            _ = _segments.Remove(segmentId.Value);
            if (!_catalogue.TryRemoveRetired(segmentId))
            {
                failureReason = "catalogue-remove-failed";
                return false;
            }

            FileSegmentStoreLogMessages.Reclaimed(_logger, segmentId.Value);
            return true;
        }
    }

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
        var seenIds = new HashSet<ulong>();
        foreach (var path in Directory.EnumerateFiles(_root, "seg-*"))
        {
            var name = Path.GetFileName(path);
            if (!SegmentFileNames.TryParse(name, out var segmentId, out var kind))
            {
                throw new SegmentStoreCorruptException(
                    $"Malformed segment filename '{name}'.",
                    path);
            }

            if (!seenIds.Add(segmentId.Value))
            {
                throw new SegmentStoreCorruptException(
                    $"Duplicate segment id {segmentId.Value} under '{_root}'.",
                    path);
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
            else if (state == SegmentState.Retired)
            {
                // Filename and length are the catalogue facts. Opening the handle proves the
                // file is accessible without reading payload bytes. TryRead never serves Retired.
                using var probe = new FileStream(
                    item.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 1,
                    FileOptions.None);
                if (probe.Length != item.Length)
                {
                    throw new SegmentStoreCorruptException(
                        $"Retired segment {item.Id} length changed during discovery.",
                        item.Path);
                }
            }

            var generation = _catalogue.AllocateGeneration();
            catalogueEntries.Add(new SegmentInfo(
                item.Id,
                state,
                generation,
                validLength,
                LiveBytes: 0,
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

        var bytes = ReadDiscoveryPayload(path);
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

    /// <summary>
    /// Reads an active segment for tail repair. This is the only discovery path that
    /// loads payload bytes.
    /// </summary>
    private byte[] ReadDiscoveryPayload(string path)
    {
        var reader = TestDiscoveryPayloadReader;
        var bytes = reader is not null ? reader(path) : File.ReadAllBytes(path);
        DiscoveryPayloadBytesRead += bytes.LongLength;
        return bytes;
    }

    /// <summary>
    /// Locations whose decoded payload is exactly <paramref name="artData"/> for the identity.
    /// Ordered by segment id, then offset. Skips retired segments. Stops a segment at the
    /// first undecodable record and does not adopt anything past it.
    /// </summary>
    internal List<StoredArticleLocation> FindProvenLocations(
        ArticleId artId,
        ulong artHash,
        int artSize,
        ReadOnlySpan<byte> artData)
    {
        var matches = new List<StoredArticleLocation>();
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            VisitProvenUnlocked(static (extent, payload, state) =>
            {
                var (id, hash, size, expected, found) = state;
                if (!extent.ArtId.Equals(id)
                    || extent.ArtHash != hash
                    || extent.ArtSize != size
                    || !payload.Span.SequenceEqual(expected))
                {
                    return;
                }

                found.Add(extent.Location);
            }, (artId, artHash, artSize, artData.ToArray(), matches));
        }

        matches.Sort(static (left, right) =>
        {
            var segment = left.SegmentId.Value.CompareTo(right.SegmentId.Value);
            return segment != 0 ? segment : left.Offset.CompareTo(right.Offset);
        });
        return matches;
    }

    /// <summary>
    /// Every record that decodes and proves its own header. Does not filter by caller identity.
    /// Retired segments are skipped. A corrupt record ends that segment's walk.
    /// </summary>
    internal List<ProvenSegmentExtent> EnumerateProvenExtents()
    {
        var extents = new List<ProvenSegmentExtent>();
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            VisitProvenUnlocked(static (extent, _, found) => found.Add(extent), extents);
        }

        return extents;
    }

    private void VisitProvenUnlocked<TState>(Action<ProvenSegmentExtent, ReadOnlyMemory<byte>, TState> visit, TState state)
    {
        foreach (var runtime in _segments.Values.OrderBy(static runtime => runtime.SegmentId.Value))
        {
            if (runtime.State == SegmentState.Retired || runtime.SizeBytes <= 0)
            {
                continue;
            }

            runtime.EnsureReadable();
            var stream = runtime.Stream;
            var restore = stream.Position;
            try
            {
                long offset = 0;
                while (offset < runtime.SizeBytes)
                {
                    if (!TryReadProvenAtUnlocked(stream, runtime, offset, out var extent, out var payload, out var consumed))
                    {
                        break;
                    }

                    visit(extent, payload, state);
                    offset += consumed;
                }
            }
            finally
            {
                stream.Seek(restore, SeekOrigin.Begin);
            }
        }
    }

    private static bool TryReadProvenAtUnlocked(
        FileStream stream,
        SegmentRuntime runtime,
        long offset,
        out ProvenSegmentExtent extent,
        out ReadOnlyMemory<byte> payload,
        out int consumed)
    {
        extent = default;
        payload = default;
        consumed = 0;
        if (offset < 0 || offset + 4 > runtime.SizeBytes)
        {
            return false;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        Span<byte> lengthBytes = stackalloc byte[4];
        if (stream.Read(lengthBytes) != 4)
        {
            return false;
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (total < SegmentRecordCodec.MinimumRecordLength
            || total > SegmentRecordCodec.MaxRecordLength
            || offset + total > runtime.SizeBytes)
        {
            return false;
        }

        var buffer = new byte[total];
        stream.Seek(offset, SeekOrigin.Begin);
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
        {
            return false;
        }

        if (!SegmentRecordCodec.TryDecode(
                buffer,
                out var recordLength,
                out var artId,
                out var artHash,
                out var artSize,
                out payload,
                out _))
        {
            return false;
        }

        if (recordLength != buffer.Length)
        {
            return false;
        }

        extent = new ProvenSegmentExtent(
            new StoredArticleLocation(runtime.SegmentId, offset, recordLength),
            artId,
            artHash,
            artSize);
        consumed = recordLength;
        return true;
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

/// <summary>One proven segment record. Payload is not retained.</summary>
internal readonly record struct ProvenSegmentExtent(
    StoredArticleLocation Location,
    ArticleId ArtId,
    ulong ArtHash,
    int ArtSize);
