using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Session.Commands;
using VectorNNTP.NNTPD.Session.CommandProcessor;
using VectorNNTP.NNTPD.Session.Framing;
using VectorNNTP.NNTPD.Diagnostics;

namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// Per-session bounded TAKETHIS window: one RX owner frames articles, then detached
/// slots complete Peek / enqueue / ordered 239 off the RX stack.
/// </summary>
/// <remarks>
/// <para>
/// The session RX task is the only consumer of <c>Connection.Input</c>. It parses the
/// TAKETHIS command, starts HistoryDB peek, frames the article with
/// <see cref="IHaveArticleReader"/> (one owned stuffed-wire buffer, terminator omitted),
/// attaches that buffer to a slot, and returns. Pipeline workers never call
/// <c>ReadAsync</c> on the session pipe. Depth matches the CHECK window and bounds
/// how many owned article buffers may be retained.
/// </para>
/// <para>
/// After detach, <see cref="CompleteWhenAsync"/> waits for Peek if needed, then
/// enqueue / Remember / ordered 239/439/400. Emit never runs on the RX task, including
/// when Peek already completed during receive. A slot is occupied from Message-ID
/// copy / peek start until the response writer accepts the ordered line (or cancel).
/// </para>
/// </remarks>
internal sealed class TakeThisPipeline
{
    /// <summary>
    /// Outstanding TAKETHIS executions per session. Architectural constant; not configurable.
    /// </summary>
    public const int Depth = CheckPipeline.Depth;

    private readonly NntpSession _session;
    private readonly NntpResponseWriter _response;
    private readonly ILogger _logger;
    private readonly Slot?[] _slots;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _emitGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly TakeThisStageSessionLog? _stageLog;
    private int _head;
    private int _count;
    private int _peakOccupied;
    private int _shutDown;
    private int _inFlightCompletions;
    private int _activeArticleReads;
    private int _maxActiveArticleReads;
    private TaskCompletionSource _progress = NewProgress();

