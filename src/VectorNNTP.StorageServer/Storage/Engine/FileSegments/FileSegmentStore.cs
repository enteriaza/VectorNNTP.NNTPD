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
/// Segment files: <c>seg-{id:D20}.{active|closed|retired}</c>. The configured active-segment
/// count (1, 2, or 4) is how many of those files may accept appends at once. Each active
/// file has its own segment id and its own writer lock. Closed/Retired segments are immutable.
/// Append durability uses
/// <see cref="FileStream.Flush(bool)"/> with <c>flushToDisk: true</c>.
/// </para>
/// <para>
/// Acceptance durability remains the NVMe journal. This store only provides physical
/// article persistence and location identity (<see cref="StoredArticleLocation"/>).
/// </para>
/// <para>
/// Active segment repair reads one record at a time from a long file offset. Each read
/// is bounded by the maximum physical record length. An incomplete final record is
/// truncated on open. A complete record with an invalid CRC fails closed, including
/// when that record is the final record. A segment is not rejected because its total
/// length exceeds <see cref="int.MaxValue"/>.
/// Closed and retired segments are catalogued from filename,
/// lifecycle, and file length only; payload CRC is proved on the targeted read path.
/// </para>
/// <para>
/// An active segment seals when the next article would exceed the size target, or when
/// <see cref="ArticleStorageRuntimeOptions.MaxSegmentSealDelay"/> has elapsed since that
/// segment's first durable article. <see cref="TimeSpan.Zero"/> disables the age condition.
/// The activation instant is the catalogue <c>CreatedUtc</c> and is stored in
/// <c>segment-activation</c> so a restart continues the same deadline. The open segment has
/// one <see cref="TimeProvider"/> delay, keyed to that segment id. The wait is monotonic.
/// A wall clock behind the saved instant does not add the backward step; the wait is at most
/// the configured delay, and the segment seals when that wait completes. An empty segment is
/// not sealed by the delay. Age sealing calls the same close as a size rollover. A delay that
/// fires for a segment which is no longer active does nothing.
/// </para>
/// </remarks>
public sealed class FileSegmentStore : ISegmentStore, IDisposable, IAsyncDisposable
{
    private readonly long _targetSegmentBytes;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxSealDelay;
    private readonly object _writeGate = new();
    private readonly Dictionary<ulong, SegmentRuntime> _segments = new();
    private readonly FileSegmentCatalogue _catalogue = new();
    private readonly string _root;
    private readonly int _activeSegmentCount;
    private readonly List<ulong> _activeWriterIds = new();
    private readonly Dictionary<ulong, CancellationTokenSource> _ageSealSources = new();
    private readonly Dictionary<ulong, Task> _ageSealTasks = new();
    private ulong _nextSegmentId = 1;
    private ulong? _activeSegmentId;
    private ActiveValidatedPrefix? _activeValidatedPrefix;
    private bool _disposed;
    private CancellationTokenSource? _ageSealCts;
    private Task _ageSealTask = Task.CompletedTask;
    private ulong? _ageSealSegmentId;
    private long _sealByAgeCount;
    private long _sealBySizeCount;

    /// <summary>File under the segment root that records the active segment's activation instant.</summary>
    private const string ActivationFileName = "segment-activation";

    /// <summary>
    /// Largest delay <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> accepts.
    /// Longer configured delays are split into successive waits on the same segment timer.
    /// </summary>
    private static readonly TimeSpan MaxSingleWait = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    /// <summary>
    /// Invoked after the segment record is written and before <see cref="FileStream.Flush(bool)"/>.
    /// Tests only. The production path is null.
    /// </summary>
    internal Action<FileStream, long, int>? TestAfterWriteBeforeFlush { get; set; }

    /// <summary>
    /// Invoked once inside <see cref="CloseActiveUnlocked"/> before the active file is renamed.
    /// Tests only. Cleared before invoke. Runs while the segment write gate is held.
    /// </summary>
    internal Action<ulong>? TestHookBeforeActiveClose { get; set; }

    /// <summary>Seals caused by <see cref="ArticleStorageRuntimeOptions.MaxSegmentSealDelay"/>.</summary>
    internal long SegmentSealByAgeCount => Volatile.Read(ref _sealByAgeCount);

    /// <summary>Seals caused by <see cref="ArticleStorageRuntimeOptions.SegmentTargetSizeBytes"/>.</summary>
    internal long SegmentSealBySizeCount => Volatile.Read(ref _sealBySizeCount);

    /// <summary>
    /// Elapsed time since the active segment's first durable article.
    /// Null when there is no active segment or it has not yet stored an article.
    /// A clock behind the activation instant reports zero.
    /// </summary>
    internal TimeSpan? ActiveSegmentAge
    {
        get
        {
            lock (_writeGate)
            {
                if (_activeSegmentId is not { } id
                    || !_segments.TryGetValue(id, out var runtime)
                    || runtime.ActivatedUtc is not DateTimeOffset activated)
                {
                    return null;
                }

                var now = _time.GetUtcNow();
                if (now <= activated)
                {
                    return TimeSpan.Zero;
                }

                return now - activated;
            }
        }
    }

    /// <summary>Tasks for every armed age delay. Tests wait on this snapshot.</summary>
    internal Task[] AgeSealTasks
    {
        get
        {
            lock (_writeGate)
            {
                return _ageSealTasks.Values.ToArray();
            }
        }
    }

    /// <summary>Task for the current active segment's age delay. Completed when no delay is armed.</summary>
    internal Task AgeSealTask
    {
        get
        {
            lock (_writeGate)
            {
                return _ageSealTask;
            }
        }
    }

    /// <summary>
    /// Invoked immediately before each durability <see cref="FileStream.Flush(bool)"/>.
    /// Tests only. A throw leaves the record undurable.
    /// </summary>
    internal Action? TestBeforeDurableFlush { get; set; }

    /// <summary>
    /// Invoked immediately after each durability <see cref="FileStream.Flush(bool)"/> returns.
    /// Tests only.
    /// </summary>
    internal Action? TestAfterDurableFlush { get; set; }

    /// <summary>Number of durability flushes of an active segment stream. Tests only.</summary>
    internal long DurableFlushCount => Volatile.Read(ref _durableFlushCount);

    /// <summary>
    /// Full-record read proofs performed by <see cref="TryProveStoredLocation"/>.
    /// A flushed append confirmed from its header does not increment this.
    /// </summary>
    internal long PayloadLocationProofCount => Volatile.Read(ref _payloadLocationProofCount);

    /// <summary>
    /// Header confirms of a record this process already flushed.
    /// Each confirm reads the fixed header and does not read the payload.
    /// </summary>
    internal long FlushedHeaderConfirmCount => Volatile.Read(ref _flushedHeaderConfirmCount);

    private long _durableFlushCount;

    private long _payloadLocationProofCount;

    private long _flushedHeaderConfirmCount;
    private int _deferDurableFlush;
    private bool _unflushedCommittedAppend;

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
    /// Durably reserves the next segment id before the file is created.
    /// Null keeps file-derived allocation for store-only tests. The engine always sets this.
    /// </summary>
    internal Action<ulong>? ReserveSegmentId { get; set; }

    /// <summary>
    /// Invoked after the segment id is durably reserved and before the file is created.
    /// Tests only. A throw leaves the id reserved and the file absent.
    /// </summary>
    internal Action<ulong>? TestHookAfterSegmentIdReserved { get; set; }

    /// <summary>
    /// Raises the next segment id to <paramref name="nextSegmentId"/> when that value is higher.
    /// Zero is ignored. Never lowers the allocator.
    /// </summary>
    internal void AdoptSegmentIdFloor(ulong nextSegmentId)
    {
        if (nextSegmentId == 0)
        {
            return;
        }

        lock (_writeGate)
        {
            if (nextSegmentId > _nextSegmentId)
            {
                _nextSegmentId = nextSegmentId;
            }
        }
    }

    /// <summary>
    /// Bytes read while proving the active append offset. Closed and retired discovery
    /// must leave this at zero. Tests use it as a regression guard.
    /// </summary>
    internal long DiscoveryPayloadBytesRead { get; private set; }

    /// <summary>
    /// Largest single record buffer allocated while repairing the active tail.
    /// Stays at zero when discovery does not read an active payload.
    /// </summary>
    internal int ActiveRepairMaxRecordBytes { get; private set; }

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
    /// Optional replacement for the active-tail bytes. Tests only.
    /// The buffer is scanned with the same record walk as a file. Closed and retired
    /// discovery must not call it.
    /// </summary>
    internal Func<string, byte[]>? TestDiscoveryPayloadReader { get; set; }

