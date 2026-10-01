using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage.Engine.FileJournal;

/// <summary>
/// Filesystem-backed Model A durability journal under <see cref="ArticleStorageRuntimeOptions.ControlDir"/>.
/// </summary>
/// <remarks>
/// <para>
/// Single append-only file <c>article.journal</c>. Accept durability uses
/// <see cref="FileStream.Flush(bool)"/> with <c>flushToDisk: true</c> (Windows
/// <c>FlushFileBuffers</c> / Unix <c>fsync</c>). <see cref="Stream.FlushAsync(CancellationToken)"/>
/// alone is not treated as the durability boundary.
/// </para>
/// <para>
/// Replay applies a contiguous prefix of CRC-verified frames. An incomplete or corrupt
/// <em>final</em> frame (no complete bytes after the failure) is truncated to the last
/// good boundary. Corruption with trailing bytes after a failed frame fails closed via
/// <see cref="ArticleJournalCorruptException"/>.
/// </para>
/// <para>
/// <see cref="IArticleJournal.OutstandingRecoverableBytes"/> tracks Accept ArtSize until
/// IndexCommitted. <see cref="IArticleJournal.JournalPhysicalBytes"/> tracks on-disk file
/// length and may remain larger until <see cref="CheckpointTruncateCommitted"/>.
/// </para>
/// </remarks>
public sealed partial class FileArticleJournal : IArticleJournal, IDisposable, IAsyncDisposable
{
    /// <summary>Engine-owned journal filename beneath ControlDir.</summary>
    public const string JournalFileName = "article.journal";

    private readonly long _softLimitBytes;
    private readonly long _hardLimitBytes;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly object _checkpointSerial = new();
    private bool _retainCheckpointTempReservation;
    private readonly Dictionary<ulong, SequenceState> _bySequence = new();
    private readonly Dictionary<ArticleId, ulong> _outstandingArtIdToSequence = new();
    private readonly Dictionary<ulong, CompactionState> _compactions = new();
    private readonly string _journalPath;
    private FileStream _stream;
    private ulong _nextSequence = 1;
    private ulong _nextCompactionId = 1;
    private long _outstandingRecoverableBytes;
    private StorageWritePressure _lastLoggedPressure = (StorageWritePressure)byte.MaxValue;
    private bool _disposed;

    private FileArticleJournal(
        ArticleStorageRuntimeOptions options,
        string journalPath,
        FileStream stream,
        ILogger logger)
    {
        _softLimitBytes = options.JournalSoftLimitBytes;
        _hardLimitBytes = options.JournalHardLimitBytes;
        _journalPath = journalPath;
        _stream = stream;
        _logger = logger;
    }

    /// <summary>Gets the absolute journal file path.</summary>
    public string JournalPath => _journalPath;

    /// <summary>Gets the next sequence that will be allocated.</summary>
    public ulong NextSequence
    {
        get
        {
            lock (_gate)
            {
                return _nextSequence;
            }
        }
    }

    /// <inheritdoc />
    public long OutstandingRecoverableBytes
    {
        get
        {
            lock (_gate)
            {
                return _outstandingRecoverableBytes;
            }
        }
    }

