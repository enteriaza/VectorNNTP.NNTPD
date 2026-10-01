using System.Buffers.Binary;
using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;

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
/// Active segment: an incomplete final record is truncated on open. A complete record
/// with an invalid CRC fails closed, including when that record is the final record.
/// Closed and retired segments are catalogued from filename,
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
    private ActiveValidatedPrefix? _activeValidatedPrefix;
    private bool _disposed;

    /// <summary>
    /// Invoked after the segment record is written and before <see cref="FileStream.Flush(bool)"/>.
    /// Tests only. The production path is null.
    /// </summary>
    internal Action<FileStream, long, int>? TestAfterWriteBeforeFlush { get; set; }

    /// <summary>
    /// Invoked immediately before each durability <see cref="FileStream.Flush(bool)"/>.
    /// Tests only. A throw leaves the record undurable.
    /// </summary>
    internal Action? TestBeforeDurableFlush { get; set; }

    /// <summary>
    /// When set, torn-tail truncation throws instead of shrinking the file. Tests only.
    /// </summary>
    internal bool TestFailTailTruncate { get; set; }

    /// <summary>
    /// Invoked during a closed-segment accounting proof after the segment write gate has been
    /// released and the private read stream is open. Tests only. Must not be the runtime stream.
    /// </summary>
    internal Action? TestHookDuringClosedExtentScan { get; set; }

    /// <summary>
    /// Invoked once per eligible segment during candidate discovery, after the write gate
    /// is released and before that segment is read. Tests only. Must not be the runtime stream.
    /// </summary>
    internal Action? TestHookDuringProvenLocationScan { get; set; }

    /// <summary>
    /// Bytes read while proving the active append offset. Closed and retired discovery
    /// must leave this at zero. Tests use it as a regression guard.
    /// </summary>
    internal long DiscoveryPayloadBytesRead { get; private set; }

    /// <summary>
    /// Times candidate discovery sequentially decoded an Active segment.
    /// Stays zero when the repaired Active prefix supplies record boundaries.
    /// </summary>
    internal int ActiveCandidateSequentialWalkCount { get; private set; }

    /// <summary>
    /// Active record bytes re-read to prove payload identity after tail repair.
    /// Not a second framing walk, and not closed-segment bytes.
    /// </summary>
    internal long ActiveCandidatePayloadProofBytesRead { get; private set; }

    /// <summary>
    /// Optional replacement for the active-tail payload read. Tests only.
    /// Closed and retired discovery must not call it.
    /// </summary>
    internal Func<string, byte[]>? TestDiscoveryPayloadReader { get; set; }

    /// <summary>
    /// Invoked after the active discovery stream is opened and before <see cref="Stream.Seek"/>.
    /// Tests only. A throw must leave that stream unpublished.
    /// </summary>
    internal Action<FileStream>? TestBeforeActiveDiscoverySeek { get; set; }

    /// <summary>Published segment runtimes. Tests only. Readable after dispose.</summary>
    internal int TestSegmentRuntimeCount => _segments.Count;

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
        => OpenCore(options, logger: null, discoveryPayloadReader, configureBeforeDiscovery: null);

    /// <summary>
    /// Test entry that configures the store after construction and before discovery.
    /// </summary>
    internal static FileSegmentStore Open(
        ArticleStorageRuntimeOptions options,
        Action<FileSegmentStore> configureBeforeDiscovery)
        => OpenCore(options, logger: null, discoveryPayloadReader: null, configureBeforeDiscovery);

    private static FileSegmentStore OpenCore(
        ArticleStorageRuntimeOptions options,
        ILogger? logger,
        Func<string, byte[]>? discoveryPayloadReader,
        Action<FileSegmentStore>? configureBeforeDiscovery = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SegmentDir);

        var log = logger ?? NullLogger.Instance;
        Directory.CreateDirectory(options.SegmentDir);
        var store = new FileSegmentStore(options.SegmentDir, options.SegmentTargetSizeBytes, log);
        store.TestDiscoveryPayloadReader = discoveryPayloadReader;
        configureBeforeDiscovery?.Invoke(store);
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
            _activeValidatedPrefix = null;
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
            return ValueTask.FromResult(AppendToActiveUnlocked(artData).Location);
        }
    }

    /// <summary>
    /// Appends one record and reports whether a failed durability flush was reconciled
    /// because the complete record was already physically present.
    /// </summary>
    internal ValueTask<ActiveSegmentAppend> AppendToActiveReportingAsync(
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

    private ActiveSegmentAppend AppendToActiveUnlocked(ReadOnlyMemory<byte> artData)
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
        if (runtime.PendingRecord is not null)
        {
            return FinishPendingSegmentRecord(runtime, record, artId, artHash, artData.Length);
        }

        ReconcileBlockedSegmentTail(runtime);

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
        var lengthBefore = runtime.Stream.Length;
        if (lengthBefore != offset)
        {
            runtime.TailUnreconciled = true;
            runtime.TailValidEnd = offset;
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} length {lengthBefore} does not match logical size {offset}.",
                new IOException("Active segment cursor and file length diverged."));
        }

        try
        {
            runtime.Stream.Position = offset;
            runtime.Stream.Write(record, 0, record.Length);
            TestAfterWriteBeforeFlush?.Invoke(runtime.Stream, offset, record.Length);
            DurableSegmentFlush(runtime.Stream);
            return CommitActiveAppend(runtime, offset, record, ambiguousComplete: false);
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            var outcome = InspectSegmentAppend(runtime, offset, record);
            if (outcome == AmbiguousAppend.Growth.CompleteExpected)
            {
                if (runtime.Stream.Length > offset + record.Length)
                {
                    try
                    {
                        TruncateSegmentOrBlock(runtime, offset + record.Length, createdByThisCall: true);
                    }
                    catch (UnreconciledDurableTailException)
                    {
                        runtime.PendingRecord = new PendingSegmentRecord(offset, record, artId, artHash, artData.Length);
                        throw;
                    }
                }

                try
                {
                    DurableSegmentFlush(runtime.Stream);
                }
                catch (Exception flushEx) when (flushEx is not UnreconciledDurableTailException)
                {
                    runtime.PendingRecord = new PendingSegmentRecord(offset, record, artId, artHash, artData.Length);
                    throw new UnreconciledDurableTailException(
                        $"Active segment {runtime.SegmentId.Value} record is present but not durable.",
                        flushEx,
                        createdByThisCall: true);
                }

                return CommitActiveAppend(runtime, offset, record, ambiguousComplete: false);
            }

            if (outcome == AmbiguousAppend.Growth.IncompleteGrowth)
            {
                TruncateSegmentOrBlock(runtime, offset, createdByThisCall: true);
            }

            throw;
        }
    }

    private ActiveSegmentAppend FinishPendingSegmentRecord(
        SegmentRuntime runtime,
        byte[] record,
        ArticleId artId,
        ulong artHash,
        int artSize)
    {
        var pending = runtime.PendingRecord
            ?? throw new InvalidOperationException("No pending segment record.");
        if (pending.ArtId != artId
            || pending.ArtHash != artHash
            || pending.ArtSize != artSize
            || record.Length != pending.Length
            || XxHash3.HashToUInt64(record) != pending.PayloadHash)
        {
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} pending record does not match the retry payload.",
                new IOException("Pending durable segment payload identity mismatch."));
        }

        if (runtime.Stream.Length < pending.Offset + pending.Length)
        {
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} pending record is no longer present.",
                new IOException("Pending durable segment payload no longer matches the file."));
        }

        var observed = ReadExact(runtime.Stream, pending.Offset, pending.Length);
        if (observed.Length != pending.Length || XxHash3.HashToUInt64(observed) != pending.PayloadHash)
        {
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} pending record bytes changed before the durable flush.",
                new IOException("Pending durable segment payload no longer matches the file."));
        }

        try
        {
            DurableSegmentFlush(runtime.Stream);
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} record is present but not durable.",
                ex);
        }

        runtime.PendingRecord = null;
        return CommitActiveAppend(runtime, pending.Offset, record, ambiguousComplete: false);
    }

    private void DurableSegmentFlush(FileStream stream)
    {
        TestBeforeDurableFlush?.Invoke();
        stream.Flush(flushToDisk: true);
    }

    private ActiveSegmentAppend CommitActiveAppend(
        SegmentRuntime runtime,
        long offset,
        byte[] record,
        bool ambiguousComplete)
    {
        runtime.SizeBytes = offset + record.Length;
        _catalogue.RecordAppend(runtime.SegmentId, record.Length, runtime.SizeBytes);
        return new ActiveSegmentAppend(
            new StoredArticleLocation(runtime.SegmentId, offset, record.Length),
            ambiguousComplete);
    }

    private AmbiguousAppend.Growth InspectSegmentAppend(SegmentRuntime runtime, long start, byte[] record)
    {
        try
        {
            runtime.Stream.Flush(flushToDisk: false);
            var length = runtime.Stream.Length;
            var observed = length >= start + record.Length
                ? ReadExact(runtime.Stream, start, record.Length)
                : [];
            return AmbiguousAppend.Classify(start, length, record, observed);
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            runtime.TailUnreconciled = true;
            runtime.TailValidEnd = start;
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} length could not be inspected after an ambiguous append.",
                ex,
                createdByThisCall: true);
        }
    }

    private static byte[] ReadExact(FileStream stream, long offset, int length)
    {
        var buffer = new byte[length];
        stream.Position = offset;
        var filled = 0;
        while (filled < length)
        {
            var read = stream.Read(buffer, filled, length - filled);
            if (read == 0)
            {
                stream.Seek(0, SeekOrigin.End);
                return [];
            }

            filled += read;
        }

        stream.Seek(0, SeekOrigin.End);
        return buffer;
    }

    private void ReconcileBlockedSegmentTail(SegmentRuntime runtime)
    {
        if (!runtime.TailUnreconciled)
        {
            return;
        }

        TruncateSegmentOrBlock(runtime, runtime.TailValidEnd, createdByThisCall: false);
    }

    private void TruncateSegmentOrBlock(SegmentRuntime runtime, long validEnd, bool createdByThisCall)
    {
        if (TestFailTailTruncate)
        {
            runtime.TailUnreconciled = true;
            runtime.TailValidEnd = validEnd;
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} tail could not be reconciled after an ambiguous append.",
                new IOException("truncate-failed"),
                createdByThisCall);
        }

        try
        {
            if (runtime.Stream.Length != validEnd)
            {
                FileSegmentStoreLogMessages.TruncatingTornTail(
                    _logger,
                    runtime.SegmentId.Value,
                    validEnd,
                    runtime.Stream.Length,
                    "ambiguous-append");
                runtime.Stream.SetLength(validEnd);
                runtime.Stream.Flush(flushToDisk: true);
            }

            runtime.SizeBytes = validEnd;
            runtime.TailUnreconciled = false;
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            runtime.TailUnreconciled = true;
            runtime.TailValidEnd = validEnd;
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} tail could not be reconciled after an ambiguous append.",
                ex,
                createdByThisCall);
        }
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

        if (runtime.PendingRecord is not null || runtime.TailUnreconciled)
        {
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} cannot close while a physical append is pending.",
                new IOException("pending-segment-record"));
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

            if (state == SegmentState.Active)
            {
                var opened = new FileStream(
                    item.Path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.None);
                try
                {
                    TestBeforeActiveDiscoverySeek?.Invoke(opened);
                    opened.Seek(0, SeekOrigin.End);
                    _activeSegmentId = item.Id.Value;
                    _segments[item.Id.Value] = new SegmentRuntime(
                        item.Id,
                        state,
                        item.Path,
                        opened,
                        validLength);
                    opened = null;
                }
                finally
                {
                    opened?.Dispose();
                }
            }
            else
            {
                FileStream? stream = null;
                if (state == SegmentState.Closed)
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
            PublishActiveValidatedPrefix(segmentId, sizeBytes: 0, []);
            return 0;
        }

        if (fileLength > int.MaxValue)
        {
            throw new SegmentStoreCorruptException(
                $"Active segment {segmentId} exceeds supported size.",
                path);
        }

        var bytes = ReadDiscoveryPayload(path);
        var records = new List<ActiveValidatedRecord>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var span = bytes.AsSpan(offset);
            if (SegmentRecordCodec.TryDecode(
                    span,
                    out var recordLength,
                    out var artId,
                    out var artHash,
                    out var artSize,
                    out _,
                    out var error))
            {
                // Identity and bounds only. The decoded payload is not retained.
                records.Add(new ActiveValidatedRecord(offset, recordLength, artId, artHash, artSize));
                offset += recordLength;
                continue;
            }

            if (error == SegmentRecordCodec.DecodeError.Incomplete
                || (error == SegmentRecordCodec.DecodeError.Corrupt
                    && offset + Math.Max(recordLength, 0) >= bytes.Length))
            {
                // Incomplete declared bytes, or a CRC-valid record that fails a later field check
                // and reaches EOF. A complete record with a bad CRC is not this case.
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
                PublishActiveValidatedPrefix(segmentId, offset, records);
                return offset;
            }

            // A complete record with a bad CRC, an illegal length, or corruption with bytes after
            // it fails closed. The prefix is not published, so a failed repair cannot become candidates.
            FileSegmentStoreLogMessages.ClosedCorrupt(_logger, path, offset, error.ToString());
            throw new SegmentStoreCorruptException(
                $"Active segment {segmentId} corrupt at offset {offset} ({error}).",
                path);
        }

        PublishActiveValidatedPrefix(segmentId, offset, records);
        return offset;
    }

    /// <summary>
    /// Drops the repaired Active prefix. Candidate discovery also drops it when the
    /// snapshot is taken. Call this when startup recovery has no Accept-only targets.
    /// </summary>
    internal void DiscardActiveRepairPrefix()
    {
        lock (_writeGate)
        {
            _activeValidatedPrefix = null;
        }
    }

    private void PublishActiveValidatedPrefix(
        SegmentId segmentId,
        long sizeBytes,
        IReadOnlyList<ActiveValidatedRecord> records)
    {
        var copy = records.Count == 0 ? [] : new ActiveValidatedRecord[records.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = records[i];
        }

        _activeValidatedPrefix = new ActiveValidatedPrefix(segmentId.Value, sizeBytes, copy);
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
    /// One Accept identity for a single physical walk. <see cref="ArtData"/> aliases the
    /// caller's bytes; this value does not copy them.
    /// </summary>
    /// <param name="Sequence">Journal sequence that owns the identity.</param>
    /// <param name="ArtId">Article identity.</param>
    /// <param name="ArtHash">Article hash.</param>
    /// <param name="ArtSize">Article size.</param>
    /// <param name="ArtData">Canonical payload used for the byte-for-byte proof.</param>
    internal readonly record struct AcceptOnlyMatchTarget(
        ulong Sequence,
        ArticleId ArtId,
        ulong ArtHash,
        int ArtSize,
        ReadOnlyMemory<byte> ArtData);

    /// <summary>
    /// Locations whose decoded payload is exactly <paramref name="artData"/> for the identity.
    /// Ordered by segment id, then offset. Skips retired segments. Stops a segment at the
    /// first undecodable record and does not adopt anything past it.
    /// The segment write gate is held only to snapshot eligible path and size. Each segment
    /// is then read on a private stream over <c>[0, SizeBytes)</c>.
    /// </summary>
    internal List<StoredArticleLocation> FindProvenLocations(
        ArticleId artId,
        ulong artHash,
        int artSize,
        ReadOnlySpan<byte> artData)
    {
        var found = FindAcceptOnlyCandidates(
            [new AcceptOnlyMatchTarget(0, artId, artHash, artSize, artData.ToArray())]);
        return found.TryGetValue(0, out var matches) ? matches : [];
    }

    /// <summary>
    /// One walk of every non-retired segment with <c>SizeBytes &gt; 0</c>. Each decoded record
    /// is matched against <paramref name="accepts"/> with the same checksum, identity, and
    /// payload rule as <see cref="FindProvenLocations"/>. Every sequence is present in the
    /// result. Lists are ordered by segment id, then offset. The walk does not mutate the
    /// journal, index, catalogue, or segment state. An undecodable record stops that segment
    /// only. <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> propagate.
    /// An Active segment whose snapshotted size is still the repaired prefix is not decoded
    /// again. Records whose repaired identity cannot match are not re-read. A possible match
    /// is proved by reading that record, including its checksum and full payload.
    /// </summary>
    /// <param name="accepts">Accept-only identities. Payloads are not copied or retained.</param>
    internal Dictionary<ulong, List<StoredArticleLocation>> FindAcceptOnlyCandidates(
        IReadOnlyList<AcceptOnlyMatchTarget> accepts)
    {
        var results = new Dictionary<ulong, List<StoredArticleLocation>>(accepts.Count);
        if (accepts.Count == 0)
        {
            DiscardActiveRepairPrefix();
            return results;
        }

        var byArtId = new Dictionary<ArticleId, List<int>>();
        for (var i = 0; i < accepts.Count; i++)
        {
            var accept = accepts[i];
            if (!results.ContainsKey(accept.Sequence))
            {
                results[accept.Sequence] = [];
            }

            if (!byArtId.TryGetValue(accept.ArtId, out var indexes))
            {
                indexes = [];
                byArtId[accept.ArtId] = indexes;
            }

            indexes.Add(i);
        }

        ProvenLocationScanTarget[] targets;
        ActiveValidatedPrefix? repairPrefix;
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            repairPrefix = _activeValidatedPrefix;
            _activeValidatedPrefix = null;
            targets = _segments.Values
                .Where(static runtime => runtime.State != SegmentState.Retired && runtime.SizeBytes > 0)
                .OrderBy(static runtime => runtime.SegmentId.Value)
                .Select(static runtime => new ProvenLocationScanTarget(
                    runtime.SegmentId,
                    runtime.Path,
                    runtime.State,
                    runtime.SizeBytes))
                .ToArray();
        }

        foreach (var target in targets)
        {
            if (target.State == SegmentState.Retired)
            {
                continue;
            }

            if (repairPrefix is not null
                && target.State == SegmentState.Active
                && target.SegmentId.Value == repairPrefix.SegmentId
                && target.SizeBytes == repairPrefix.SizeBytes)
            {
                MatchActiveRepairPrefix(target, repairPrefix, accepts, byArtId, results);
                continue;
            }

            if (target.State == SegmentState.Active)
            {
                ActiveCandidateSequentialWalkCount++;
            }

            using var stream = new FileStream(
                target.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.None);
            TestHookDuringProvenLocationScan?.Invoke();
            long offset = 0;
            while (offset < target.SizeBytes)
            {
                if (!TryReadProvenAt(
                        stream,
                        target.SegmentId,
                        target.SizeBytes,
                        offset,
                        out var extent,
                        out var payload,
                        out var consumed))
                {
                    break;
                }

                if (byArtId.TryGetValue(extent.ArtId, out var indexes))
                {
                    foreach (var index in indexes)
                    {
                        var accept = accepts[index];
                        if (extent.ArtHash == accept.ArtHash
                            && extent.ArtSize == accept.ArtSize
                            && payload.Span.SequenceEqual(accept.ArtData.Span))
                        {
                            results[accept.Sequence].Add(extent.Location);
                        }
                    }
                }

                offset += consumed;
            }
        }

        foreach (var matches in results.Values)
        {
            matches.Sort(static (left, right) =>
            {
                var segment = left.SegmentId.Value.CompareTo(right.SegmentId.Value);
                return segment != 0 ? segment : left.Offset.CompareTo(right.Offset);
            });
        }

        return results;
    }

    /// <summary>
    /// Proves Active candidates from the repaired prefix. Non-matching records are not read.
    /// A matching record is checksummed and compared byte for byte. A failed read stops the
    /// remainder of this prefix, which is the same boundary as the sequential walk.
    /// </summary>
    private void MatchActiveRepairPrefix(
        ProvenLocationScanTarget target,
        ActiveValidatedPrefix repairPrefix,
        IReadOnlyList<AcceptOnlyMatchTarget> accepts,
        Dictionary<ArticleId, List<int>> byArtId,
        Dictionary<ulong, List<StoredArticleLocation>> results)
    {
        TestHookDuringProvenLocationScan?.Invoke();
        FileStream? stream = null;
        try
        {
            foreach (var record in repairPrefix.Records)
            {
                if (record.Offset < 0
                    || record.Length <= 0
                    || record.Offset + record.Length > target.SizeBytes)
                {
                    break;
                }

                if (!byArtId.TryGetValue(record.ArtId, out var indexes))
                {
                    continue;
                }

                var identityHit = false;
                foreach (var index in indexes)
                {
                    var accept = accepts[index];
                    if (record.ArtHash == accept.ArtHash && record.ArtSize == accept.ArtSize)
                    {
                        identityHit = true;
                        break;
                    }
                }

                if (!identityHit)
                {
                    continue;
                }

                stream ??= new FileStream(
                    target.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.None);
                ActiveCandidatePayloadProofBytesRead += record.Length;
                if (!TryReadProvenAt(
                        stream,
                        target.SegmentId,
                        target.SizeBytes,
                        record.Offset,
                        out var extent,
                        out var payload,
                        out var consumed)
                    || consumed != record.Length
                    || extent.Location.Offset != record.Offset)
                {
                    break;
                }

                if (!byArtId.TryGetValue(extent.ArtId, out var freshIndexes))
                {
                    continue;
                }

                foreach (var index in freshIndexes)
                {
                    var accept = accepts[index];
                    if (extent.ArtHash == accept.ArtHash
                        && extent.ArtSize == accept.ArtSize
                        && payload.Span.SequenceEqual(accept.ArtData.Span))
                    {
                        results[accept.Sequence].Add(extent.Location);
                    }
                }
            }
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>
    /// True when any segment still has a process-local pending record or an unreconciled tail.
    /// Those bytes are not yet a committed <c>SizeBytes</c> prefix, so an Accept that has not
    /// itself written must not skip orphan discovery while they exist.
    /// </summary>
    internal bool HasPendingOrUnreconciledTail()
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var runtime in _segments.Values)
            {
                if (runtime.PendingRecord is not null || runtime.TailUnreconciled)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private readonly record struct ProvenLocationScanTarget(
        SegmentId SegmentId,
        string Path,
        SegmentState State,
        long SizeBytes);

    /// <summary>
    /// One checksum-valid Active record observed by tail repair. Payload bytes are not stored.
    /// </summary>
    private readonly record struct ActiveValidatedRecord(
        long Offset,
        int Length,
        ArticleId ArtId,
        ulong ArtHash,
        int ArtSize);

    /// <summary>
    /// Repaired Active prefix. Used only while <see cref="SegmentRuntime.SizeBytes"/> is still
    /// that repaired length. Dropped at the candidate snapshot so later appends are not candidates.
    /// </summary>
    private sealed class ActiveValidatedPrefix
    {
        internal ActiveValidatedPrefix(ulong segmentId, long sizeBytes, ActiveValidatedRecord[] records)
        {
            SegmentId = segmentId;
            SizeBytes = sizeBytes;
            Records = records;
        }

        internal ulong SegmentId { get; }

        internal long SizeBytes { get; }

        internal ActiveValidatedRecord[] Records { get; }
    }

    /// <summary>
    /// Proves every physical record in a Closed segment, from offset 0 through the
    /// size observed under the segment write gate. Active and Retired segments are not walked.
    /// The gate is not held during the read. A failed proof returns false and an empty list;
    /// partial results are not published.
    /// </summary>
    internal bool TryReadClosedProvedExtents(SegmentId segmentId, out List<ProvenSegmentExtent> extents)
    {
        extents = [];
        string path;
        long sizeBytes;
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_segments.TryGetValue(segmentId.Value, out var runtime)
                || runtime.State != SegmentState.Closed)
            {
                return false;
            }

            if (runtime.SizeBytes == 0)
            {
                return true;
            }

            path = runtime.Path;
            sizeBytes = runtime.SizeBytes;
        }

        var found = new List<ProvenSegmentExtent>();
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.None);
            TestHookDuringClosedExtentScan?.Invoke();
            long offset = 0;
            while (offset < sizeBytes)
            {
                if (!TryReadProvenAt(
                        stream,
                        segmentId,
                        sizeBytes,
                        offset,
                        out var extent,
                        out _,
                        out var consumed)
                    || consumed <= 0
                    || extent.Location.Offset != offset
                    || extent.Location.Length != consumed)
                {
                    return false;
                }

                found.Add(extent);
                offset += consumed;
            }

            if (offset != sizeBytes)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        lock (_writeGate)
        {
            if (_disposed)
            {
                return false;
            }
        }

        extents = found;
        return true;
    }

    private static bool TryReadProvenAt(
        FileStream stream,
        SegmentId segmentId,
        long sizeBytes,
        long offset,
        out ProvenSegmentExtent extent,
        out ReadOnlyMemory<byte> payload,
        out int consumed)
    {
        extent = default;
        payload = default;
        consumed = 0;
        if (offset < 0 || offset + 4 > sizeBytes)
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
            || offset + total > sizeBytes)
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
            new StoredArticleLocation(segmentId, offset, recordLength),
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

        public bool TailUnreconciled { get; set; }

        public long TailValidEnd { get; set; }

        public PendingSegmentRecord? PendingRecord { get; set; }

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

/// <summary>
/// Process-local complete segment record whose durability flush has not returned.
/// The article identity is the logical owner. Not restored after restart.
/// </summary>
internal sealed class PendingSegmentRecord
{
    internal PendingSegmentRecord(long offset, byte[] payload, ArticleId artId, ulong artHash, int artSize)
    {
        Offset = offset;
        Length = payload.Length;
        PayloadHash = XxHash3.HashToUInt64(payload);
        ArtId = artId;
        ArtHash = artHash;
        ArtSize = artSize;
    }

    internal long Offset { get; }

    internal int Length { get; }

    internal ulong PayloadHash { get; }

    internal ArticleId ArtId { get; }

    internal ulong ArtHash { get; }

    internal int ArtSize { get; }
}

internal readonly record struct ActiveSegmentAppend(
    StoredArticleLocation Location,
    bool AmbiguousComplete);

/// <summary>One proven segment record. Payload is not retained.</summary>
internal readonly record struct ProvenSegmentExtent(
    StoredArticleLocation Location,
    ArticleId ArtId,
    ulong ArtHash,
    int ArtSize);