    /// <summary>Initializes a new instance of the <see cref="TakeThisPipeline"/> class.</summary>
    public TakeThisPipeline(NntpSession session, NntpResponseWriter response)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(response);
        _session = session;
        _response = response;
        _logger = NntpCommandLoggers.For(typeof(TakeThis));
        _slots = new Slot?[Depth];
        _stageLog = TakeThisStageProbe.CreateSessionLog();
    }

    /// <summary>Gets the number of occupied slots.</summary>
    public int Occupied
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Gets whether the window cannot accept another TAKETHIS without waiting.</summary>
    public bool IsFull => Occupied >= Depth;

    /// <summary>Gets the peak occupied slot count observed on this session.</summary>
    public int PeakOccupied => Volatile.Read(ref _peakOccupied);

    /// <summary>Gets the number of completion tasks still running (at most <see cref="Depth"/>).</summary>
    internal int InFlightCompletions => Volatile.Read(ref _inFlightCompletions);

    /// <summary>
    /// Test-only: set after HistoryDB peek is started and before article receive begins.
    /// </summary>
    internal Action? AfterPeekStarted { get; set; }

    /// <summary>
    /// Test-only: set immediately before <see cref="IHaveArticleReader.ReadAsync"/> is awaited.
    /// </summary>
    internal Action? AfterReceiveStarted { get; set; }

    /// <summary>
    /// Test-only: set after <see cref="IHaveArticleReader.ReadAsync"/> returns (including cancel).
    /// </summary>
    internal Action? AfterReceiveCompleted { get; set; }

    /// <summary>
    /// Test-only: when set, <see cref="AdmitAuthorizedAsync"/> awaits this task after
    /// <see cref="AfterReceiveStarted"/> and before <see cref="IHaveArticleReader.ReadAsync"/>.
    /// Production never sets this.
    /// </summary>
    internal Task? HoldReceive { get; set; }

    /// <summary>
    /// Test-only: invoked after the owned article is stored on the slot and before
    /// admission returns to the RX loop. Production never sets this.
    /// </summary>
    internal Action<ReadOnlyMemory<byte>>? AfterArticleDetached { get; set; }

    /// <summary>
    /// Test-only: awaited after response composition and before
    /// <see cref="NntpResponseWriter.EnqueueLineImmediateAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.
    /// The slot remains occupied. Production never sets this.
    /// </summary>
    internal Func<ValueTask>? BeforeEnqueueProbe { get; set; }

    /// <summary>Gets how many <see cref="IHaveArticleReader.ReadAsync"/> calls are on this session's RX stack.</summary>
    internal int ActiveArticleReads => Volatile.Read(ref _activeArticleReads);

    /// <summary>Gets the peak concurrent article-receive count observed on this session (must stay 1).</summary>
    internal int MaxActiveArticleReads => Volatile.Read(ref _maxActiveArticleReads);

    /// <summary>Waits until a slot is free. The caller must not read RX while this is pending.</summary>
    public async ValueTask WaitForCapacityAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                ThrowIfShutDown();
                if (_count < Depth)
                {
                    return;
                }

                wait = _progress.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts HistoryDB peek, frames the STREAM article on the RX task, attaches the
    /// owned buffer to a slot, and returns. Does not wait for Peek, enqueue, Remember,
    /// or 239 after the article is detached from the PipeReader.
    /// </summary>
    public async ValueTask AdmitAuthorizedAsync(
        NntpCommand command,
        ReadOnlyMemory<byte> line,
        CancellationToken cancellationToken)
    {
        ThrowIfShutDown();
        var probe = _session.FeedProbe;
        probe?.RecordCommand();
        _session.SetActivityState(FeedSessionState.Receiving);
        var ownedId = command.ArgumentMemory(line).ToArray();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var marks = _stageLog is null ? null : new TakeThisStageMarks
        {
            CommandParsedTs = started,
            PeekStartTs = started,
        };
        var lookup = TakeThis.PeekAsync(_session, ownedId, cancellationToken);
        if (marks is not null)
        {
            lookup = marks.ObservePeek(lookup);
        }

        AfterPeekStarted?.Invoke();

        Slot slot;
        lock (_gate)
        {
            ThrowIfShutDown();
            if (_count >= Depth)
            {
                throw new InvalidOperationException("TAKETHIS pipeline accepted a command without a free slot.");
            }

            slot = new Slot
            {
                MessageId = ownedId,
                StartedTimestamp = started,
                Index = (_head + _count) % Depth,
                Lookup = lookup,
                Marks = marks,
            };
            _slots[slot.Index] = slot;
            _count++;
            if (_count > _peakOccupied)
            {
                _peakOccupied = _count;
            }

            _stageLog?.NoteOccupied(_count);
        }

        if (marks is not null)
        {
            marks.ReceiveStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        AfterReceiveStarted?.Invoke();
        var reads = Interlocked.Increment(ref _activeArticleReads);
        if (reads > _maxActiveArticleReads)
        {
            _maxActiveArticleReads = reads;
        }

        IHaveArticleReadResult read;
        try
        {
            if (HoldReceive is { } holdReceive)
            {
                await holdReceive.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            read = await IHaveArticleReader
                .ReadAsync(_session.Connection.Input, _session.ArticleIngestion.MaxArticleBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || _session.Connection.ConnectionClosed.IsCancellationRequested)
        {
            Interlocked.Decrement(ref _activeArticleReads);
            AfterReceiveCompleted?.Invoke();
            CancelAndRelease(slot);
            return;
        }

        Interlocked.Decrement(ref _activeArticleReads);
        AfterReceiveCompleted?.Invoke();

        if (marks is not null)
        {
            marks.ReceiveCompleteTs = System.Diagnostics.Stopwatch.GetTimestamp();
            marks.ArticleBytes = read.Payload.Length;
        }

        if (probe is not null)
        {
            var receiveStart = marks?.ReceiveStartTs ?? started;
            probe.RecordArticleReceived(
                read.Payload.Length,
                System.Diagnostics.Stopwatch.GetTimestamp() - receiveStart);
        }

        _session.RecordPeerArticleReceived(read.Payload.Length);
        _session.SetActivityState(FeedSessionState.WaitingHistory);

        lock (_gate)
        {
            if (!IsPresentNoLock(slot))
            {
                return;
            }

            slot.Read = read;
            slot.ArticleReady = true;
            SignalProgressNoLock();
        }

        AfterArticleDetached?.Invoke(read.Payload);
        _ = CompleteWhenAsync(slot, lookup, cancellationToken);
    }

    /// <summary>Waits until every slot has been accepted by the TX writer (or cancelled).</summary>
    public async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_count == 0)
                {
                    return;
                }

                wait = _progress.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stops emission and waits until every slot is released.</summary>
    public async ValueTask ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutDown, 1) == 1)
        {
            return;
        }

        try
        {
            await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        lock (_gate)
        {
            for (var i = 0; i < Depth; i++)
            {
                if (_slots[i] is { } slot)
                {
                    slot.Cancelled = true;
                }
            }

            ReleaseCancelledPrefixNoLock();
            SignalProgressNoLock();
        }

        try
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_count == 0)
                    {
                        return;
                    }

                    wait = _progress.Task;
                }

                await wait.ConfigureAwait(false);
            }
        }
        finally
        {
            _lifetimeCts.Dispose();
            WriteStageLog();
        }
    }

    private async Task CompleteWhenAsync(
        Slot slot,
        ValueTask<HistoryLookupResult> lookup,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _inFlightCompletions);
        try
        {
            // Leave the RX stack before Peek/enqueue/239, including when Peek already completed.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

            HistoryLookupResult peek;
            var peekWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                peek = await lookup.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || Volatile.Read(ref _shutDown) == 1)
            {
                CancelAndRelease(slot);
                return;
            }
            catch (OperationCanceledException)
            {
                peek = HistoryLookupResult.Unavailable;
            }
            catch (Exception)
            {
                peek = HistoryLookupResult.Unavailable;
            }

            _session.FeedProbe?.RecordHistory(
                peek,
                System.Diagnostics.Stopwatch.GetTimestamp() - peekWaitStart);

            var emit = false;
            lock (_gate)
            {
                if (!IsPresentNoLock(slot))
                {
                    return;
                }

                if (Volatile.Read(ref _shutDown) == 1)
                {
                    slot.PeekCompleted = true;
                    slot.Cancelled = true;
                    ReleaseCancelledPrefixNoLock();
                    SignalProgressNoLock();
                    return;
                }

                slot.PeekCompleted = true;
                slot.Peek = peek;
                emit = HeadIsReadyNoLock();
                SignalProgressNoLock();
            }

            if (emit)
            {
                try
                {
                    await EmitReadySerializedAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested || Volatile.Read(ref _shutDown) == 1)
                {
                    CancelAndRelease(slot);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightCompletions);
        }
    }

    private async ValueTask EmitReadySerializedAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        var token = linked.Token;
        await _emitGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Slot? slot;
                lock (_gate)
                {
                    if (Volatile.Read(ref _shutDown) == 1)
                    {
                        ReleaseCancelledPrefixNoLock();
                        SignalProgressNoLock();
                        return;
                    }

                    slot = TryBeginEmitHeadNoLock();
                }

                if (slot is null)
                {
                    return;
                }

                try
                {
                    await EmitOccupiedAsync(slot, token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        ReleaseHeadIfNoLock(slot);
                        SignalProgressNoLock();
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        slot.Cancelled = true;
                        slot.Emitting = false;
                        ReleaseCancelledPrefixNoLock();
                        SignalProgressNoLock();
                    }

                    throw;
                }
            }
        }
        finally
        {
            _emitGate.Release();
        }
    }

    private async ValueTask EmitOccupiedAsync(Slot slot, CancellationToken cancellationToken)
    {
        if (slot.Marks is { } emitMarks)
        {
            emitMarks.EmitStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
            emitMarks.OccupiedAtEmit = Occupied;
        }

        var read = slot.Read;
        if (read.Status == NntpMultilineReadStatus.Incomplete)
        {
            return;
        }

        if (read.Status == NntpMultilineReadStatus.TooLarge)
        {
            await EnqueueReplyAsync(slot, NntpResponses.TransferRejectedPrefix, cancellationToken)
                .ConfigureAwait(false);
            WriteTxIfDebug(slot, "rejected too large", TransferLogKind.Rejected);
            return;
        }

        if (slot.Peek == HistoryLookupResult.Unavailable)
        {
            await TakeThis.FailTemporaryAsync(_session, _response, cancellationToken).ConfigureAwait(false);
            WriteTxIfDebug(slot, "temporary failure", TransferLogKind.Temporary);
            return;
        }

        if (slot.Peek == HistoryLookupResult.Seen)
        {
            if (slot.Marks is { } seenMarks)
            {
                seenMarks.Outcome = "seen";
            }

            var probe = _session.FeedProbe;
            _session.SetActivityState(FeedSessionState.Completing);
            var responseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            await EnqueueReplyAsync(slot, NntpResponses.ArticleTransferredOkPrefix, cancellationToken)
                .ConfigureAwait(false);
            WriteTxIfDebug(slot, "accepted duplicate", TransferLogKind.Accepted);
            probe?.RecordArticleCompleted(
                duplicate: true,
                System.Diagnostics.Stopwatch.GetTimestamp() - responseStart);
            _session.SetActivityState(FeedSessionState.Idle);
            RecordAccepted(slot);
            return;
        }

        var queue = _session.ArticleIngestion;
        if (!queue.IsAccepting)
        {
            await TakeThis.FailTemporaryAsync(_session, _response, cancellationToken).ConfigureAwait(false);
            WriteTxIfDebug(slot, "temporary failure", TransferLogKind.Temporary);
            return;
        }

        var messageIdText = System.Text.Encoding.ASCII.GetString(slot.MessageId);
        var inbound = new InboundArticle(
            messageIdText,
            read.Payload,
            _session.ClientIdentity,
            DateTimeOffset.UtcNow,
            structured: null,
            InboundArticleProducer.TakeThis);

        ArticleEnqueueResult enqueue;
        try
        {
            if (slot.Marks is { } enqueueMarks)
            {
                enqueueMarks.EnqueueStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            var probe = _session.FeedProbe;
            _session.SetActivityState(FeedSessionState.WaitingQueue);
            var queueStart = System.Diagnostics.Stopwatch.GetTimestamp();
            enqueue = await queue.EnqueueAsync(inbound, cancellationToken).ConfigureAwait(false);
            probe?.RecordQueue(enqueue, System.Diagnostics.Stopwatch.GetTimestamp() - queueStart, read.Payload.Length);
            if (slot.Marks is { } acceptedMarks)
            {
                acceptedMarks.EnqueueAcceptedTs = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || _session.Connection.ConnectionClosed.IsCancellationRequested)
        {
            return;
        }

        if (enqueue == ArticleEnqueueResult.Unavailable)
        {
            await TakeThis.FailTemporaryAsync(_session, _response, cancellationToken).ConfigureAwait(false);
            WriteTxIfDebug(slot, "temporary failure", TransferLogKind.Temporary);
            return;
        }

        if (enqueue == ArticleEnqueueResult.Rejected)
        {
            var rejectProbe = _session.FeedProbe;
            _session.SetActivityState(FeedSessionState.Completing);
            var rejectStart = System.Diagnostics.Stopwatch.GetTimestamp();
            await EnqueueReplyAsync(slot, NntpResponses.TransferRejectedPrefix, cancellationToken)
                .ConfigureAwait(false);
            WriteTxIfDebug(slot, "rejected exceeds queue budget", TransferLogKind.Rejected);
            rejectProbe?.RecordArticleCompleted(
                duplicate: false,
                System.Diagnostics.Stopwatch.GetTimestamp() - rejectStart);
            _session.SetActivityState(FeedSessionState.Idle);
            return;
        }

        if (slot.Marks is { } rememberMarks)
        {
            rememberMarks.Outcome = "unseen";
            rememberMarks.RememberStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        _session.HistoryDb?.Remember(slot.MessageId);
        if (slot.Marks is { } rememberedMarks)
        {
            rememberedMarks.RememberCompleteTs = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        var completeProbe = _session.FeedProbe;
        _session.SetActivityState(FeedSessionState.Completing);
        var completeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        await EnqueueReplyAsync(slot, NntpResponses.ArticleTransferredOkPrefix, cancellationToken)
            .ConfigureAwait(false);
        WriteTxIfDebug(slot, "accepted", TransferLogKind.Accepted);
        completeProbe?.RecordArticleCompleted(
            duplicate: false,
            System.Diagnostics.Stopwatch.GetTimestamp() - completeStart);
        _session.SetActivityState(FeedSessionState.Idle);
        RecordAccepted(slot);
    }

    private enum TransferLogKind
    {
        Accepted,
        Rejected,
        Temporary,
    }

    private void WriteTxIfDebug(Slot slot, string detail, TransferLogKind kind)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var status = kind switch
        {
            TransferLogKind.Rejected => NntpCommandStatusText.FormatTakeThisRejected(slot.MessageId),
            TransferLogKind.Temporary => NntpResponseStatus.ServiceTemporarilyUnavailable,
            _ => NntpCommandStatusText.FormatTakeThisAccepted(slot.MessageId),
        };
        TakeThis.WriteCompletion(_logger, _session, slot.StartedTimestamp, detail, status);
    }

    private ValueTask EnqueueReplyAsync(
        Slot slot,
        ReadOnlyMemory<byte> prefix,
        CancellationToken cancellationToken)
    {
        var owned = NntpResponseCompose.Concat(prefix.Span, slot.MessageId, NntpResponses.Crlf.Span);
        if (slot.Marks is { } marks)
        {
            return EnqueueReplyTimedAsync(marks, owned, cancellationToken);
        }

        return EnqueueReplyImmediateAsync(owned, cancellationToken);
    }

    private async ValueTask EnqueueReplyImmediateAsync(
        ReadOnlyMemory<byte> owned,
        CancellationToken cancellationToken)
    {
        if (BeforeEnqueueProbe is { } probe)
        {
            await probe().ConfigureAwait(false);
            if (Volatile.Read(ref _shutDown) == 1 || cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        await _response.EnqueueLineImmediateAsync(owned, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnqueueReplyTimedAsync(
        TakeThisStageMarks marks,
        ReadOnlyMemory<byte> owned,
        CancellationToken cancellationToken)
    {
        marks.ReplyEnqueueStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
        await EnqueueReplyImmediateAsync(owned, cancellationToken).ConfigureAwait(false);
        marks.ReplyEnqueueCompleteTs = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private void RecordAccepted(Slot slot)
    {
        if (_stageLog is null || slot.Marks is not { } marks)
        {
            return;
        }

        _stageLog.Record(marks.ToSample(_peakOccupied));
    }

    private void WriteStageLog()
    {
        if (_stageLog is null || !TakeThisStageProbe.IsEnabled)
        {
            return;
        }

        var dir = TakeThisStageProbe.OutputDirectory;
        if (dir is null)
        {
            return;
        }

        try
        {
            var remote = _session.ClientIdentity.TcpPeer?.ToString() ?? "session";
            var path = _stageLog.Write(dir, remote);
            _logger.LogInformation(
                "TAKETHIS stage timing wrote {Path} samples={Samples} peak={Peak}",
                path,
                _stageLog.Count,
                _stageLog.PeakOccupied);
        }
        catch
        {
            // Diagnostic dump must not affect shutdown.
        }
    }

    private bool HeadIsReadyNoLock()
    {
        if (_count == 0)
        {
            return false;
        }

        var slot = _slots[_head];
        return slot is { ArticleReady: true, PeekCompleted: true, Cancelled: false, Emitting: false };
    }

    private Slot? TryBeginEmitHeadNoLock()
    {
        if (!HeadIsReadyNoLock())
        {
            return null;
        }

        var slot = _slots[_head]!;
        slot.Emitting = true;
        return slot;
    }

    private void ReleaseHeadIfNoLock(Slot slot)
    {
        if (_count == 0 || !ReferenceEquals(_slots[_head], slot))
        {
            return;
        }

        _slots[_head] = null;
        _head = (_head + 1) % Depth;
        _count--;
        slot.Emitting = false;
    }

    private void CancelAndRelease(Slot slot)
    {
        lock (_gate)
        {
            if (!IsPresentNoLock(slot))
            {
                return;
            }

            slot.ArticleReady = true;
            slot.PeekCompleted = true;
            slot.Cancelled = true;
            slot.Emitting = false;
            ReleaseCancelledPrefixNoLock();
            SignalProgressNoLock();
        }
    }

    private void ReleaseCancelledPrefixNoLock()
    {
        while (_count > 0)
        {
            var slot = _slots[_head];
            if (slot is null || slot.Emitting || !slot.Cancelled)
            {
                break;
            }

            _slots[_head] = null;
            _head = (_head + 1) % Depth;
            _count--;
        }
    }

    private bool IsPresentNoLock(Slot slot) =>
        (uint)slot.Index < Depth && ReferenceEquals(_slots[slot.Index], slot);

    private void ThrowIfShutDown()
    {
        if (Volatile.Read(ref _shutDown) == 1)
        {
            throw new OperationCanceledException("TAKETHIS pipeline has shut down.");
        }
    }

    private void SignalProgressNoLock()
    {
        var previous = _progress;
        _progress = NewProgress();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewProgress() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Slot
    {
        public required byte[] MessageId { get; init; }

        public required int Index { get; init; }

        public required long StartedTimestamp { get; init; }

        public ValueTask<HistoryLookupResult> Lookup { get; init; }

        public TakeThisStageMarks? Marks { get; init; }

        public IHaveArticleReadResult Read { get; set; }

        public HistoryLookupResult Peek { get; set; }

        public bool ArticleReady { get; set; }

        public bool PeekCompleted { get; set; }

        public bool Cancelled { get; set; }

        public bool Emitting { get; set; }
    }
}