    /// <summary>
    /// Optional replacement for the active-tail stream. Tests only.
    /// Production repair opens the segment file and reads one bounded record at a time.
    /// </summary>
    internal Func<string, FileStream>? TestActiveRepairStreamFactory { get; set; }

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
        ILogger logger,
        TimeProvider time,
        TimeSpan maxSealDelay,
        int activeSegmentCount)
    {
        if (maxSealDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSealDelay),
                "Maximum segment seal delay cannot be negative.");
        }

        if (activeSegmentCount is not 1 and not 2 and not 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeSegmentCount),
                "Active segment count must be 1, 2, or 4.");
        }

        _root = root;
        _targetSegmentBytes = targetSegmentBytes;
        _logger = logger;
        _time = time;
        _maxSealDelay = maxSealDelay;
        _activeSegmentCount = activeSegmentCount;
    }

    /// <summary>Gets the segment root directory (CacheDir / SegmentDir).</summary>
    public string SegmentRoot => _root;

    /// <summary>Gets the in-memory catalogue reconstructed from segment files.</summary>
    public FileSegmentCatalogue Catalogue => _catalogue;

    /// <summary>Configured number of segment files that may accept appends at once.</summary>
    internal int ConfiguredActiveSegmentCount => _activeSegmentCount;

    /// <summary>Segment ids whose age-seal timers are armed. Tests only.</summary>
    internal ulong[] AgeSealSegmentIds
    {
        get
        {
            lock (_writeGate)
            {
                return _ageSealSources.Keys.ToArray();
            }
        }
    }

    /// <summary>Segment files that currently accept appends.</summary>
    internal int OpenActiveSegmentCount
    {
        get
        {
            lock (_writeGate)
            {
                return _activeWriterIds.Count;
            }
        }
    }

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
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
        => OpenCore(options, logger, discoveryPayloadReader: null, configureBeforeDiscovery: null, timeProvider);

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
        Action<FileSegmentStore>? configureBeforeDiscovery = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SegmentDir);

        var log = logger ?? NullLogger.Instance;
        Directory.CreateDirectory(options.SegmentDir);
        var store = new FileSegmentStore(
            options.SegmentDir,
            options.SegmentTargetSizeBytes,
            log,
            timeProvider ?? TimeProvider.System,
            options.MaxSegmentSealDelay,
            options.ActiveSegmentCount);
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
                CloseActiveUnlocked(active, _time.GetUtcNow(), SegmentSealCause.Explicit);
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

        var gateStart = PhysicalProofProbe.Mark();
        lock (_writeGate)
        {
            PhysicalProofProbe.AddGate(gateStart);
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
        var gateStart = PhysicalProofProbe.Mark();
        lock (_writeGate)
        {
            PhysicalProofProbe.AddGate(gateStart);
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

    /// <summary>
    /// Reads the record at <paramref name="location"/> once and proves it against the expected
    /// Accept identity. Holds <c>_writeGate</c> for the same lookup, retired-segment rejection,
    /// and read as <see cref="TryReadProven"/>. Does not copy the payload.
    /// </summary>
    internal bool TryProveStoredLocation(
        in StoredArticleLocation location,
        ArticleId expectedArtId,
        ulong expectedArtHash,
        int expectedArtSize)
    {
        _ = Interlocked.Increment(ref _payloadLocationProofCount);
        IndexCommittedProbe.NotePhysicalDuringArticle();
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_segments.TryGetValue(location.SegmentId.Value, out var runtime)
                || runtime.State == SegmentState.Retired)
            {
                return false;
            }

            if (location.Offset < 0
                || location.Length < SegmentRecordCodec.MinimumRecordLength
                || location.Offset + location.Length > runtime.SizeBytes)
            {
                return false;
            }

            var buffer = new byte[location.Length];
            int read;
            lock (runtime.Sync)
            {
                runtime.EnsureReadable();
                runtime.Stream.Seek(location.Offset, SeekOrigin.Begin);
                read = runtime.Stream.Read(buffer, 0, buffer.Length);
            }

            if (read != buffer.Length)
            {
                return false;
            }

            return SegmentRecordCodec.TryProveExactRecord(
                buffer,
                expectedArtId,
                expectedArtHash,
                expectedArtSize);
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

    /// <summary>
    /// Deletes the exact <c>.closed</c> file for a fully dead segment, then removes its catalogue
    /// entry. Refuses unless the catalogue generation still matches and
    /// <see cref="SegmentLifecycle.IsReclaimable"/> is true under the write gate.
    /// </summary>
    /// <param name="segmentId">Closed segment to delete.</param>
    /// <param name="expectedGeneration">Catalogue generation observed when the segment was selected.</param>
    /// <param name="failureReason">Diagnostic reason when the method returns <see langword="false"/>.</param>
    /// <param name="afterDeleteBeforeCatalogueRemove">
    /// Optional hook invoked after the file delete and before catalogue removal. A throw leaves
    /// the catalogue entry in place.
    /// </param>
    /// <returns><see langword="true"/> when the file was deleted or was already absent.</returns>
    public bool TryReclaimFullyDeadClosed(
        SegmentId segmentId,
        ulong expectedGeneration,
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

            if (File.Exists(activePath) || File.Exists(retiredPath))
            {
                failureReason = "unexpected-active-or-retired-file";
                return false;
            }

            if (!_catalogue.TryGet(segmentId, out var info))
            {
                if (!File.Exists(closedPath))
                {
                    failureReason = "already-reclaimed";
                    return true;
                }

                failureReason = "catalogue-missing-with-closed-file";
                return false;
            }

            if (info.State == SegmentState.Active)
            {
                failureReason = "segment-active";
                return false;
            }

            if (info.State != SegmentState.Closed)
            {
                failureReason = "segment-not-closed";
                return false;
            }

            if (info.Generation != expectedGeneration)
            {
                failureReason = "generation-changed";
                return false;
            }

            if (!SegmentLifecycle.IsReclaimable(info))
            {
                failureReason = info.LiveBytes > 0 ? "live-bytes-remain" : "extent-accounting-incomplete";
                return false;
            }

            if (_segments.TryGetValue(segmentId.Value, out var runtime))
            {
                if (runtime.State != SegmentState.Closed)
                {
                    failureReason = "runtime-not-closed";
                    return false;
                }

                if (!string.Equals(runtime.Path, closedPath, StringComparison.OrdinalIgnoreCase))
                {
                    failureReason = "runtime-path-not-closed";
                    return false;
                }

                runtime.DisposeStream();
            }
            else if (!File.Exists(closedPath))
            {
                return _catalogue.TryRemoveClosed(segmentId);
            }

            if (!File.Exists(closedPath))
            {
                _ = _segments.Remove(segmentId.Value);
                return _catalogue.TryRemoveClosed(segmentId);
            }

            try
            {
                File.Delete(closedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failureReason = "delete-failed:" + ex.GetType().Name;
                return false;
            }

            afterDeleteBeforeCatalogueRemove?.Invoke();

            _ = _segments.Remove(segmentId.Value);
            if (!_catalogue.TryRemoveClosed(segmentId))
            {
                failureReason = "catalogue-remove-failed";
                return false;
            }

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
            CancelAgeSealScheduleUnlocked();
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
        PhysicalProofProbe.BeginAppend();
        try
        {
            return AppendToActiveUnlockedCore(artData);
        }
        finally
        {
            PhysicalProofProbe.EndAppend();
        }
    }

    /// <summary>
    /// Frames and appends one payload on the single active segment.
    /// Identity and ArtHash are derived once from <paramref name="artData"/>.
    /// The frame CRC is always written. <see cref="SegmentRecordCodec.FramedHash"/> is computed
    /// only when a <see cref="PendingSegmentRecord"/> must be matched or created.
    /// </summary>
    private ActiveSegmentAppend AppendToActiveUnlockedCore(ReadOnlyMemory<byte> artData)
    {
        if (artData.Length is < 1 or > ArticleResourceLimits.MaxArticleBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(artData), "ArtData length out of range.");
        }

        var messageStart = PhysicalProofProbe.MarkAppend();
        if (!ArticleStorageIntegrity.TryExtractMessageIdValue(artData.Span, out var messageId))
        {
            throw new ArgumentException("ArtData must contain a Message-ID header value.", nameof(artData));
        }

        PhysicalProofProbe.AddAppendMessage(messageStart);
        var blakeStart = PhysicalProofProbe.MarkAppend();
        var artId = ArticleId.FromMessageId(messageId);
        PhysicalProofProbe.AddAppendBlake(blakeStart);
        var xxStart = PhysicalProofProbe.MarkAppend();
        var artHash = XxHash3.HashToUInt64(artData.Span);
        PhysicalProofProbe.AddAppendXx(xxStart);

        // artId and artHash were just derived from this span on this thread. TryProve would
        // extract the Message-ID, hash the payload, and derive the id again. Nothing in this
        // method mutates the span before the write. Callers that already hold an expected
        // identity compare the receipt, and disk reads prove the persisted record.
        Span<byte> header = stackalloc byte[SegmentRecordCodec.FixedHeaderLength];
        Span<byte> crc = stackalloc byte[4];
        SegmentRecordCodec.PrepareProductionFrame(artId, artHash, artData.Span, header, crc);
        var recordLength = SegmentRecordCodec.RecordLengthForArtSize(artData.Length);
        EnsureActiveUnlocked();
        var activeId = _activeSegmentId!.Value;
        var runtime = _segments[activeId];
        if (runtime.PendingRecord is not null)
        {
            var framedHash = SegmentRecordCodec.FramedHash(header, artData.Span, crc);
            var finished = FinishPendingSegmentRecord(runtime, recordLength, framedHash, artId, artHash, artData.Length);
            if (ActiveAgeElapsedUnlocked(runtime))
            {
                if (_deferDurableFlush > 0 && _unflushedCommittedAppend)
                {
                    DurableSegmentFlush(runtime.Stream);
                }

                CloseActiveUnlocked(runtime.SegmentId.Value, _time.GetUtcNow(), SegmentSealCause.Age);
            }

            return finished;
        }

        ReconcileBlockedSegmentTail(runtime);

        // Rotate when current + next would exceed target, unless the segment is empty
        // (a single oversized article may exceed the nominal target).
        if (runtime.SizeBytes > 0
            && runtime.SizeBytes + recordLength > _targetSegmentBytes)
        {
            var closedId = activeId;
            if (_deferDurableFlush > 0 && _unflushedCommittedAppend)
            {
                DurableSegmentFlush(runtime.Stream);
            }

            CloseActiveUnlocked(activeId, _time.GetUtcNow(), SegmentSealCause.Size);
            EnsureActiveUnlocked();
            activeId = _activeSegmentId!.Value;
            runtime = _segments[activeId];
            FileSegmentStoreLogMessages.Rotated(_logger, closedId, activeId);
        }

        if (runtime.SizeBytes > 0 && ActiveAgeElapsedUnlocked(runtime))
        {
            var closedForAge = activeId;
            if (_deferDurableFlush > 0 && _unflushedCommittedAppend)
            {
                DurableSegmentFlush(runtime.Stream);
            }

            CloseActiveUnlocked(activeId, _time.GetUtcNow(), SegmentSealCause.Age);
            EnsureActiveUnlocked();
            activeId = _activeSegmentId!.Value;
            runtime = _segments[activeId];
            FileSegmentStoreLogMessages.Rotated(_logger, closedForAge, activeId);
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
            var writeStart = PhysicalProofProbe.MarkAppend();
            // Header, then the caller's payload span, then CRC. The file grows through these
            // writes. The CRC is not written before the payload, and the payload is not copied.
            runtime.Stream.Write(header);
            runtime.Stream.Write(artData.Span);
            runtime.Stream.Write(crc);
            PhysicalProofProbe.AddAppendWrite(writeStart);
            TestAfterWriteBeforeFlush?.Invoke(runtime.Stream, offset, recordLength);
            var end = offset + recordLength;
            if (runtime.Stream.Position != end || runtime.Stream.Length < end)
            {
                throw new IOException(
                    $"Active segment {runtime.SegmentId.Value} append at offset {offset} ended at position {runtime.Stream.Position}, length {runtime.Stream.Length}, expected {end}.");
            }

            if (_deferDurableFlush == 0)
            {
                DurableSegmentFlush(runtime.Stream);
            }

            return CommitActiveAppend(runtime, offset, recordLength, ambiguousComplete: false, artId, artHash, artData.Length);
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            var framedHash = SegmentRecordCodec.FramedHash(header, artData.Span, crc);
            if (TryRecoverFailedActiveWrite(
                    runtime,
                    offset,
                    recordLength,
                    framedHash,
                    artId,
                    artHash,
                    artData.Length,
                    header,
                    artData.Span,
                    crc,
                    publishActivation: true,
                    allowPending: true,
                    out var recovered))
            {
                return recovered;
            }

            throw;
        }
    }

    /// <summary>
    /// Applies the single-writer ambiguous-append rules after a write or length check threw.
    /// A complete matching record is durability-flushed and committed, or retained as
    /// <see cref="PendingSegmentRecord"/> when that flush fails. A partial or mismatched tail
    /// is truncated to <paramref name="offset"/> when <paramref name="allowPending"/> is true,
    /// or to the committed cursor when it is false. Returns false when the caller must rethrow
    /// the original failure. Does not claim durability without a successful <c>Flush(true)</c>.
    /// </summary>
    private bool TryRecoverFailedActiveWrite(
        SegmentRuntime runtime,
        long offset,
        int recordLength,
        ulong framedHash,
        ArticleId artId,
        ulong artHash,
        int artSize,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> artData,
        ReadOnlySpan<byte> crc,
        bool publishActivation,
        bool allowPending,
        out ActiveSegmentAppend recovered)
    {
        recovered = default;
        if (!allowPending)
        {
            TruncateSegmentOrBlock(runtime, runtime.SizeBytes, createdByThisCall: true);
            return false;
        }

        var outcome = InspectSegmentAppend(runtime, offset, recordLength, header, artData, crc);
        if (outcome == AmbiguousAppend.Growth.CompleteExpected)
        {
            if (runtime.Stream.Length > offset + recordLength)
            {
                try
                {
                    TruncateSegmentOrBlock(runtime, offset + recordLength, createdByThisCall: true);
                }
                catch (UnreconciledDurableTailException)
                {
                    runtime.PendingRecord = new PendingSegmentRecord(
                        offset, recordLength, framedHash, artId, artHash, artSize);
                    throw;
                }
            }

            try
            {
                DurableSegmentFlush(runtime.Stream);
            }
            catch (Exception flushEx) when (flushEx is not UnreconciledDurableTailException)
            {
                runtime.PendingRecord = new PendingSegmentRecord(
                    offset, recordLength, framedHash, artId, artHash, artSize);
                throw new UnreconciledDurableTailException(
                    $"Active segment {runtime.SegmentId.Value} record is present but not durable.",
                    flushEx,
                    createdByThisCall: true);
            }

            recovered = CommitActiveAppend(
                runtime,
                offset,
                recordLength,
                ambiguousComplete: false,
                artId,
                artHash,
                artSize,
                publishActivation);
            return true;
        }

        if (outcome == AmbiguousAppend.Growth.IncompleteGrowth)
        {
            TruncateSegmentOrBlock(runtime, offset, createdByThisCall: true);
        }

        return false;
    }

    private ActiveSegmentAppend FinishPendingSegmentRecord(
        SegmentRuntime runtime,
        int recordLength,
        ulong framedHash,
        ArticleId artId,
        ulong artHash,
        int artSize,
        bool publishActivation = true)
    {
        var pending = runtime.PendingRecord
            ?? throw new InvalidOperationException("No pending segment record.");
        if (pending.ArtId != artId
            || pending.ArtHash != artHash
            || pending.ArtSize != artSize
            || recordLength != pending.Length
            || framedHash != pending.PayloadHash)
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
        return CommitActiveAppend(
            runtime,
            pending.Offset,
            recordLength,
            ambiguousComplete: false,
            artId,
            artHash,
            artSize,
            publishActivation);
    }

    private void DurableSegmentFlush(FileStream stream)
    {
        TestBeforeDurableFlush?.Invoke();
        stream.Flush(flushToDisk: true);
        TestAfterDurableFlush?.Invoke();
        _ = Interlocked.Increment(ref _durableFlushCount);
        _unflushedCommittedAppend = false;
    }

    /// <summary>
    /// Appends every article, durability-flushes the segment file or files, then confirms
    /// each record header. Receipts are returned only after that flush and header confirm.
    /// The payload is not read back. A failure leaves earlier durable journal state
    /// untouched and does not return these receipts.
    /// </summary>
    /// <param name="articles">Payloads to frame and append, in order.</param>
    /// <returns>One receipt per article, in the same order.</returns>
    /// <remarks>
    /// One configured writer runs the append on the single active segment.
    /// Two or four writers split the batch into contiguous groups, one group per active
    /// segment. Each group holds only that segment's writer lock. The journal is not touched.
    /// </remarks>
    internal FlushedSegmentAppend[] AppendActiveBatch(IReadOnlyList<ReadOnlyMemory<byte>> articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        if (articles.Count == 0)
        {
            return [];
        }

        if (_activeSegmentCount > 1 && articles.Count > 1)
        {
            return AppendAcrossActiveSegments(articles);
        }

        return AppendActiveBatchSingle(articles);
    }

    /// <summary>
    /// Appends the batch to the one active segment. Caller does not hold <see cref="_writeGate"/>.
    /// </summary>
    private FlushedSegmentAppend[] AppendActiveBatchSingle(IReadOnlyList<ReadOnlyMemory<byte>> articles)
    {

        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var appended = new ActiveSegmentAppend[articles.Count];
            _deferDurableFlush++;
            try
            {
                for (var i = 0; i < articles.Count; i++)
                {
                    appended[i] = AppendToActiveUnlocked(articles[i]);
                }

                FlushActiveDurableUnlocked();
                var receipts = new FlushedSegmentAppend[appended.Length];
                for (var i = 0; i < appended.Length; i++)
                {
                    var append = appended[i];
                    if (!TryConfirmFlushedHeaderUnlocked(
                            append.Location,
                            append.ArtId,
                            append.ArtHash,
                            append.ArtSize))
                    {
                        throw new IOException(
                            $"Flushed segment record at {append.Location.SegmentId.Value}:{append.Location.Offset} does not match the written article header.");
                    }

                    receipts[i] = new FlushedSegmentAppend(
                        append.Location,
                        append.ArtId,
                        append.ArtHash,
                        append.ArtSize);
                }

                return receipts;
            }
            finally
            {
                _deferDurableFlush--;
            }
        }
    }

    /// <summary>
    /// Splits <paramref name="articles"/> across the configured active segments and appends
    /// those groups concurrently. Each group locks only its segment. Journal state is unchanged.
    /// </summary>
    private FlushedSegmentAppend[] AppendAcrossActiveSegments(IReadOnlyList<ReadOnlyMemory<byte>> articles)
    {
        SegmentRuntime[] writers;
        int[] counts;
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureActiveWriterCountUnlocked();
            var writerIds = _activeWriterIds.ToArray();
            counts = WriterCounts(articles.Count, writerIds.Length);
            writers = new SegmentRuntime[writerIds.Length];
            var cursor = 0;
            for (var w = 0; w < writers.Length; w++)
            {
                var bytes = 0L;
                for (var i = 0; i < counts[w]; i++)
                {
                    bytes += SegmentRecordCodec.RecordLengthForArtSize(articles[cursor + i].Length);
                }

                writers[w] = PrepareWriterForBytesUnlocked(writerIds[w], bytes);
                writers[w].AppendHold++;
                cursor += counts[w];
            }
        }

        var receipts = new FlushedSegmentAppend[articles.Count];
        var chains = new List<SegmentRuntime>[writers.Length];
        var tasks = new Task[writers.Length];
        var cursorForTask = 0;
        for (var w = 0; w < writers.Length; w++)
        {
            var runtime = writers[w];
            var start = cursorForTask;
            var count = counts[w];
            cursorForTask += count;
            var chain = new List<SegmentRuntime>(2) { runtime };
            chains[w] = chain;
            tasks[w] = Task.Run(() => AppendGroup(runtime, articles, start, count, receipts, chain));
        }

        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ex)
        {
            // One writer already surfaces its own exception. Several writers failing together
            // must still look like one filesystem failure so persist retries the batch.
            var flat = ex.Flatten();
            if (flat.InnerExceptions.Count == 1)
            {
                throw flat.InnerExceptions[0];
            }

            var allUnreconciled = true;
            var createdPending = false;
            foreach (var inner in flat.InnerExceptions)
            {
                if (inner is not UnreconciledDurableTailException unreconciled)
                {
                    allUnreconciled = false;
                    break;
                }

                createdPending |= unreconciled.CreatedByThisCall;
            }

            if (allUnreconciled)
            {
                throw new UnreconciledDurableTailException(
                    "One or more active segment writers left a record that is not durable.",
                    ex,
                    createdPending);
            }

            var allIo = true;
            foreach (var inner in flat.InnerExceptions)
            {
                if (inner is not IOException)
                {
                    allIo = false;
                    break;
                }
            }

            if (allIo)
            {
                throw new IOException("One or more active segment writers failed.", ex);
            }

            throw;
        }
        finally
        {
            lock (_writeGate)
            {
                foreach (var chain in chains)
                {
                    if (chain is null)
                    {
                        continue;
                    }

                    foreach (var runtime in chain)
                    {
                        runtime.AppendHold--;
                        if (runtime.AppendHold == 0
                            && runtime.State == SegmentState.Active
                            && runtime.SizeBytes > 0
                            && runtime.ActivatedUtc is null)
                        {
                            NoteFirstDurableArticleUnlocked(runtime);
                        }

                        if (runtime.AppendHold == 0 && runtime.DeferredSeal is { } deferred)
                        {
                            runtime.DeferredSeal = null;
                            if (runtime.State == SegmentState.Active)
                            {
                                CloseActiveUnlocked(runtime.SegmentId.Value, _time.GetUtcNow(), deferred);
                            }
                        }
                    }
                }
            }
        }

        return receipts;
    }

    /// <summary>
    /// Writes <paramref name="count"/> articles starting at <paramref name="start"/>.
    /// A record that does not fit rotates only this writer onto a new segment.
    /// Each contiguous run is flushed before the next segment is opened.
    /// <see cref="SegmentRuntime.Sync"/> is not held while taking <see cref="_writeGate"/>.
    /// </summary>
    private void AppendGroup(
        SegmentRuntime runtime,
        IReadOnlyList<ReadOnlyMemory<byte>> articles,
        int start,
        int count,
        FlushedSegmentAppend[] receipts,
        List<SegmentRuntime> chain)
    {
        if (count == 0)
        {
            return;
        }

        var index = start;
        var end = start + count;
        while (index < end)
        {
            var chunk = new List<PinnedChunkItem>();
            lock (runtime.Sync)
            {
                var committed = runtime.SizeBytes;
                var logical = committed;
                try
                {
                    while (index < end && NextRecordFitsAt(runtime, logical, articles[index]))
                    {
                        var pinned = AppendPinned(runtime, articles[index], logical);
                        logical += pinned.Append.Location.Length;
                        chunk.Add(pinned);
                        if (pinned.Published)
                        {
                            committed = runtime.SizeBytes;
                        }

                        index++;
                    }

                    if (chunk.Count > 0)
                    {
                        var needsFlush = false;
                        foreach (var pinned in chunk)
                        {
                            if (!pinned.Published)
                            {
                                needsFlush = true;
                                break;
                            }
                        }

                        // A recovered pending record was already flushed inside FinishPending.
                        if (needsFlush)
                        {
                            DurableSegmentFlush(runtime.Stream);
                        }

                        foreach (var pinned in chunk)
                        {
                            var append = pinned.Append;
                            if (!pinned.Published)
                            {
                                CommitActiveAppend(
                                    runtime,
                                    append.Location.Offset,
                                    append.Location.Length,
                                    append.AmbiguousComplete,
                                    append.ArtId,
                                    append.ArtHash,
                                    append.ArtSize,
                                    publishActivation: false);
                                committed = runtime.SizeBytes;
                            }

                            if (!HeaderMatches(runtime, append.Location, append.ArtId, append.ArtHash, append.ArtSize))
                            {
                                throw new IOException(
                                    $"Flushed segment record at {append.Location.SegmentId.Value}:{append.Location.Offset} does not match the written article header.");
                            }

                            receipts[start] = new FlushedSegmentAppend(
                                append.Location,
                                append.ArtId,
                                append.ArtHash,
                                append.ArtSize);
                            start++;
                        }
                    }
                }
                catch (Exception ex) when (ex is not UnreconciledDurableTailException)
                {
                    StabilizePinnedChunkFailure(runtime, committed, chunk, ex);
                    throw;
                }
            }

            if (index >= end)
            {
                break;
            }

            var cause = SealCauseForNext(runtime, articles[index]);
            lock (_writeGate)
            {
                var closedId = runtime.SegmentId.Value;
                CloseActiveUnlocked(closedId, _time.GetUtcNow(), cause, ignoreHold: true);
                var id = _nextSegmentId;
                ReserveSegmentId?.Invoke(id);
                TestHookAfterSegmentIdReserved?.Invoke(id);
                _nextSegmentId = id + 1;
                CreateActiveUnlocked(id, _time.GetUtcNow());
                runtime = _segments[id];
                runtime.AppendHold++;
                chain.Add(runtime);
                FileSegmentStoreLogMessages.Rotated(_logger, closedId, id);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="article"/> can be appended without sealing <paramref name="runtime"/>.
    /// An empty segment accepts one record even when that record exceeds the size target.
    /// Caller holds <see cref="SegmentRuntime.Sync"/>.
    /// </summary>
    private bool NextRecordFits(SegmentRuntime runtime, ReadOnlyMemory<byte> article) =>
        NextRecordFitsAt(runtime, runtime.SizeBytes, article);

    /// <summary>
    /// Same fit rule as <see cref="NextRecordFits"/> using <paramref name="logicalSize"/> instead of
    /// the committed cursor. A multi-writer chunk tracks bytes written but not yet flushed there.
    /// Caller holds <see cref="SegmentRuntime.Sync"/>.
    /// </summary>
    private bool NextRecordFitsAt(SegmentRuntime runtime, long logicalSize, ReadOnlyMemory<byte> article)
    {
        if (logicalSize == 0 || _targetSegmentBytes == long.MaxValue)
        {
            return true;
        }

        var recordLength = SegmentRecordCodec.RecordLengthForArtSize(article.Length);
        if (logicalSize + recordLength > _targetSegmentBytes)
        {
            return false;
        }

        return !ActiveAgeElapsedUnlocked(runtime);
    }

    /// <summary>
    /// Size wins when the next record would pass the target. Otherwise the segment is over age.
    /// Caller holds <see cref="SegmentRuntime.Sync"/> or has exclusive use of <paramref name="runtime"/>.
    /// </summary>
    private SegmentSealCause SealCauseForNext(SegmentRuntime runtime, ReadOnlyMemory<byte> article)
    {
        var recordLength = SegmentRecordCodec.RecordLengthForArtSize(article.Length);
        if (runtime.SizeBytes > 0
            && runtime.SizeBytes + recordLength > _targetSegmentBytes)
        {
            return SegmentSealCause.Size;
        }

        return SegmentSealCause.Age;
    }

    /// <summary>
    /// Appends one record to <paramref name="runtime"/> at <paramref name="expectedOffset"/>.
    /// Caller holds <see cref="SegmentRuntime.Sync"/>. Does not rotate the segment and does not
    /// arm its seal timer. A successful write is not catalogue-committed until the chunk flush
    /// returns. A write or flush failure uses <see cref="TryRecoverFailedActiveWrite"/> so a
    /// partial tail is truncated and a complete undurable record becomes
    /// <see cref="PendingSegmentRecord"/> instead of wedging the writer.
    /// <see cref="SegmentRecordCodec.FramedHash"/> runs only for that pending record
    /// or when a later append must match one. A successful chunk does not compute it.
    /// </summary>
    private PinnedChunkItem AppendPinned(
        SegmentRuntime runtime,
        ReadOnlyMemory<byte> artData,
        long expectedOffset)
    {
        PhysicalProofProbe.BeginAppend();
        try
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

            // Same derivation as the single-writer path. The second proof would rescan this span.
            Span<byte> header = stackalloc byte[SegmentRecordCodec.FixedHeaderLength];
            Span<byte> crc = stackalloc byte[4];
            SegmentRecordCodec.PrepareProductionFrame(artId, artHash, artData.Span, header, crc);
            var recordLength = SegmentRecordCodec.RecordLengthForArtSize(artData.Length);
            if (runtime.PendingRecord is not null)
            {
                var framedHash = SegmentRecordCodec.FramedHash(header, artData.Span, crc);
                var finished = FinishPendingSegmentRecord(
                    runtime,
                    recordLength,
                    framedHash,
                    artId,
                    artHash,
                    artData.Length,
                    publishActivation: false);
                return new PinnedChunkItem(finished, published: true);
            }

            if (runtime.TailUnreconciled)
            {
                ReconcileBlockedSegmentTail(runtime);
            }

            if (runtime.Stream.Length != expectedOffset)
            {
                if (expectedOffset == runtime.SizeBytes)
                {
                    runtime.TailUnreconciled = true;
                    runtime.TailValidEnd = runtime.SizeBytes;
                    throw new UnreconciledDurableTailException(
                        $"Active segment {runtime.SegmentId.Value} length {runtime.Stream.Length} does not match logical size {expectedOffset}.",
                        new IOException("Active segment cursor and file length diverged."));
                }

                throw new IOException(
                    $"Active segment {runtime.SegmentId.Value} append at offset {expectedOffset} ended at position {runtime.Stream.Position}, length {runtime.Stream.Length}, expected {expectedOffset + recordLength}.");
            }

            var allowPending = expectedOffset == runtime.SizeBytes;
            try
            {
                runtime.Stream.Position = expectedOffset;
                runtime.Stream.Write(header);
                runtime.Stream.Write(artData.Span);
                runtime.Stream.Write(crc);
                TestAfterWriteBeforeFlush?.Invoke(runtime.Stream, expectedOffset, recordLength);
                var end = expectedOffset + recordLength;
                if (runtime.Stream.Position != end || runtime.Stream.Length < end)
                {
                    throw new IOException(
                        $"Active segment {runtime.SegmentId.Value} append at offset {expectedOffset} ended at position {runtime.Stream.Position}, length {runtime.Stream.Length}, expected {end}.");
                }

                return new PinnedChunkItem(
                    new ActiveSegmentAppend(
                        new StoredArticleLocation(runtime.SegmentId, expectedOffset, recordLength),
                        AmbiguousComplete: false,
                        artId,
                        artHash,
                        artData.Length),
                    published: false);
            }
            catch (Exception ex) when (ex is not UnreconciledDurableTailException)
            {
                var framedHash = SegmentRecordCodec.FramedHash(header, artData.Span, crc);
                if (TryRecoverFailedActiveWrite(
                        runtime,
                        expectedOffset,
                        recordLength,
                        framedHash,
                        artId,
                        artHash,
                        artData.Length,
                        header,
                        artData.Span,
                        crc,
                        publishActivation: false,
                        allowPending,
                        out var recovered))
                {
                    return new PinnedChunkItem(recovered, published: true);
                }

                throw;
            }
        }
        finally
        {
            PhysicalProofProbe.EndAppend();
        }
    }

    /// <summary>
    /// Restores <paramref name="runtime"/> after a multi-writer chunk failed before its receipts
    /// were published. A complete record at the committed cursor becomes one pending record.
    /// Every other unpublished tail is truncated. Caller holds <see cref="SegmentRuntime.Sync"/>.
    /// </summary>
    private void StabilizePinnedChunkFailure(
        SegmentRuntime runtime,
        long committed,
        List<PinnedChunkItem> chunk,
        Exception failure)
    {
        if (runtime.PendingRecord is { } pending && pending.Offset == committed)
        {
            KeepPendingRecord(runtime, pending, committed);
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} record is present but not durable.",
                failure,
                createdByThisCall: true);
        }

        var retainFirst = chunk.Count > 0 && !chunk[0].Published && chunk[0].Append.Location.Offset == committed;
        if (retainFirst)
        {
            foreach (var pinned in chunk)
            {
                if (pinned.Published)
                {
                    retainFirst = false;
                    break;
                }
            }
        }

        if (retainFirst && TryRetainCompleteChunkRecord(runtime, chunk[0].Append, committed))
        {
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} record is present but not durable.",
                failure,
                createdByThisCall: true);
        }

        runtime.PendingRecord = null;
        if (runtime.Stream.Length != committed || runtime.SizeBytes != committed)
        {
            TruncateSegmentOrBlock(runtime, committed, createdByThisCall: true);
        }

        runtime.TailUnreconciled = false;
    }

    /// <summary>
    /// Leaves <paramref name="pending"/> on <paramref name="runtime"/> and drops bytes past it.
    /// The committed cursor stays at the pending offset so the record is not durable yet.
    /// </summary>
    private void KeepPendingRecord(SegmentRuntime runtime, PendingSegmentRecord pending, long committed)
    {
        var end = pending.Offset + pending.Length;
        if (runtime.Stream.Length != end)
        {
            TruncateSegmentOrBlock(runtime, end, createdByThisCall: true);
        }

        runtime.SizeBytes = committed;
        runtime.PendingRecord = pending;
        runtime.TailUnreconciled = false;
    }

    /// <summary>
    /// Retains <paramref name="append"/> as <see cref="PendingSegmentRecord"/> when the file
    /// still contains that exact record at the committed cursor. Extra bytes are truncated.
    /// </summary>
    private bool TryRetainCompleteChunkRecord(
        SegmentRuntime runtime,
        ActiveSegmentAppend append,
        long committed)
    {
        var offset = append.Location.Offset;
        var length = append.Location.Length;
        if (offset != committed || length < SegmentRecordCodec.FixedHeaderLength + 4)
        {
            return false;
        }

        try
        {
            runtime.Stream.Flush(flushToDisk: false);
            if (runtime.Stream.Length < offset + length)
            {
                return false;
            }

            var observed = ReadExact(runtime.Stream, offset, length);
            if (observed.Length != length)
            {
                return false;
            }

            if (!SegmentRecordCodec.TryConfirmRecordHeader(
                    observed.AsSpan(0, SegmentRecordCodec.FixedHeaderLength),
                    length,
                    append.ArtId,
                    append.ArtHash,
                    append.ArtSize))
            {
                return false;
            }

            var payload = observed.AsSpan(SegmentRecordCodec.FixedHeaderLength, append.ArtSize);
            if (XxHash3.HashToUInt64(payload) != append.ArtHash)
            {
                return false;
            }

            var crcOffset = length - 4;
            var actualCrc = Crc32.HashToUInt32(observed.AsSpan(0, crcOffset));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(observed.AsSpan(crcOffset, 4));
            if (actualCrc != expectedCrc)
            {
                return false;
            }

            if (runtime.Stream.Length > offset + length)
            {
                TruncateSegmentOrBlock(runtime, offset + length, createdByThisCall: true);
            }

            runtime.SizeBytes = committed;
            runtime.PendingRecord = new PendingSegmentRecord(
                offset,
                length,
                XxHash3.HashToUInt64(observed),
                append.ArtId,
                append.ArtHash,
                append.ArtSize);
            runtime.TailUnreconciled = false;
            return true;
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            runtime.TailUnreconciled = true;
            runtime.TailValidEnd = committed;
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} length could not be inspected after an ambiguous append.",
                ex,
                createdByThisCall: true);
        }
    }

    /// <summary>
    /// True when the fixed header at <paramref name="location"/> matches the written identity.
    /// Caller holds <see cref="SegmentRuntime.Sync"/>. Does not read the payload.
    /// </summary>
    private bool HeaderMatches(
        SegmentRuntime runtime,
        in StoredArticleLocation location,
        ArticleId artId,
        ulong artHash,
        int artSize)
    {
        _ = Interlocked.Increment(ref _flushedHeaderConfirmCount);
        if (artSize < 1
            || artSize > SegmentRecordCodec.MaxArtDataBytes
            || location.Offset < 0
            || location.Length != SegmentRecordCodec.RecordLengthForArtSize(artSize)
            || location.Offset + location.Length > runtime.SizeBytes)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[SegmentRecordCodec.FixedHeaderLength];
        if (!TryReadHeaderAt(runtime, location.Offset, header))
        {
            return false;
        }

        return SegmentRecordCodec.TryConfirmRecordHeader(header, location.Length, artId, artHash, artSize);
    }

    /// <summary>
    /// Opens enough active segments for the configured writer count.
    /// Caller holds <see cref="_writeGate"/>.
    /// </summary>
    private void EnsureActiveWriterCountUnlocked()
    {
        PruneActiveWritersUnlocked();
        while (_activeWriterIds.Count < _activeSegmentCount)
        {
            var id = _nextSegmentId;
            ReserveSegmentId?.Invoke(id);
            TestHookAfterSegmentIdReserved?.Invoke(id);
            _nextSegmentId = id + 1;
            CreateActiveUnlocked(id, _time.GetUtcNow());
        }
    }

    /// <summary>
    /// Drops writers that are no longer active. Caller holds <see cref="_writeGate"/>.
    /// </summary>
    private void PruneActiveWritersUnlocked()
    {
        for (var i = _activeWriterIds.Count - 1; i >= 0; i--)
        {
            var id = _activeWriterIds[i];
            if (!_segments.TryGetValue(id, out var runtime) || runtime.State != SegmentState.Active)
            {
                _activeWriterIds.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Seals <paramref name="segmentId"/> when its current bytes plus <paramref name="incomingBytes"/>
    /// would pass the size target. Returns the segment that should receive the bytes.
    /// Caller holds <see cref="_writeGate"/>.
    /// </summary>
    private SegmentRuntime PrepareWriterForBytesUnlocked(ulong segmentId, long incomingBytes)
    {
        if (!_segments.TryGetValue(segmentId, out var runtime) || runtime.State != SegmentState.Active)
        {
            throw new InvalidOperationException($"Segment {segmentId} is not an active writer.");
        }

        // A pending record or torn tail is reconciled by the append that holds this segment,
        // not refused here. Refusing it left the writer wedged until process restart.
        if (runtime.PendingRecord is not null || runtime.TailUnreconciled)
        {
            return runtime;
        }

        if (runtime.SizeBytes > 0
            && incomingBytes > 0
            && runtime.SizeBytes + incomingBytes > _targetSegmentBytes)
        {
            CloseActiveUnlocked(segmentId, _time.GetUtcNow(), SegmentSealCause.Size);
            var id = _nextSegmentId;
            ReserveSegmentId?.Invoke(id);
            TestHookAfterSegmentIdReserved?.Invoke(id);
            _nextSegmentId = id + 1;
            CreateActiveUnlocked(id, _time.GetUtcNow());
            runtime = _segments[id];
        }

        if (runtime.SizeBytes > 0 && ActiveAgeElapsedUnlocked(runtime))
        {
            var aged = runtime.SegmentId.Value;
            CloseActiveUnlocked(aged, _time.GetUtcNow(), SegmentSealCause.Age);
            var id = _nextSegmentId;
            ReserveSegmentId?.Invoke(id);
            TestHookAfterSegmentIdReserved?.Invoke(id);
            _nextSegmentId = id + 1;
            CreateActiveUnlocked(id, _time.GetUtcNow());
            runtime = _segments[id];
        }

        return runtime;
    }

    private static int[] WriterCounts(int articleCount, int writers)
    {
        var counts = new int[writers];
        var baseCount = articleCount / writers;
        var remainder = articleCount % writers;
        for (var i = 0; i < writers; i++)
        {
            counts[i] = baseCount + (i < remainder ? 1 : 0);
        }

        return counts;
    }

    /// <summary>
    /// Reads the fixed header at <paramref name="location"/> and checks it against the Accept.
    /// Does not read the payload. A retired segment, a short range, or a mismatched header
    /// returns false. The stream position is restored.
    /// </summary>
    /// <param name="location">Record written and flushed by this process.</param>
    /// <param name="artId">Accept article identity.</param>
    /// <param name="artHash">Accept article hash.</param>
    /// <param name="artSize">Accept article size.</param>
    /// <returns>True when the durable header still names that article.</returns>
    internal bool TryConfirmFlushedAppend(
        in StoredArticleLocation location,
        ArticleId artId,
        ulong artHash,
        int artSize)
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return TryConfirmFlushedHeaderUnlocked(location, artId, artHash, artSize);
        }
    }

    /// <summary>
    /// Header confirm. Caller holds <see cref="_writeGate"/>.
    /// </summary>
    private bool TryConfirmFlushedHeaderUnlocked(
        in StoredArticleLocation location,
        ArticleId artId,
        ulong artHash,
        int artSize)
    {
        _ = Interlocked.Increment(ref _flushedHeaderConfirmCount);
        if (artSize < 1
            || artSize > SegmentRecordCodec.MaxArtDataBytes
            || location.Offset < 0
            || location.Length != SegmentRecordCodec.RecordLengthForArtSize(artSize)
            || !_segments.TryGetValue(location.SegmentId.Value, out var runtime)
            || runtime.State == SegmentState.Retired
            || location.Offset + location.Length > runtime.SizeBytes)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[SegmentRecordCodec.FixedHeaderLength];
        if (!TryReadHeaderAt(runtime, location.Offset, header))
        {
            return false;
        }

        return SegmentRecordCodec.TryConfirmRecordHeader(header, location.Length, artId, artHash, artSize);
    }

    /// <summary>
    /// Reads <paramref name="header"/> at <paramref name="offset"/> and restores the stream position.
    /// Caller holds <see cref="_writeGate"/>.
    /// </summary>
    private static bool TryReadHeaderAt(SegmentRuntime runtime, long offset, Span<byte> header)
    {
        lock (runtime.Sync)
        {
            return ReadHeaderAt(runtime, offset, header);
        }
    }

    private static bool ReadHeaderAt(SegmentRuntime runtime, long offset, Span<byte> header)
    {
        runtime.EnsureReadable();
        var stream = runtime.Stream;
        var restore = stream.Position;
        stream.Seek(offset, SeekOrigin.Begin);
        var filled = 0;
        while (filled < header.Length)
        {
            var read = stream.Read(header[filled..]);
            if (read == 0)
            {
                stream.Position = restore;
                return false;
            }

            filled += read;
        }

        stream.Position = restore;
        return true;
    }

    /// <summary>
    /// True when segment bytes were committed to the active cursor and their durability flush
    /// has not returned. PhysicalWritten must not be written while this is true.
    /// </summary>
    internal bool HasUnflushedCommittedAppend
    {
        get
        {
            lock (_writeGate)
            {
                return _unflushedCommittedAppend;
            }
        }
    }

    /// <summary>Durability-flushes the active segment when a deferred append is still unflushed.</summary>
    internal void FlushActiveDurable()
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FlushActiveDurableUnlocked();
        }
    }

    private void FlushActiveDurableUnlocked()
    {
        if (!_unflushedCommittedAppend)
        {
            return;
        }

        if (_activeSegmentId is not { } activeId || !_segments.TryGetValue(activeId, out var runtime))
        {
            _unflushedCommittedAppend = false;
            return;
        }

        DurableSegmentFlush(runtime.Stream);
    }

    private ActiveSegmentAppend CommitActiveAppend(
        SegmentRuntime runtime,
        long offset,
        int recordLength,
        bool ambiguousComplete,
        ArticleId artId,
        ulong artHash,
        int artSize,
        bool publishActivation = true)
    {
        runtime.SizeBytes = offset + recordLength;
        _catalogue.RecordAppend(runtime.SegmentId, recordLength, runtime.SizeBytes);
        if (publishActivation && offset == 0)
        {
            NoteFirstDurableArticleUnlocked(runtime);
        }

        if (_deferDurableFlush > 0)
        {
            _unflushedCommittedAppend = true;
        }

        return new ActiveSegmentAppend(
            new StoredArticleLocation(runtime.SegmentId, offset, recordLength),
            ambiguousComplete,
            artId,
            artHash,
            artSize);
    }

    private AmbiguousAppend.Growth InspectSegmentAppend(
        SegmentRuntime runtime,
        long start,
        int recordLength,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> artData,
        ReadOnlySpan<byte> crc)
    {
        try
        {
            runtime.Stream.Flush(flushToDisk: false);
            var length = runtime.Stream.Length;
            if (length <= start)
            {
                return AmbiguousAppend.Growth.NoGrowth;
            }

            if (length < start + recordLength)
            {
                return AmbiguousAppend.Growth.IncompleteGrowth;
            }

            var observed = ReadExact(runtime.Stream, start, recordLength);
            if (observed.Length != recordLength
                || !observed.AsSpan(0, header.Length).SequenceEqual(header)
                || !observed.AsSpan(header.Length, artData.Length).SequenceEqual(artData)
                || !observed.AsSpan(header.Length + artData.Length, crc.Length).SequenceEqual(crc))
            {
                return AmbiguousAppend.Growth.IncompleteGrowth;
            }

            return AmbiguousAppend.Growth.CompleteExpected;
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

    /// <summary>
    /// Seals <paramref name="segmentId"/> when it is still the active segment, contains article
    /// bytes, and its activation age has elapsed. A stale id, an empty segment, or a segment
    /// that already left the active state is left unchanged.
    /// </summary>
    /// <returns>True when this call sealed the segment.</returns>
    internal bool TrySealActiveForAge(ulong segmentId) =>
        SealActiveForAge(segmentId, scheduledWaitElapsed: false);

    /// <summary>
    /// Seals <paramref name="segmentId"/> when <paramref name="scheduledWaitElapsed"/> or the age
    /// predicate says the delay is over. Caller does not hold <see cref="_writeGate"/>.
    /// </summary>
    private bool SealActiveForAge(ulong segmentId, bool scheduledWaitElapsed)
    {
        lock (_writeGate)
        {
            return TrySealActiveForAgeUnlocked(segmentId, scheduledWaitElapsed);
        }
    }

    /// <summary>Age-seal check. Caller holds <see cref="_writeGate"/>.</summary>
    private bool TrySealActiveForAgeUnlocked(ulong segmentId, bool scheduledWaitElapsed)
    {
        if (_disposed || !_activeWriterIds.Contains(segmentId))
        {
            return false;
        }

        if (!_segments.TryGetValue(segmentId, out var runtime)
            || runtime.State != SegmentState.Active
            || runtime.SizeBytes <= 0
            || runtime.PendingRecord is not null
            || runtime.TailUnreconciled
            || (!scheduledWaitElapsed && !ActiveAgeElapsedUnlocked(runtime)))
        {
            return false;
        }

        CloseActiveUnlocked(segmentId, _time.GetUtcNow(), SegmentSealCause.Age);
        return runtime.State == SegmentState.Closed;
    }

    /// <summary>
    /// True when the segment should seal for age. The monotonic budget wins when the wall
    /// clock is behind the saved activation instant.
    /// </summary>
    private bool ActiveAgeElapsedUnlocked(SegmentRuntime runtime)
    {
        if (_maxSealDelay <= TimeSpan.Zero || runtime.ActivatedUtc is not DateTimeOffset activated)
        {
            return false;
        }

        if (MonotonicBudgetElapsedUnlocked(runtime))
        {
            return true;
        }

        var now = _time.GetUtcNow();
        if (now < activated)
        {
            return false;
        }

        return now - activated >= _maxSealDelay;
    }

    /// <summary>True when this process has waited out the budget armed for <paramref name="runtime"/>.</summary>
    private bool MonotonicBudgetElapsedUnlocked(SegmentRuntime runtime)
    {
        if (runtime.AgeBudgetStartTimestamp is not long start)
        {
            return false;
        }

        var elapsed = _time.GetElapsedTime(start);
        if (elapsed < TimeSpan.Zero)
        {
            return false;
        }

        return elapsed >= runtime.AgeBudget;
    }

    /// <summary>
    /// Wall-clock time left until <paramref name="activated"/> plus the configured delay.
    /// A clock behind <paramref name="activated"/> returns the configured delay, not the backward step.
    /// </summary>
    private TimeSpan SealDelayRemainingUnlocked(DateTimeOffset activated)
    {
        var now = _time.GetUtcNow();
        if (now < activated)
        {
            return _maxSealDelay;
        }

        var due = activated + _maxSealDelay;
        if (due <= now)
        {
            return TimeSpan.Zero;
        }

        var remaining = due - now;
        return remaining > _maxSealDelay ? _maxSealDelay : remaining;
    }

    /// <summary>
    /// Records the activation instant for a segment whose first article has just been committed.
    /// An instant already recorded for this segment is kept, including across the articles that follow.
    /// </summary>
    private void NoteFirstDurableArticleUnlocked(SegmentRuntime runtime)
    {
        if (_maxSealDelay <= TimeSpan.Zero || runtime.ActivatedUtc is not null)
        {
            return;
        }

        var activated = _time.GetUtcNow();
        runtime.ActivatedUtc = activated;
        if (_catalogue.TryGet(runtime.SegmentId, out var info))
        {
            _catalogue.Upsert(info with { CreatedUtc = activated });
        }

        try
        {
            PersistActivationUnlocked(runtime.SegmentId.Value, activated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The article is already durable. Losing the sidecar seals on the next open
            // instead of failing an append that has already committed.
            FileSegmentStoreLogMessages.ActivationPersistFailed(_logger, runtime.SegmentId.Value, ex);
        }

        ArmAgeSealUnlocked(runtime.SegmentId.Value, activated);
    }

    /// <summary>Arms one delay for <paramref name="segmentId"/>. Caller holds <see cref="_writeGate"/>.</summary>
    private void ArmAgeSealUnlocked(ulong segmentId, DateTimeOffset activated)
    {
        if (_maxSealDelay <= TimeSpan.Zero)
        {
            return;
        }

        if (!_segments.TryGetValue(segmentId, out var runtime))
        {
            return;
        }

        CancelAgeSealScheduleUnlocked(segmentId);
        var remaining = SealDelayRemainingUnlocked(activated);
        runtime.AgeBudgetStartTimestamp = _time.GetTimestamp();
        runtime.AgeBudget = remaining;
        var cts = new CancellationTokenSource();
        _ageSealCts = cts;
        _ageSealSegmentId = segmentId;
        var task = SealWhenDueAsync(segmentId, remaining, cts);
        _ageSealTask = task;
        _ageSealSources[segmentId] = cts;
        _ageSealTasks[segmentId] = task;
    }

    /// <summary>Waits for the active segment's remaining age, then attempts one age seal.</summary>
    private async Task SealWhenDueAsync(ulong segmentId, TimeSpan remaining, CancellationTokenSource cancellation)
    {
        try
        {
            try
            {
                if (remaining > TimeSpan.Zero)
                {
                    await WaitRemainingAsync(remaining, cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    // A zero wait must not call back onto the write gate on this stack.
                    await Task.Yield();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            try
            {
                SealActiveForAge(segmentId, scheduledWaitElapsed: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FileSegmentStoreLogMessages.AgeSealFailed(_logger, segmentId, ex);
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    /// <summary>Waits <paramref name="remaining"/> on <see cref="_time"/>, splitting only at the timer limit.</summary>
    private async Task WaitRemainingAsync(TimeSpan remaining, CancellationToken cancellationToken)
    {
        var wait = remaining;
        while (wait > TimeSpan.Zero)
        {
            var slice = wait > MaxSingleWait ? MaxSingleWait : wait;
            await Task.Delay(slice, _time, cancellationToken).ConfigureAwait(false);
            wait -= slice;
        }
    }

    /// <summary>Cancels every age-seal delay. Does not wait. Caller holds <see cref="_writeGate"/>.</summary>
    private void CancelAgeSealScheduleUnlocked()
    {
        foreach (var id in _ageSealSources.Keys.ToArray())
        {
            CancelAgeSealScheduleUnlocked(id);
        }
    }

    /// <summary>
    /// Cancels the age-seal delay for <paramref name="segmentId"/> only.
    /// A timer for another active segment keeps running. Does not wait.
    /// Caller holds <see cref="_writeGate"/>.
    /// </summary>
    private void CancelAgeSealScheduleUnlocked(ulong segmentId)
    {
        _ = _ageSealTasks.Remove(segmentId);
        if (!_ageSealSources.Remove(segmentId, out var cts))
        {
            if (_ageSealSegmentId == segmentId)
            {
                _ageSealCts = null;
                _ageSealSegmentId = null;
            }

            return;
        }

        if (_ageSealSegmentId == segmentId)
        {
            _ageSealCts = null;
            _ageSealSegmentId = null;
            _ageSealTask = _ageSealTasks.Count == 1
                ? _ageSealTasks.Values.First()
                : Task.CompletedTask;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The delay task disposes the source when it finishes.
        }
    }

    /// <summary>Writes the activation instant durably next to the segment files.</summary>
    private void PersistActivationUnlocked(ulong segmentId, DateTimeOffset activated)
    {
        var buffer = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, segmentId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), activated.UtcTicks);
        var path = _activeSegmentCount <= 1
            ? Path.Combine(_root, ActivationFileName)
            : ActivationPath(segmentId);
        WriteActivationFile(path, buffer);
    }

    private static void WriteActivationFile(string path, byte[] buffer)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(buffer);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private string ActivationPath(ulong segmentId) =>
        Path.Combine(
            _root,
            "segment-activation-" + segmentId.ToString("D20", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Reads the activation instant when the file names <paramref name="segmentId"/>.</summary>
    private bool TryReadActivationUnlocked(ulong segmentId, out DateTimeOffset activated)
    {
        activated = default;
        var perWriter = ActivationPath(segmentId);
        var path = File.Exists(perWriter) ? perWriter : Path.Combine(_root, ActivationFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return false;
        }

        if (bytes.Length != 16)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes) != segmentId)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8));
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        activated = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    /// <summary>Removes the activation file when it still names <paramref name="segmentId"/>.</summary>
    private void DeleteActivationIfMatchUnlocked(ulong segmentId)
    {
        if (!TryReadActivationUnlocked(segmentId, out _))
        {
            return;
        }

        try
        {
            var perWriter = ActivationPath(segmentId);
            if (File.Exists(perWriter))
            {
                File.Delete(perWriter);
            }

            if (TryReadLegacyActivation(segmentId))
            {
                File.Delete(Path.Combine(_root, ActivationFileName));
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>True when the single-writer activation file still names <paramref name="segmentId"/>.</summary>
    private bool TryReadLegacyActivation(ulong segmentId)
    {
        var path = Path.Combine(_root, ActivationFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            return bytes.Length == 16 && BinaryPrimitives.ReadUInt64LittleEndian(bytes) == segmentId;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void TrackActiveWriterUnlocked(ulong id)
    {
        if (!_activeWriterIds.Contains(id))
        {
            _activeWriterIds.Add(id);
        }
    }

    private void UntrackActiveWriterUnlocked(ulong id)
    {
        _ = _activeWriterIds.Remove(id);
        if (_activeSegmentId == id)
        {
            _activeSegmentId = _activeWriterIds.Count > 0 ? _activeWriterIds[0] : null;
        }
    }

    /// <summary>
    /// After discovery, seals an active segment whose saved age has already elapsed.
    /// A younger segment keeps that saved instant and arms the remainder of the delay.
    /// </summary>
    private void SealOrArmRecoveredActiveUnlocked()
    {
        if (_maxSealDelay <= TimeSpan.Zero)
        {
            return;
        }

        foreach (var id in _activeWriterIds.ToArray())
        {
            SealOrArmRecoveredWriterUnlocked(id);
        }
    }

    /// <summary>
    /// Seals or arms one recovered active segment. Caller holds <see cref="_writeGate"/>.
    /// </summary>
    private void SealOrArmRecoveredWriterUnlocked(ulong id)
    {
        if (!_segments.TryGetValue(id, out var runtime)
            || runtime.State != SegmentState.Active
            || runtime.SizeBytes <= 0
            || runtime.ActivatedUtc is not DateTimeOffset activated)
        {
            return;
        }

        if (ActiveAgeElapsedUnlocked(runtime))
        {
            CloseActiveUnlocked(id, _time.GetUtcNow(), SegmentSealCause.Age);
            return;
        }

        ArmAgeSealUnlocked(id, activated);
    }

    /// <summary>Why an active segment left the active state.</summary>
    private enum SegmentSealCause
    {
        /// <summary>Caller asked to close the active segment.</summary>
        Explicit = 0,

        /// <summary>The next article would exceed the size target.</summary>
        Size = 1,

        /// <summary>The segment has held a durable article for the configured delay.</summary>
        Age = 2,
    }

    private void EnsureActiveUnlocked()
    {
        if (_activeSegmentId is { } existing && _segments.TryGetValue(existing, out var runtime)
            && runtime.State == SegmentState.Active)
        {
            return;
        }

        var id = _nextSegmentId;
        ReserveSegmentId?.Invoke(id);
        TestHookAfterSegmentIdReserved?.Invoke(id);
        _nextSegmentId = id + 1;
        CreateActiveUnlocked(id, DateTimeOffset.UtcNow);
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
        TrackActiveWriterUnlocked(id);
        if (id >= _nextSegmentId)
        {
            _nextSegmentId = id + 1;
        }

        FileSegmentStoreLogMessages.ActiveSelected(_logger, id, 0);
    }

    /// <summary>
    /// Seals <paramref name="activeId"/> when it is still active.
    /// <paramref name="ignoreHold"/> is for the writer that already owns the append and has
    /// released <see cref="SegmentRuntime.Sync"/>. Any other caller defers while <see cref="SegmentRuntime.AppendHold"/> is set.
    /// Caller holds <see cref="_writeGate"/>.
    /// </summary>
    private void CloseActiveUnlocked(
        ulong activeId,
        DateTimeOffset utcNow,
        SegmentSealCause cause,
        bool ignoreHold = false)
    {
        if (!_segments.TryGetValue(activeId, out var runtime) || runtime.State != SegmentState.Active)
        {
            UntrackActiveWriterUnlocked(activeId);
            return;
        }

        if (!ignoreHold && runtime.AppendHold > 0)
        {
            runtime.DeferredSeal = cause;
            return;
        }

        if (runtime.PendingRecord is not null || runtime.TailUnreconciled)
        {
            throw new UnreconciledDurableTailException(
                $"Active segment {runtime.SegmentId.Value} cannot close while a physical append is pending.",
                new IOException("pending-segment-record"));
        }

        var beforeClose = TestHookBeforeActiveClose;
        TestHookBeforeActiveClose = null;
        beforeClose?.Invoke(activeId);
        if (runtime.State != SegmentState.Active)
        {
            return;
        }

        runtime.Stream.Flush(flushToDisk: true);
        _unflushedCommittedAppend = false;
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
        UntrackActiveWriterUnlocked(activeId);
        CancelAgeSealScheduleUnlocked(activeId);

        DeleteActivationIfMatchUnlocked(activeId);
        if (cause == SegmentSealCause.Age)
        {
            Interlocked.Increment(ref _sealByAgeCount);
            FileSegmentStoreLogMessages.SealedByAge(_logger, activeId, runtime.SizeBytes);
        }
        else if (cause == SegmentSealCause.Size)
        {
            Interlocked.Increment(ref _sealBySizeCount);
        }

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
            DateTimeOffset? activated = null;
            var createdUtc = DateTimeOffset.UtcNow;
            if (state == SegmentState.Active && _maxSealDelay > TimeSpan.Zero && validLength > 0)
            {
                if (TryReadActivationUnlocked(item.Id.Value, out var stamp))
                {
                    createdUtc = stamp;
                    activated = stamp;
                }
                else
                {
                    createdUtc = DateTimeOffset.MinValue;
                    activated = DateTimeOffset.MinValue;
                }
            }

            catalogueEntries.Add(new SegmentInfo(
                item.Id,
                state,
                generation,
                validLength,
                LiveBytes: 0,
                DeadBytes: 0,
                CreatedUtc: createdUtc,
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
                    TrackActiveWriterUnlocked(item.Id.Value);
                    _segments[item.Id.Value] = new SegmentRuntime(
                        item.Id,
                        state,
                        item.Path,
                        opened,
                        validLength)
                    {
                        ActivatedUtc = activated,
                    };
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

        SealOrArmRecoveredActiveUnlocked();
    }

    private long RepairActiveTail(string path, SegmentId segmentId, long fileLength)
    {
        if (fileLength == 0
            && TestDiscoveryPayloadReader is null
            && TestActiveRepairStreamFactory is null)
        {
            PublishActiveValidatedPrefix(segmentId, sizeBytes: 0, []);
            return 0;
        }

        using var stream = OpenActiveRepairStream(path);
        var length = stream.Length;
        if (length == 0)
        {
            PublishActiveValidatedPrefix(segmentId, sizeBytes: 0, []);
            return 0;
        }

        var records = new List<ActiveValidatedRecord>();
        long offset = 0;
        while (offset < length)
        {
            var remaining = length - offset;
            if (remaining < 4)
            {
                ReadRepairExact(stream, new byte[(int)remaining], path, segmentId);
                return TruncateTornActiveTail(stream, path, segmentId, offset, length, records, SegmentRecordCodec.DecodeError.Incomplete);
            }

            var lengthPrefix = new byte[4];
            ReadRepairExact(stream, lengthPrefix, path, segmentId);
            var total = BinaryPrimitives.ReadUInt32LittleEndian(lengthPrefix);
            if (total < SegmentRecordCodec.MinimumRecordLength || total > SegmentRecordCodec.MaxRecordLength)
            {
                return FailClosedActiveTail(path, segmentId, offset, SegmentRecordCodec.DecodeError.CorruptLength);
            }

            var recordLength = (int)total;
            if (offset + recordLength > length)
            {
                return TruncateTornActiveTail(stream, path, segmentId, offset, length, records, SegmentRecordCodec.DecodeError.Incomplete);
            }

            if (recordLength > ActiveRepairMaxRecordBytes)
            {
                ActiveRepairMaxRecordBytes = recordLength;
            }

            var buffer = new byte[recordLength];
            lengthPrefix.CopyTo(buffer.AsSpan(0, 4));
            ReadRepairExact(stream, buffer.AsSpan(4), path, segmentId);
            if (SegmentRecordCodec.TryDecode(
                    buffer,
                    out recordLength,
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

            var reachesEnd = offset + Math.Max(recordLength, 0) >= length;
            if (error == SegmentRecordCodec.DecodeError.Incomplete
                || (error == SegmentRecordCodec.DecodeError.Corrupt && reachesEnd))
            {
                // Incomplete declared bytes, or a CRC-valid record that fails a later field check
                // and reaches EOF. A complete record with a bad CRC is not this case.
                return TruncateTornActiveTail(stream, path, segmentId, offset, length, records, error);
            }

            // A complete record with a bad CRC, an illegal length, or corruption with bytes after
            // it fails closed. The prefix is not published, so a failed repair cannot become candidates.
            return FailClosedActiveTail(path, segmentId, offset, error);
        }

        PublishActiveValidatedPrefix(segmentId, offset, records);
        return offset;
    }

    private long TruncateTornActiveTail(
        Stream stream,
        string path,
        SegmentId segmentId,
        long validEnd,
        long fileLength,
        List<ActiveValidatedRecord> records,
        SegmentRecordCodec.DecodeError error)
    {
        FileSegmentStoreLogMessages.TruncatingTornTail(
            _logger,
            segmentId.Value,
            validEnd,
            fileLength,
            error.ToString());
        if (stream is FileStream file)
        {
            file.SetLength(validEnd);
            file.Flush(flushToDisk: true);
        }
        else
        {
            using var truncating = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.None);
            truncating.SetLength(validEnd);
            truncating.Flush(flushToDisk: true);
        }

        PublishActiveValidatedPrefix(segmentId, validEnd, records);
        return validEnd;
    }

    private long FailClosedActiveTail(
        string path,
        SegmentId segmentId,
        long offset,
        SegmentRecordCodec.DecodeError error)
    {
        FileSegmentStoreLogMessages.ClosedCorrupt(_logger, path, offset, error.ToString());
        throw new SegmentStoreCorruptException(
            $"Active segment {segmentId} corrupt at offset {offset} ({error}).",
            path);
    }

    private void ReadRepairExact(Stream stream, Span<byte> destination, string path, SegmentId segmentId)
    {
        var filled = 0;
        while (filled < destination.Length)
        {
            var read = stream.Read(destination[filled..]);
            if (read == 0)
            {
                throw new SegmentStoreCorruptException(
                    $"Active segment {segmentId} ended before offset {DiscoveryPayloadBytesRead + destination.Length - filled}.",
                    path);
            }

            filled += read;
            DiscoveryPayloadBytesRead += read;
        }
    }

    /// <summary>
    /// Opens the active segment for a sequential record walk.
    /// A test reader or stream factory replaces the file bytes. Production reads the file.
    /// </summary>
    private Stream OpenActiveRepairStream(string path)
    {
        var reader = TestDiscoveryPayloadReader;
        if (reader is not null)
        {
            return new MemoryStream(reader(path), writable: false);
        }

        var factory = TestActiveRepairStreamFactory;
        if (factory is not null)
        {
            return factory(path);
        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
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
    /// Repaired Active prefix observed by the latest tail walk. Tests only.
    /// </summary>
    internal bool TryGetActiveValidatedPrefix(out long sizeBytes, out int recordCount, out long lastOffset)
    {
        if (_activeValidatedPrefix is not { } prefix)
        {
            sizeBytes = 0;
            recordCount = 0;
            lastOffset = 0;
            return false;
        }

        sizeBytes = prefix.SizeBytes;
        recordCount = prefix.Records.Length;
        lastOffset = recordCount == 0 ? 0 : prefix.Records[^1].Offset;
        return true;
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
        lock (runtime.Sync)
        {
            return TryReadInside(runtime, location, expectedArtId, expectedArtHash, expectedArtSize, out artData);
        }
    }

    private static bool TryReadInside(
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

        var allocStart = PhysicalProofProbe.Mark();
        var buffer = new byte[location.Length];
        PhysicalProofProbe.AddRecordAlloc(allocStart, buffer.Length);
        var readStart = PhysicalProofProbe.Mark();
        runtime.EnsureReadable();
        runtime.Stream.Seek(location.Offset, SeekOrigin.Begin);
        var read = runtime.Stream.Read(buffer, 0, buffer.Length);
        PhysicalProofProbe.AddRead(readStart);
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

    /// <summary>
    /// One multi-writer record that has been framed. <see cref="Published"/> is true when the
    /// catalogue already includes it, which is only after a successful durability flush.
    /// </summary>
    private readonly struct PinnedChunkItem
    {
        internal PinnedChunkItem(ActiveSegmentAppend append, bool published)
        {
            Append = append;
            Published = published;
        }

        internal ActiveSegmentAppend Append { get; }

        internal bool Published { get; }
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

        /// <summary>
        /// Instant of the first durable article on this segment.
        /// Null until that article is committed. Not cleared when the segment closes.
        /// </summary>
        public DateTimeOffset? ActivatedUtc { get; set; }

        /// <summary>
        /// <see cref="TimeProvider.GetTimestamp"/> when the current age budget was armed.
        /// Null until this process arms a delay for the segment.
        /// </summary>
        public long? AgeBudgetStartTimestamp { get; set; }

        /// <summary>Monotonic time to wait from <see cref="AgeBudgetStartTimestamp"/> before an age seal.</summary>
        public TimeSpan AgeBudget { get; set; }

        public bool TailUnreconciled { get; set; }

        public long TailValidEnd { get; set; }

        public PendingSegmentRecord? PendingRecord { get; set; }

        /// <summary>
        /// Serializes appends and reads of this segment's stream.
        /// A writer holds this lock and does not take the store write gate.
        /// A reader takes the store write gate first, then this lock.
        /// </summary>
        public object Sync { get; } = new();

        /// <summary>Appends currently between pin and completion. The segment is not sealed while this is positive.</summary>
        public int AppendHold { get; set; }

        /// <summary>Seal requested while an append held the segment. Applied when the hold drops.</summary>
        public SegmentSealCause? DeferredSeal { get; set; }

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
/// Stores the framed-record hash and article identity. Does not retain the payload.
/// Not restored after restart.
/// </summary>
internal sealed class PendingSegmentRecord
{
    internal PendingSegmentRecord(
        long offset,
        int length,
        ulong payloadHash,
        ArticleId artId,
        ulong artHash,
        int artSize)
    {
        Offset = offset;
        Length = length;
        PayloadHash = payloadHash;
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

/// <summary>
/// One append committed to the active segment cursor.
/// Identity fields are those derived from the payload before the write.
/// </summary>
/// <param name="Location">Segment, offset, and full record length.</param>
/// <param name="AmbiguousComplete">True when a failed flush was reconciled from bytes already present.</param>
/// <param name="ArtId">Article identity derived from the payload Message-ID.</param>
/// <param name="ArtHash">XxHash3 of the payload that was written.</param>
/// <param name="ArtSize">Payload length that was written.</param>
internal readonly record struct ActiveSegmentAppend(
    StoredArticleLocation Location,
    bool AmbiguousComplete,
    ArticleId ArtId,
    ulong ArtHash,
    int ArtSize);

/// <summary>
/// A segment record whose payload was proved, framed, written, and durability-flushed,
/// and whose header was read back and matched. The payload was not read back.
/// </summary>
/// <param name="Location">Flushed record location.</param>
/// <param name="ArtId">Identity written into the header.</param>
/// <param name="ArtHash">Hash written into the header.</param>
/// <param name="ArtSize">Payload size written into the header.</param>
internal readonly record struct FlushedSegmentAppend(
    StoredArticleLocation Location,
    ArticleId ArtId,
    ulong ArtHash,
    int ArtSize);

/// <summary>One proven segment record. Payload is not retained.</summary>
internal readonly record struct ProvenSegmentExtent(
    StoredArticleLocation Location,
    ArticleId ArtId,
    ulong ArtHash,
    int ArtSize);