    /// <inheritdoc />
    public long JournalPhysicalBytes
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _stream.Length;
            }
        }
    }

    /// <inheritdoc />
    public StorageWritePressure Pressure
    {
        get
        {
            lock (_gate)
            {
                return ComputePressureUnlocked();
            }
        }
    }

    /// <summary>
    /// Opens or creates <c>article.journal</c> under <paramref name="options"/>.ControlDir,
    /// replays durable state, and truncates a torn/corrupt final tail when required.
    /// </summary>
    public static FileArticleJournal Open(
        ArticleStorageRuntimeOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ControlDir);

        var log = logger ?? NullLogger.Instance;
        Directory.CreateDirectory(options.ControlDir);
        var path = Path.Combine(options.ControlDir, JournalFileName);
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.None);

        var journal = new FileArticleJournal(options, path, stream, log);
        try
        {
            journal.ReplayAndRecoverUnlocked();
            FileArticleJournalLogMessages.Opened(
                log,
                path,
                stream.Length,
                journal._nextSequence,
                journal._outstandingRecoverableBytes,
                journal._bySequence.Values.Count(static s => !s.IndexCommitted));
            FileArticleJournalLogMessages.SequenceRecovered(log, path, journal._nextSequence);
            journal.LogPressureIfChangedUnlocked();
            return journal;
        }
        catch
        {
            journal.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Atomically rejects under pressure / duplicate / conflict, otherwise allocates a
    /// sequence and durably appends Accept (including ArtData).
    /// </summary>
    public bool TryAppendNewAccept(
        ArticleId artId,
        ulong artHash,
        int artSize,
        DateTimeOffset utcNow,
        ReadOnlyMemory<byte> artData,
        out JournalAcceptRecord record,
        out ArticleAcceptOutcome rejectOutcome)
    {
        if (artSize != artData.Length || artSize is < 1 or > ArticleResourceLimits.MaxArticleBytes)
        {
            record = null!;
            rejectOutcome = ArticleAcceptOutcome.RejectedInvalid;
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_pending is PendingJournalFrame.AcceptOperation pendingAccept)
            {
                if (pendingAccept.ArtId != artId
                    || pendingAccept.ArtHash != artHash
                    || pendingAccept.ArtSize != artSize)
                {
                    throw PendingOwnedByOther();
                }

                var pendingRecord = pendingAccept.Record;
                var encodedPendingAccept = ArticleJournalFrameCodec.EncodeAccept(pendingRecord);
                AppendFrameUnlocked(
                    encodedPendingAccept,
                    offset => new PendingJournalFrame.AcceptOperation(offset, pendingRecord, encodedPendingAccept));
                record = pendingRecord;
                ApplyAcceptUnlocked(record);
                _nextSequence = record.Sequence + 1;
                LogPressureIfChangedUnlocked();
                rejectOutcome = default;
                return true;
            }

            if (_pending is not null)
            {
                throw PendingOwnedByOther();
            }

            if (_outstandingArtIdToSequence.TryGetValue(artId, out var existingSeq)
                && _bySequence.TryGetValue(existingSeq, out var existing)
                && !existing.IndexCommitted)
            {
                record = existing.Accept;
                rejectOutcome = existing.Accept.ArtHash == artHash && existing.Accept.ArtSize == artSize
                    ? ArticleAcceptOutcome.Duplicate
                    : ArticleAcceptOutcome.Conflict;
                return false;
            }

            if (ComputePressureUnlocked() == StorageWritePressure.Critical)
            {
                record = null!;
                rejectOutcome = ArticleAcceptOutcome.RejectedPressure;
                return false;
            }

            var sequence = _nextSequence;
            var accept = new JournalAcceptRecord(
                version: ArticleJournalFrameCodec.SchemaVersion,
                sequence,
                artId,
                artHash,
                artSize,
                utcNow,
                artData);
            record = accept;

            var encodedAccept = ArticleJournalFrameCodec.EncodeAccept(accept);
            AppendFrameUnlocked(
                encodedAccept,
                offset => new PendingJournalFrame.AcceptOperation(offset, accept, encodedAccept));

            ApplyAcceptUnlocked(accept);
            _nextSequence = sequence + 1;
            LogPressureIfChangedUnlocked();
            rejectOutcome = default;
            return true;
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAcceptAsync(JournalAcceptRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        if (record.ArtSize is < 1 or > ArticleResourceLimits.MaxArticleBytes
            || record.ArtSize != record.ArtData.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "ArtData length out of range.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bySequence.ContainsKey(record.Sequence))
            {
                throw new InvalidOperationException($"Journal sequence {record.Sequence} already exists.");
            }

            var encodedAccept = ArticleJournalFrameCodec.EncodeAccept(record);
            AppendFrameUnlocked(
                encodedAccept,
                offset => new PendingJournalFrame.AcceptOperation(offset, record, encodedAccept));
            ApplyAcceptUnlocked(record);
            if (record.Sequence >= _nextSequence)
            {
                _nextSequence = record.Sequence + 1;
            }

            LogPressureIfChangedUnlocked();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<JournalAppendOutcome> AppendPhysicalWrittenAsync(
        JournalPhysicalWrittenRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_bySequence.TryGetValue(record.Sequence, out var state))
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            if (state.PhysicalWritten is { } existing)
            {
                if (LocationsEqual(existing.Location, record.Location))
                {
                    return ValueTask.FromResult(JournalAppendOutcome.IdempotentNoOp);
                }

                return ValueTask.FromResult(JournalAppendOutcome.Conflict);
            }

            if (record.Location.Length < state.Accept.ArtSize)
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            var encodedPhysicalWritten = ArticleJournalFrameCodec.EncodePhysicalWritten(record);
            AppendFrameUnlocked(
                encodedPhysicalWritten,
                offset => new PendingJournalFrame.PhysicalWrittenOperation(
                    offset,
                    record.Sequence,
                    record.Location,
                    encodedPhysicalWritten));
            state.PhysicalWritten = record;
            return ValueTask.FromResult(JournalAppendOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public ValueTask<JournalAppendOutcome> AppendIndexCommittedAsync(
        JournalIndexCommittedRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_bySequence.TryGetValue(record.Sequence, out var state))
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            if (state.IndexCommitted)
            {
                return ValueTask.FromResult(JournalAppendOutcome.IdempotentNoOp);
            }

            if (state.PhysicalWritten is null)
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            var encodedIndexCommitted = ArticleJournalFrameCodec.EncodeIndexCommitted(record);
            AppendFrameUnlocked(
                encodedIndexCommitted,
                offset => new PendingJournalFrame.IndexCommittedOperation(
                    offset,
                    record.Sequence,
                    encodedIndexCommitted));
            state.IndexCommitted = true;
            state.IndexCommittedRecord = record;
            _ = _outstandingArtIdToSequence.Remove(state.Accept.ArtId);
            _outstandingRecoverableBytes = Math.Max(0L, _outstandingRecoverableBytes - state.Accept.ArtSize);
            LogPressureIfChangedUnlocked();
            return ValueTask.FromResult(JournalAppendOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public bool TryGetOutstanding(ArticleId artId, out JournalAcceptRecord record)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_outstandingArtIdToSequence.TryGetValue(artId, out var sequence)
                && _bySequence.TryGetValue(sequence, out var state)
                && !state.IndexCommitted)
            {
                record = state.Accept;
                return true;
            }

            record = null!;
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<JournalIncompleteSequence> EnumerateIncomplete()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bySequence.Values
                .Where(static s => !s.IndexCommitted)
                .OrderBy(static s => s.Accept.Sequence)
                .Select(static s => new JournalIncompleteSequence(s.Accept, s.PhysicalWritten))
                .ToArray();
        }
    }

    /// <summary>
    /// Copies incomplete Accept identity under the journal lock.
    /// The lock is not held after this method returns. ArtData is not read or copied.
    /// </summary>
    internal JournalReservationIdentity[] CopyIncompleteIdentities()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var count = 0;
            foreach (var state in _bySequence.Values)
            {
                if (!state.IndexCommitted)
                {
                    count++;
                }
            }

            var rows = new JournalReservationIdentity[count];
            var index = 0;
            foreach (var state in _bySequence.Values)
            {
                if (state.IndexCommitted)
                {
                    continue;
                }

                var accept = state.Accept;
                rows[index++] = new JournalReservationIdentity(
                    accept.ArtId,
                    accept.ArtHash,
                    accept.ArtSize,
                    accept.Sequence,
                    state.PhysicalWritten is not null);
            }

            return rows;
        }
    }

    /// <summary>
    /// Every sequence still present in the authoritative journal, including IndexCommitted
    /// sequences that checkpoint has not yet omitted.
    /// </summary>
    internal (ulong Sequence, int ArtSize)[] CopyRetainedJournalSequences()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rows = new (ulong Sequence, int ArtSize)[_bySequence.Count];
            var index = 0;
            foreach (var state in _bySequence.Values)
            {
                rows[index++] = (state.Accept.Sequence, state.Accept.ArtSize);
            }

            return rows;
        }
    }

    /// <summary>
    /// Test-only fault injection for checkpoint durability ordering (InternalsVisibleTo).
    /// </summary>
    internal enum CheckpointFaultPoint
    {
        /// <summary>After the temporary replacement has been flushed; old journal still open.</summary>
        AfterTempFlushed = 1,

        /// <summary>After the old stream was closed; before <c>File.Move</c>.</summary>
        AfterOldStreamClosedBeforeMove = 2,

        /// <summary>After <c>File.Move</c> replaced the journal; before reopen.</summary>
        AfterMoveBeforeReopen = 3,
    }

    /// <summary>
    /// Optional test hook invoked at checkpoint stages. Throwing aborts checkpoint.
    /// </summary>
    internal Action<CheckpointFaultPoint>? CheckpointTestFault { get; set; }

    /// <summary>
    /// Invoked before a journal frame is written. Tests only. A throw leaves no durable frame.
    /// </summary>
    internal Action? TestBeforeFrameAppend { get; set; }

    /// <summary>
    /// Invoked after <see cref="FileStream.Write(byte[], int, int)"/> and before
    /// <see cref="FileStream.Flush(bool)"/>. Tests only. The production path is null.
    /// </summary>
    internal Action<FileStream, long, int>? TestAfterWriteBeforeFlush { get; set; }

    private bool _tailUnreconciled;

    private long _tailValidEnd;

    private PendingJournalFrame? _pending;

    /// <summary>
    /// Invoked immediately before each durability <see cref="FileStream.Flush(bool)"/>.
    /// Tests only. A throw leaves the payload undurable.
    /// </summary>
    internal Action? TestBeforeDurableFlush { get; set; }

    /// <summary>
    /// When set, checkpoint temp deletion leaves the file in place. Tests only.
    /// </summary>
    internal bool TestFailCheckpointTempDelete { get; set; }

    /// <summary>
    /// When set, torn-tail truncation throws instead of shrinking the file. Tests only.
    /// </summary>
    internal bool TestFailTailTruncate { get; set; }

    /// <summary>
    /// When set, the encoded checkpoint image is reserved before the temporary journal is created.
    /// Null leaves checkpoint IO unchanged and does not consult capacity.
    /// </summary>
    internal CheckpointCapacityReservation? CheckpointCapacity { get; set; }

    /// <summary>
    /// Rewrites the journal retaining only incomplete sequences plus a sequence fence.
    /// Releases physical bytes of IndexCommitted records; does not change
    /// <see cref="OutstandingRecoverableBytes"/>.
    /// </summary>
    /// <remarks>
    /// Committed in-memory entries are removed only after the replacement file is installed
    /// and successfully reopened. Checkpoint failure does not report success and does not
    /// leave a usable live instance with memory/disk divergence.
    /// When <see cref="CheckpointCapacity"/> is set, the same encoded image is reserved on the
    /// process-local ledger before the temp file is created and released only after that extra
    /// file is gone. A capacity refusal throws <see cref="CheckpointCapacityDeniedException"/>
    /// without creating the temp.
    /// </remarks>
    public long CheckpointTruncateCommitted() =>
        CheckpointTruncateCommittedReporting(out _, out _);

    /// <summary>
    /// Checkpoints and reports sequences and retired compactions omitted from the installed replacement.
    /// Both arrays are empty when checkpoint did not install a replacement.
    /// </summary>
    internal long CheckpointTruncateCommittedReporting(
        out ulong[] omittedSequences,
        out ulong[] omittedCompactionIds)
    {
        omittedSequences = [];
        omittedCompactionIds = [];
        lock (_checkpointSerial)
        {
            var capacity = CheckpointCapacity;
            if (capacity is null)
            {
                return CheckpointTruncateCommittedCore(out omittedSequences, out omittedCompactionIds);
            }

            return CheckpointTruncateCommittedReserved(capacity, out omittedSequences, out omittedCompactionIds);
        }
    }

    private long CheckpointTruncateCommittedCore(
        out ulong[] omittedSequences,
        out ulong[] omittedCompactionIds)
    {
        omittedSequences = [];
        omittedCompactionIds = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfPendingJournalAppendUnlocked();
            if (!TryCollectCheckpointPlanUnlocked(out var incomplete, out var committedKeys))
            {
                return 0;
            }

            var retiredCompactions = CopyRetiredCompactionIdsUnlocked();
            var before = _stream.Length;
            InstallReplacementJournalUnlocked(incomplete);
            PublishCheckpointMemoryUnlocked(committedKeys);
            omittedSequences = committedKeys;
            omittedCompactionIds = retiredCompactions;
            return LogCheckpointUnlocked(before);
        }
    }

    private long CheckpointTruncateCommittedReserved(
        CheckpointCapacityReservation capacity,
        out ulong[] omittedSequences,
        out ulong[] omittedCompactionIds)
    {
        omittedSequences = [];
        omittedCompactionIds = [];
        ulong? reservationId = null;
        var reservedBytes = 0L;
        try
        {
            while (true)
            {
                long pendingSize;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    ThrowIfPendingJournalAppendUnlocked();
                    if (!TryCollectCheckpointPlanUnlocked(out var incomplete, out var committedKeys))
                    {
                        return 0;
                    }

                    var image = MaterializeCheckpointImageUnlocked(incomplete);
                    if (image.Length <= reservedBytes)
                    {
                        var retiredCompactions = CopyRetiredCompactionIdsUnlocked();
                        var before = _stream.Length;
                        InstallReplacementJournalFromImageUnlocked(image);
                        PublishCheckpointMemoryUnlocked(committedKeys);
                        omittedSequences = committedKeys;
                        omittedCompactionIds = retiredCompactions;
                        return LogCheckpointUnlocked(before);
                    }

                    pendingSize = image.Length;
                }

                var additional = pendingSize - reservedBytes;
                if (reservationId is null)
                {
                    reservationId = capacity.TryReserve(pendingSize);
                    if (reservationId is null)
                    {
                        throw new CheckpointCapacityDeniedException(pendingSize);
                    }
                }
                else if (!capacity.TryIncrease(reservationId.Value, additional))
                {
                    throw new CheckpointCapacityDeniedException(pendingSize);
                }

                reservedBytes = pendingSize;
            }
        }
        finally
        {
            if (!_retainCheckpointTempReservation && reservationId is ulong id)
            {
                capacity.Release(id);
            }

            _retainCheckpointTempReservation = false;
        }
    }

    private bool TryCollectCheckpointPlanUnlocked(
        out SequenceState[] incomplete,
        out ulong[] committedKeys)
    {
        incomplete = _bySequence.Values
            .Where(static s => !s.IndexCommitted)
            .OrderBy(static s => s.Accept.Sequence)
            .ToArray();
        committedKeys = _bySequence
            .Where(static kv => kv.Value.IndexCommitted)
            .Select(static kv => kv.Key)
            .ToArray();
        var hasRetiredCompaction = _compactions.Values.Any(static c => c.Retired is not null);
        return committedKeys.Length != 0 || hasRetiredCompaction;
    }

    private ulong[] CopyRetiredCompactionIdsUnlocked() =>
        _compactions
            .Where(static kv => kv.Value.Retired is not null)
            .Select(static kv => kv.Key)
            .ToArray();

    private void PublishCheckpointMemoryUnlocked(ulong[] committedKeys)
    {
        foreach (var key in committedKeys)
        {
            _ = _bySequence.Remove(key);
        }

        foreach (var retiredId in _compactions
                     .Where(static kv => kv.Value.Retired is not null)
                     .Select(static kv => kv.Key)
                     .ToArray())
        {
            _ = _compactions.Remove(retiredId);
        }
    }

    private long LogCheckpointUnlocked(long lengthBefore)
    {
        var released = Math.Max(0L, lengthBefore - _stream.Length);
        FileArticleJournalLogMessages.Checkpointed(
            _logger,
            _journalPath,
            released,
            _stream.Length,
            _nextSequence);
        return released;
    }

    private byte[] MaterializeCheckpointImageUnlocked(SequenceState[] incomplete)
    {
        using var buffer = new MemoryStream();
        WriteCheckpointBodyUnlocked(buffer, incomplete);
        return buffer.ToArray();
    }

    private void WriteCheckpointBodyUnlocked(Stream destination, SequenceState[] incomplete)
    {
        var fence = ArticleJournalFrameCodec.EncodeSequenceFence(_nextSequence);
        destination.Write(fence, 0, fence.Length);
        foreach (var state in incomplete)
        {
            var acceptFrame = ArticleJournalFrameCodec.EncodeAccept(state.Accept);
            destination.Write(acceptFrame, 0, acceptFrame.Length);
            if (state.PhysicalWritten is { } physicalWritten)
            {
                var physicalFrame = ArticleJournalFrameCodec.EncodePhysicalWritten(physicalWritten);
                destination.Write(physicalFrame, 0, physicalFrame.Length);
            }
        }

        WriteOpenCompactionFramesUnlocked(destination);
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
            FileArticleJournalLogMessages.Closed(_logger, _journalPath);
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
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
            throw new ArticleJournalCorruptException(
                $"Article journal exceeds supported size ({fileLength} bytes).",
                0);
        }

        var buffer = new byte[(int)fileLength];
        _stream.Seek(0, SeekOrigin.Begin);
        var read = _stream.Read(buffer, 0, buffer.Length);
        if (read != buffer.Length)
        {
            throw new IOException($"Short read replaying article journal ({read}/{buffer.Length}).");
        }

        var offset = 0;
        while (offset < buffer.Length)
        {
            var span = buffer.AsSpan(offset);
            if (!ArticleJournalFrameCodec.TryDecode(
                    span,
                    out var type,
                    out var frameLength,
                    out var decoded,
                    out var error))
            {
                HandleDecodeFailureUnlocked(offset, frameLength, buffer.Length, error);
                break;
            }

            ApplyDecodedFrameUnlocked(decoded);
            offset += frameLength;
        }

        _stream.Seek(0, SeekOrigin.End);
    }

    private void ApplyDecodedFrameUnlocked(in ArticleJournalDecodedFrame decoded)
    {
        switch (decoded.Type)
        {
            case ArticleJournalFrameType.Accept:
            case ArticleJournalFrameType.PhysicalWritten:
            case ArticleJournalFrameType.IndexCommitted:
            case ArticleJournalFrameType.SequenceFence:
                ApplyFrameUnlocked(
                    decoded.Type,
                    decoded.Accept,
                    decoded.PhysicalWritten,
                    decoded.IndexCommitted,
                    decoded.SequenceFence);
                break;
            default:
                ApplyCompactionFrameUnlocked(decoded);
                break;
        }
    }

    // Accept-path ApplyFrameUnlocked retained below.

    private void HandleDecodeFailureUnlocked(
        long offset,
        int frameLength,
        long fileLength,
        ArticleJournalFrameError error)
    {
        switch (error)
        {
            case ArticleJournalFrameError.Incomplete:
                TruncateTornTailUnlocked(offset, fileLength, "incomplete-final-frame");
                return;

            case ArticleJournalFrameError.CorruptChecksum:
            case ArticleJournalFrameError.Corrupt:
                if (frameLength > 0 && offset + frameLength < fileLength)
                {
                    FileArticleJournalLogMessages.MidFileCorrupt(
                        _logger,
                        _journalPath,
                        offset,
                        error.ToString());
                    throw new ArticleJournalCorruptException(
                        $"Article journal corrupt at offset {offset} ({error}) with trailing bytes after the failed frame.",
                        offset);
                }

                TruncateTornTailUnlocked(offset, fileLength, error.ToString());
                return;

            case ArticleJournalFrameError.CorruptLength:
                // Structurally invalid length is corruption, not a recoverable torn tail.
                // Genuine EOF shortfalls with an in-range TotalLength use Incomplete instead.
                FileArticleJournalLogMessages.MidFileCorrupt(
                    _logger,
                    _journalPath,
                    offset,
                    "corrupt-length");
                throw new ArticleJournalCorruptException(
                    $"Article journal corrupt length at offset {offset}.",
                    offset);

            default:
                throw new ArticleJournalCorruptException(
                    $"Article journal decode failed at offset {offset} ({error}).",
                    offset);
        }
    }

    private void TruncateTornTailUnlocked(long validEnd, long fileLength, string reason)
    {
        FileArticleJournalLogMessages.TruncatingTornTail(
            _logger,
            _journalPath,
            validEnd,
            fileLength,
            reason);
        _stream.SetLength(validEnd);
        _stream.Flush(flushToDisk: true);
    }

    private void ApplyFrameUnlocked(
        ArticleJournalFrameType type,
        JournalAcceptRecord? accept,
        JournalPhysicalWrittenRecord? physicalWritten,
        JournalIndexCommittedRecord? indexCommitted,
        ulong? sequenceFence)
    {
        switch (type)
        {
            case ArticleJournalFrameType.Accept:
                ArgumentNullException.ThrowIfNull(accept);
                if (_bySequence.ContainsKey(accept.Sequence))
                {
                    throw new ArticleJournalCorruptException(
                        $"Duplicate Accept sequence {accept.Sequence} during replay.",
                        0);
                }

                ApplyAcceptUnlocked(accept);
                if (accept.Sequence >= _nextSequence)
                {
                    _nextSequence = accept.Sequence + 1;
                }

                break;

            case ArticleJournalFrameType.PhysicalWritten:
                if (physicalWritten is null)
                {
                    throw new ArticleJournalCorruptException("PhysicalWritten frame missing body.");
                }

                ApplyPhysicalWrittenReplayUnlocked(physicalWritten.Value);
                break;

            case ArticleJournalFrameType.IndexCommitted:
                if (indexCommitted is null)
                {
                    throw new ArticleJournalCorruptException("IndexCommitted frame missing body.");
                }

                ApplyIndexCommittedReplayUnlocked(indexCommitted.Value);
                break;

            case ArticleJournalFrameType.SequenceFence:
                if (sequenceFence is null)
                {
                    throw new ArticleJournalCorruptException("SequenceFence frame missing body.");
                }

                if (sequenceFence.Value > _nextSequence)
                {
                    _nextSequence = sequenceFence.Value;
                }

                break;

            default:
                throw new ArticleJournalCorruptException($"Unknown journal frame type {(byte)type}.");
        }
    }

    private void ApplyAcceptUnlocked(JournalAcceptRecord record)
    {
        _bySequence[record.Sequence] = new SequenceState(record);
        _outstandingArtIdToSequence[record.ArtId] = record.Sequence;
        _outstandingRecoverableBytes += record.ArtSize;
    }

    private void ApplyPhysicalWrittenReplayUnlocked(JournalPhysicalWrittenRecord record)
    {
        if (!_bySequence.TryGetValue(record.Sequence, out var state))
        {
            throw new ArticleJournalCorruptException(
                $"PhysicalWritten for unknown sequence {record.Sequence}.");
        }

        if (state.PhysicalWritten is { } existing)
        {
            if (!LocationsEqual(existing.Location, record.Location))
            {
                throw new ArticleJournalCorruptException(
                    $"Conflicting PhysicalWritten locations for sequence {record.Sequence}.");
            }

            return;
        }

        if (record.Location.Length < state.Accept.ArtSize)
        {
            throw new ArticleJournalCorruptException(
                $"PhysicalWritten length mismatch for sequence {record.Sequence}.");
        }

        state.PhysicalWritten = record;
    }

    private void ApplyIndexCommittedReplayUnlocked(JournalIndexCommittedRecord record)
    {
        if (!_bySequence.TryGetValue(record.Sequence, out var state))
        {
            throw new ArticleJournalCorruptException(
                $"IndexCommitted for unknown sequence {record.Sequence}.");
        }

        if (state.IndexCommitted)
        {
            return;
        }

        if (state.PhysicalWritten is null)
        {
            throw new ArticleJournalCorruptException(
                $"IndexCommitted without PhysicalWritten for sequence {record.Sequence}.");
        }

        state.IndexCommitted = true;
        state.IndexCommittedRecord = record;
        _ = _outstandingArtIdToSequence.Remove(state.Accept.ArtId);
        _outstandingRecoverableBytes = Math.Max(0L, _outstandingRecoverableBytes - state.Accept.ArtSize);
    }

    private void AppendFrameUnlocked(byte[] frame, Func<long, PendingJournalFrame> ownerAt)
    {
        if (_pending is not null)
        {
            FinishPendingFrameUnlocked(frame, ownerAt);
            return;
        }

        ReconcileBlockedTailUnlocked();
        TestBeforeFrameAppend?.Invoke();
        _stream.Seek(0, SeekOrigin.End);
        var start = _stream.Position;
        try
        {
            _stream.Write(frame, 0, frame.Length);
            TestAfterWriteBeforeFlush?.Invoke(_stream, start, frame.Length);
            // Durability boundary: flush OS buffers to stable storage.
            // FileStream.Flush(flushToDisk: true) maps to FlushFileBuffers (Windows) / fsync (Unix).
            DurableFlushUnlocked();
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            var outcome = InspectAppendUnlocked(start, frame);
            if (outcome == AmbiguousAppend.Growth.CompleteExpected)
            {
                if (_stream.Length > start + frame.Length)
                {
                    try
                    {
                        TruncateOrBlockUnlocked(start + frame.Length, createdByThisCall: true);
                    }
                    catch (UnreconciledDurableTailException)
                    {
                        _pending = ownerAt(start);
                        throw;
                    }
                }

                try
                {
                    DurableFlushUnlocked();
                }
                catch (Exception flushEx) when (flushEx is not UnreconciledDurableTailException)
                {
                    _pending = ownerAt(start);
                    throw new UnreconciledDurableTailException(
                        "Article journal frame is present but not durable.",
                        flushEx,
                        createdByThisCall: true);
                }

                _stream.Seek(0, SeekOrigin.End);
                return;
            }

            if (outcome == AmbiguousAppend.Growth.IncompleteGrowth)
            {
                TruncateOrBlockUnlocked(start, createdByThisCall: true);
            }

            throw;
        }
    }

    /// <summary>
    /// Flushes a complete frame that is already at the pending offset. Does not write.
    /// A different logical owner leaves the pending frame unchanged.
    /// </summary>
    private void FinishPendingFrameUnlocked(byte[] frame, Func<long, PendingJournalFrame> ownerAt)
    {
        var pending = _pending
            ?? throw new InvalidOperationException("No pending journal frame.");
        var offered = ownerAt(pending.Offset);
        if (!pending.SameOwner(offered)
            || frame.Length != pending.Length
            || XxHash3.HashToUInt64(frame) != pending.PayloadHash)
        {
            throw PendingOwnedByOther();
        }

        if (!PendingBytesMatchUnlocked(pending))
        {
            throw new UnreconciledDurableTailException(
                "Pending journal frame bytes changed before the durable flush.",
                new IOException("Pending durable payload no longer matches the file."));
        }

        try
        {
            DurableFlushUnlocked();
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            throw new UnreconciledDurableTailException(
                "Article journal frame is present but not durable.",
                ex);
        }

        _pending = null;
        _stream.Seek(0, SeekOrigin.End);
    }

    private static UnreconciledDurableTailException PendingOwnedByOther() =>
        new(
            "A different journal operation is waiting for a durable flush.",
            new IOException("Pending durable payload identity mismatch."));

    private void ThrowIfPendingJournalAppendUnlocked()
    {
        if (_pending is null && !_tailUnreconciled)
        {
            return;
        }

        throw new UnreconciledDurableTailException(
            "Article journal checkpoint cannot replace the file while a physical append is pending.",
            new IOException("pending-journal-frame"));
    }

    private void DurableFlushUnlocked()
    {
        TestBeforeDurableFlush?.Invoke();
        _stream.Flush(flushToDisk: true);
    }

    private bool PendingBytesMatchUnlocked(PendingJournalFrame pending)
    {
        if (_stream.Length < pending.Offset + pending.Length)
        {
            return false;
        }

        var observed = ReadExactUnlocked(pending.Offset, pending.Length);
        return observed.Length == pending.Length
            && XxHash3.HashToUInt64(observed) == pending.PayloadHash;
    }

    /// <summary>
    /// Pushes any buffered write so length and a read-back see the same bytes, then classifies them.
    /// Inspection failure keeps the tail blocked so a later append cannot add a second copy.
    /// </summary>
    private AmbiguousAppend.Growth InspectAppendUnlocked(long start, byte[] frame)
    {
        try
        {
            _stream.Flush(flushToDisk: false);
            var length = _stream.Length;
            var observed = length >= start + frame.Length
                ? ReadExactUnlocked(start, frame.Length)
                : [];
            return AmbiguousAppend.Classify(start, length, frame, observed);
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            _tailUnreconciled = true;
            _tailValidEnd = start;
            throw new UnreconciledDurableTailException(
                "Article journal length could not be inspected after an ambiguous append.",
                ex,
                createdByThisCall: true);
        }
    }

    private byte[] ReadExactUnlocked(long offset, int length)
    {
        var buffer = new byte[length];
        _stream.Position = offset;
        var filled = 0;
        while (filled < length)
        {
            var read = _stream.Read(buffer, filled, length - filled);
            if (read == 0)
            {
                _stream.Seek(0, SeekOrigin.End);
                return [];
            }

            filled += read;
        }

        _stream.Seek(0, SeekOrigin.End);
        return buffer;
    }

    private void ReconcileBlockedTailUnlocked()
    {
        if (!_tailUnreconciled)
        {
            return;
        }

        TruncateOrBlockUnlocked(_tailValidEnd, createdByThisCall: false);
    }

    private void TruncateOrBlockUnlocked(long validEnd, bool createdByThisCall)
    {
        if (TestFailTailTruncate)
        {
            _tailUnreconciled = true;
            _tailValidEnd = validEnd;
            throw new UnreconciledDurableTailException(
                "Article journal tail could not be reconciled after an ambiguous append.",
                new IOException("truncate-failed"),
                createdByThisCall);
        }

        try
        {
            TruncateTornTailUnlocked(validEnd, _stream.Length, "ambiguous-append");
            _tailUnreconciled = false;
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            _tailUnreconciled = true;
            _tailValidEnd = validEnd;
            throw new UnreconciledDurableTailException(
                "Article journal tail could not be reconciled after an ambiguous append.",
                ex,
                createdByThisCall);
        }
    }

    /// <summary>
    /// Writes a flushed temporary replacement, installs it, and reopens it.
    /// Does not mutate <see cref="_bySequence"/>. On failure before successful reopen of a
    /// replaced file, restores the previous journal stream when possible; otherwise marks
    /// the instance disposed/unusable.
    /// </summary>
    private void InstallReplacementJournalUnlocked(SequenceState[] incomplete) =>
        InstallReplacementJournalCoreUnlocked(temp => WriteCheckpointBodyUnlocked(temp, incomplete));

    private void InstallReplacementJournalFromImageUnlocked(byte[] image) =>
        InstallReplacementJournalCoreUnlocked(temp => temp.Write(image, 0, image.Length));

    private void InstallReplacementJournalCoreUnlocked(Action<FileStream> writeBody)
    {
        var directory = Path.GetDirectoryName(_journalPath)
            ?? throw new InvalidOperationException("Journal path has no directory.");
        var tempPath = Path.Combine(
            directory,
            $".{JournalFileName}.{Guid.NewGuid():N}.tmp");

        var oldStream = _stream;
        var oldStreamClosed = false;
        var replaced = false;
        FileStream? newStream = null;

        try
        {
            using (var temp = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.None))
            {
                writeBody(temp);
                temp.Flush(flushToDisk: true);
            }

            CheckpointTestFault?.Invoke(CheckpointFaultPoint.AfterTempFlushed);

            oldStream.Dispose();
            oldStreamClosed = true;
            CheckpointTestFault?.Invoke(CheckpointFaultPoint.AfterOldStreamClosedBeforeMove);

            File.Move(tempPath, _journalPath, overwrite: true);
            replaced = true;
            tempPath = string.Empty;

            CheckpointTestFault?.Invoke(CheckpointFaultPoint.AfterMoveBeforeReopen);

            newStream = new FileStream(
                _journalPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.None);
            newStream.Seek(0, SeekOrigin.End);
            _stream = newStream;
            newStream = null;
        }
        catch (Exception)
        {
            if (!string.IsNullOrEmpty(tempPath) && !TryDeleteCheckpointTemp(tempPath))
            {
                _retainCheckpointTempReservation = true;
            }

            newStream?.Dispose();

            if (!replaced)
            {
                if (oldStreamClosed)
                {
                    try
                    {
                        _stream = OpenJournalStream(_journalPath);
                    }
                    catch (Exception reopenEx)
                    {
                        MarkUnusableUnlocked();
                        throw new InvalidOperationException(
                            "Article journal checkpoint failed and the previous journal could not be reopened.",
                            reopenEx);
                    }
                }

                // oldStream still open, or successfully reopened: memory unchanged, usable.
                throw;
            }

            // Replacement is on disk but this instance could not reopen it — fail closed.
            MarkUnusableUnlocked();
            throw;
        }
    }

    private static FileStream OpenJournalStream(string path) =>
        new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.None);

    private void MarkUnusableUnlocked()
    {
        _disposed = true;
        try
        {
            _stream.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already closed during failed checkpoint.
        }
        catch (IOException)
        {
            // Best-effort close while failing closed.
        }
    }

    private StorageWritePressure ComputePressureUnlocked()
    {
        if (_outstandingRecoverableBytes >= _hardLimitBytes)
        {
            return StorageWritePressure.Critical;
        }

        if (_outstandingRecoverableBytes >= _softLimitBytes)
        {
            return StorageWritePressure.Elevated;
        }

        return StorageWritePressure.Normal;
    }

    private void LogPressureIfChangedUnlocked()
    {
        var pressure = ComputePressureUnlocked();
        if (pressure == _lastLoggedPressure)
        {
            return;
        }

        _lastLoggedPressure = pressure;
        FileArticleJournalLogMessages.Pressure(
            _logger,
            pressure,
            _outstandingRecoverableBytes,
            _softLimitBytes,
            _hardLimitBytes);
    }

    private static bool LocationsEqual(in StoredArticleLocation left, in StoredArticleLocation right) =>
        left.SegmentId.Value == right.SegmentId.Value
        && left.Offset == right.Offset
        && left.Length == right.Length;

    private bool TryDeleteCheckpointTemp(string path)
    {
        if (TestFailCheckpointTempDelete && File.Exists(path))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort.
        }

        return !File.Exists(path);
    }

    private sealed class SequenceState(JournalAcceptRecord accept)
    {
        public JournalAcceptRecord Accept { get; } = accept;

        public JournalPhysicalWrittenRecord? PhysicalWritten { get; set; }

        public bool IndexCommitted { get; set; }

        public JournalIndexCommittedRecord? IndexCommittedRecord { get; set; }
    }

    /// <summary>
    /// One process-local complete journal frame whose durability flush has not returned.
    /// The subclass is the logical owner. Not written to disk and not restored after restart.
    /// </summary>
    private abstract class PendingJournalFrame
    {
        private PendingJournalFrame(long offset, byte[] payload)
        {
            Offset = offset;
            Length = payload.Length;
            PayloadHash = XxHash3.HashToUInt64(payload);
        }

        public long Offset { get; }

        public int Length { get; }

        public ulong PayloadHash { get; }

        public abstract bool SameOwner(PendingJournalFrame other);

        public sealed class AcceptOperation : PendingJournalFrame
        {
            public AcceptOperation(long offset, JournalAcceptRecord record, byte[] payload)
                : base(offset, payload)
            {
                Record = record;
            }

            public JournalAcceptRecord Record { get; }

            public ArticleId ArtId => Record.ArtId;

            public ulong ArtHash => Record.ArtHash;

            public int ArtSize => Record.ArtSize;

            public ulong Sequence => Record.Sequence;

            public override bool SameOwner(PendingJournalFrame other) =>
                other is AcceptOperation accept
                && accept.Sequence == Sequence
                && accept.ArtId == ArtId
                && accept.ArtHash == ArtHash
                && accept.ArtSize == ArtSize;
        }

        public sealed class PhysicalWrittenOperation : PendingJournalFrame
        {
            public PhysicalWrittenOperation(
                long offset,
                ulong sequence,
                StoredArticleLocation location,
                byte[] payload)
                : base(offset, payload)
            {
                Sequence = sequence;
                Location = location;
            }

            public ulong Sequence { get; }

            public StoredArticleLocation Location { get; }

            public override bool SameOwner(PendingJournalFrame other) =>
                other is PhysicalWrittenOperation written
                && written.Sequence == Sequence
                && written.Location == Location;
        }

        public sealed class IndexCommittedOperation : PendingJournalFrame
        {
            public IndexCommittedOperation(long offset, ulong sequence, byte[] payload)
                : base(offset, payload)
            {
                Sequence = sequence;
            }

            public ulong Sequence { get; }

            public override bool SameOwner(PendingJournalFrame other) =>
                other is IndexCommittedOperation committed && committed.Sequence == Sequence;
        }

        public sealed class CompactionOperation : PendingJournalFrame
        {
            public CompactionOperation(
                long offset,
                ArticleJournalFrameType frameType,
                ulong compactionId,
                ulong relocationId,
                byte[] payload,
                JournalCompactionBeginRecord? begin = null,
                StoredArticleLocation? writtenDestination = null)
                : base(offset, payload)
            {
                FrameType = frameType;
                CompactionId = compactionId;
                RelocationId = relocationId;
                Begin = begin;
                WrittenDestination = writtenDestination;
            }

            public ArticleJournalFrameType FrameType { get; }

            public ulong CompactionId { get; }

            public ulong RelocationId { get; }

            public JournalCompactionBeginRecord? Begin { get; }

            public StoredArticleLocation? WrittenDestination { get; }

            public override bool SameOwner(PendingJournalFrame other) =>
                other is CompactionOperation compaction
                && compaction.FrameType == FrameType
                && compaction.CompactionId == CompactionId
                && compaction.RelocationId == RelocationId;
        }
    }
}
