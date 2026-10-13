using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.ArticleIndex;

/// <summary>
/// Single writer for article-index mutations. Producers enqueue; this type is the only committer.
/// </summary>
/// <remarks>
/// <para>
/// A command is complete only after its transaction commits and its completion is set.
/// A failed transaction rolls back, completes its waiters with the exception, and does not publish
/// membership. Busy and locked results retry a bounded number of times. The statements are idempotent,
/// so a retry does not reset <c>ReceivedAt</c>.
/// </para>
/// <para>
/// The queue is bounded. A full queue waits. Touch commands for an article that already has a queued
/// token join that token, up to the coalescer limits, and keep the newest timestamp.
/// </para>
/// </remarks>
internal sealed class ArticleIndexWriter
{
    private readonly ArticleIndexDatabase _database;
    private readonly ArticlePresenceIndex _presence;
    private readonly ArticleIndexSettings _settings;
    private readonly ArticleIndexCounters _counters;
    private readonly ArticleAccessCoalescer _coalescer;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _spoolDirectory;
    private readonly SemaphoreSlim? _checkpointGate;
    private readonly Channel<ArticleIndexRequest> _channel;
    private readonly Dictionary<ArticleId, ArticleIndexMutation> _byId = new();
    private readonly List<ArticleIndexMutation> _mutations = new();
    private long _batchStarted;
    private long _lastSummary;
    private int _batchesSinceCheckpoint;

    /// <summary>Initializes the writer. The database must already be open.</summary>
    /// <param name="database">Writer connection owner.</param>
    /// <param name="presence">Membership index published after commit.</param>
    /// <param name="settings">Batch size, flush interval, and queue capacity.</param>
    /// <param name="counters">Process counters.</param>
    /// <param name="coalescer">Bounded touch coalescer.</param>
    /// <param name="logger">Batch and checkpoint logger.</param>
    /// <param name="time">Clock for flush intervals and summaries.</param>
    /// <param name="spoolDirectory">Spool whose database this writer owns.</param>
    /// <param name="checkpointGate">
    /// Limits concurrent checkpoints across spool databases. Null allows this writer to checkpoint immediately.
    /// A busy gate skips a passive checkpoint and does not delay commits on other spools.
    /// </param>
    internal ArticleIndexWriter(
        ArticleIndexDatabase database,
        ArticlePresenceIndex presence,
        ArticleIndexSettings settings,
        ArticleIndexCounters counters,
        ArticleAccessCoalescer coalescer,
        ILogger logger,
        TimeProvider time,
        string spoolDirectory,
        SemaphoreSlim? checkpointGate)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(coalescer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);
        _database = database;
        _presence = presence;
        _settings = settings;
        _counters = counters;
        _coalescer = coalescer;
        _logger = logger;
        _time = time;
        _spoolDirectory = spoolDirectory;
        _checkpointGate = checkpointGate;
        _channel = Channel.CreateBounded<ArticleIndexRequest>(new BoundedChannelOptions(settings.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _counters.NoteQueue(0, settings.QueueCapacity);
    }

    /// <summary>Gets the number of commands accepted by the queue and not yet read.</summary>
    internal int QueueDepth => _channel.Reader.Count;

    /// <summary>Gets the queue capacity.</summary>
    internal int QueueCapacity => _settings.QueueCapacity;

    /// <summary>Gets the spool whose database this writer owns.</summary>
    internal string SpoolDirectory => _spoolDirectory;

    /// <summary>Stops accepting commands. Commands already queued are still committed.</summary>
    internal void Complete() => _channel.Writer.TryComplete();

    /// <summary>
    /// Enqueues an insert and waits until that batch commits.
    /// </summary>
    /// <param name="articleId">Canonical article identity.</param>
    /// <param name="spoolDirectory">Validated spool root.</param>
    /// <param name="receivedAtUnixMilliseconds">UTC Unix epoch milliseconds used for both initial timestamps.</param>
    /// <param name="cancellationToken">Cancels the wait. A command already queued can still commit.</param>
    /// <returns>Whether this call created the row.</returns>
    internal async ValueTask<ArticleIndexInsertResult> InsertAsync(
        ArticleId articleId,
        string spoolDirectory,
        long receivedAtUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);
        if (spoolDirectory.Length > ArticleIndexSettings.MaxSpoolDirectoryChars)
        {
            throw new ArgumentException(
                $"Spool directory exceeds {ArticleIndexSettings.MaxSpoolDirectoryChars} characters.",
                nameof(spoolDirectory));
        }

        EnsureTimestamp(receivedAtUnixMilliseconds);
        var request = new ArticleIndexRequest(
            ArticleIndexCommandKind.Insert,
            articleId,
            receivedAtUnixMilliseconds,
            spoolDirectory);
        await EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        var result = await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ArticleIndexInsertResult(true, result.Disposition == ArticleIndexWriteDisposition.Inserted);
    }

    /// <summary>
    /// Enqueues a touch, or joins an in-flight touch for the same article, and waits until it commits.
    /// </summary>
    /// <param name="articleId">Canonical article identity.</param>
    /// <param name="accessedAtUnixMilliseconds">UTC Unix epoch milliseconds. Older values do not rewrite the row.</param>
    /// <param name="cancellationToken">Cancels the wait. A command already queued can still commit.</param>
    /// <returns>Whether the row moved, stayed, or was absent.</returns>
    internal async ValueTask<ArticleIndexTouchResult> TouchAsync(
        ArticleId articleId,
        long accessedAtUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        EnsureTimestamp(accessedAtUnixMilliseconds);
        var request = new ArticleIndexRequest(ArticleIndexCommandKind.Touch, articleId, accessedAtUnixMilliseconds, spoolDirectory: null);
        if (!_coalescer.RequiresEnqueue(articleId, accessedAtUnixMilliseconds, request.Completion))
        {
            var joined = await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return MapTouch(joined.Disposition);
        }

        try
        {
            await EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _coalescer.FailToken(articleId, ex);
            request.Completion.TrySetException(ex);
            throw;
        }

        var result = await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return MapTouch(result.Disposition);
    }

    /// <summary>
    /// Reads queued commands until the channel is completed or the token is cancelled, then drains.
    /// Cancellation of this token still commits commands already in the queue.
    /// A failed batch fails every command still queued so callers do not wait forever.
    /// </summary>
    /// <param name="cancellationToken">Unblocks the wait. It does not abort a transaction that has started.</param>
    /// <returns>A task that finishes after the final batch.</returns>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var batch = new List<ArticleIndexRequest>(_settings.BatchSize);
        Exception? failure = null;
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await ReadMoreAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (ShouldFlush(batch, more))
                {
                    await FlushAsync(batch).ConfigureAwait(false);
                    batch.Clear();
                    MaybeSummarize();
                }

                if (!more && _channel.Reader.Completion.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex;
        }

        if (failure is null)
        {
            while (_channel.Reader.TryRead(out var pending))
            {
                batch.Add(pending);
            }

            if (batch.Count > 0)
            {
                try
                {
                    await FlushAsync(batch).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ex;
                }
            }
        }

        if (failure is not null)
        {
            _channel.Writer.TryComplete(failure);
            FailRequests(batch, failure);
            while (_channel.Reader.TryRead(out var pending))
            {
                pending.Completion.TrySetException(failure);
            }

            _coalescer.FailOutstanding(failure);
            throw failure;
        }

        _coalescer.FailOutstanding(new InvalidOperationException("Article index stopped before a coalesced touch was committed."));
        ArticleIndexLogMessages.Stopped(_logger, _counters.Batches);
    }

    /// <summary>Rejects a negative timestamp before it is queued.</summary>
    /// <param name="unixMilliseconds">UTC Unix epoch milliseconds.</param>
    private static void EnsureTimestamp(long unixMilliseconds)
    {
        if (unixMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unixMilliseconds), "Article index timestamps must be zero or positive Unix milliseconds.");
        }
    }

    /// <summary>Maps a committed touch disposition.</summary>
    /// <param name="disposition">Writer disposition.</param>
    /// <returns>The touch result.</returns>
    private static ArticleIndexTouchResult MapTouch(ArticleIndexWriteDisposition disposition)
    {
        var mapped = disposition switch
        {
            ArticleIndexWriteDisposition.Updated => ArticleIndexTouchDisposition.Updated,
            ArticleIndexWriteDisposition.Unchanged => ArticleIndexTouchDisposition.Unchanged,
            ArticleIndexWriteDisposition.Missing => ArticleIndexTouchDisposition.Missing,
            _ => throw new InvalidOperationException($"Article index touch completed as {disposition}."),
        };
        return new ArticleIndexTouchResult(true, mapped);
    }

    /// <summary>Waits for queue space, counting a full queue as backpressure.</summary>
    /// <param name="request">Command to queue.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that finishes when the command is in the queue.</returns>
    private async ValueTask EnqueueAsync(ArticleIndexRequest request, CancellationToken cancellationToken)
    {
        if (_channel.Reader.Count >= _settings.QueueCapacity)
        {
            _counters.NoteBackpressure();
        }

        try
        {
            await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            var closed = new InvalidOperationException("Article index is shutting down and did not accept the command.", ex);
            request.Completion.TrySetException(closed);
            throw closed;
        }

        _counters.NoteQueue(_channel.Reader.Count, _settings.QueueCapacity);
    }

    /// <summary>Fills <paramref name="batch"/> until it is full, the flush interval elapses, or the channel completes.</summary>
    /// <param name="batch">Current batch. Empty when the writer is idle.</param>
    /// <param name="cancellationToken">Cancels the idle or interval wait.</param>
    /// <returns><see langword="false"/> when the channel is completed and no further command was read.</returns>
    private async ValueTask<bool> ReadMoreAsync(List<ArticleIndexRequest> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            if (_settings.BeforeReadAsync is not null)
            {
                await _settings.BeforeReadAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            _batchStarted = _time.GetTimestamp();
        }
        else if (batch.Count < _settings.BatchSize)
        {
            var remaining = TimeSpan.FromMilliseconds(_settings.FlushIntervalMilliseconds) - _time.GetElapsedTime(_batchStarted);
            if (remaining <= TimeSpan.Zero)
            {
                return true;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(remaining);
            try
            {
                if (!await _channel.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return true;
            }
        }

        var read = false;
        while (batch.Count < _settings.BatchSize && _channel.Reader.TryRead(out var request))
        {
            batch.Add(request);
            read = true;
        }

        _counters.NoteQueue(_channel.Reader.Count, _settings.QueueCapacity);
        return read || !_channel.Reader.Completion.IsCompleted;
    }

    /// <summary>Decides whether the current batch should commit before reading more.</summary>
    /// <param name="batch">Commands read so far.</param>
    /// <param name="more">False when the channel has completed.</param>
    /// <returns><see langword="true"/> when the batch should commit.</returns>
    private bool ShouldFlush(List<ArticleIndexRequest> batch, bool more)
    {
        if (batch.Count == 0)
        {
            return false;
        }

        if (!more || batch.Count >= _settings.BatchSize)
        {
            return true;
        }

        return _time.GetElapsedTime(_batchStarted) >= TimeSpan.FromMilliseconds(_settings.FlushIntervalMilliseconds);
    }

    /// <summary>Commits one batch, publishes membership, and completes waiters.</summary>
    /// <param name="batch">Commands to commit. The list is not retained.</param>
    /// <returns>A task that finishes after commit or after waiters are failed.</returns>
    private async Task FlushAsync(List<ArticleIndexRequest> batch)
    {
        Build(batch);
        try
        {
            await CommitWithRetryAsync().ConfigureAwait(false);
            Publish();
            CompleteWaiters(exception: null);
            _batchesSinceCheckpoint++;
            if (_batchesSinceCheckpoint >= ArticleIndexSettings.CheckpointEveryBatches || _database.WalBytes >= ArticleIndexSettings.WalCheckpointThresholdBytes)
            {
                if (Checkpoint(truncate: false))
                {
                    _batchesSinceCheckpoint = 0;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CompleteWaiters(ex);
            throw;
        }
        finally
        {
            _mutations.Clear();
            _byId.Clear();
        }
    }

    /// <summary>Groups the batch by article identity and applies coalesced touch timestamps.</summary>
    /// <param name="batch">Commands in arrival order.</param>
    private void Build(List<ArticleIndexRequest> batch)
    {
        foreach (var request in batch)
        {
            if (!_byId.TryGetValue(request.Id, out var mutation))
            {
                mutation = new ArticleIndexMutation(request.Id);
                _byId.Add(request.Id, mutation);
                _mutations.Add(mutation);
            }

            if (request.Kind == ArticleIndexCommandKind.Insert)
            {
                if (!mutation.HasInsert)
                {
                    mutation.HasInsert = true;
                    mutation.ReceivedAt = request.UnixMilliseconds;
                    mutation.SpoolDirectory = request.SpoolDirectory;
                    if (!mutation.HasTouch || request.UnixMilliseconds > mutation.LastAccessedAt)
                    {
                        mutation.LastAccessedAt = request.UnixMilliseconds;
                    }
                }

                mutation.InsertWaiters.Add(request);
                continue;
            }

            var stamp = request.UnixMilliseconds;
            if (_coalescer.TryTake(request.Id, out var pending, out var waiters, out var count))
            {
                if (pending > stamp)
                {
                    stamp = pending;
                }

                if (count > 0 && waiters is not null)
                {
                    mutation.CoalescedTouches ??= new List<TaskCompletionSource<ArticleIndexWriteResult>>(count);
                    for (var i = 0; i < count; i++)
                    {
                        mutation.CoalescedTouches.Add(waiters[i]);
                    }
                }
            }

            mutation.HasTouch = true;
            if (stamp > mutation.LastAccessedAt)
            {
                mutation.LastAccessedAt = stamp;
            }

            mutation.TouchWaiters.Add(request);
        }
    }

    /// <summary>Retries a busy batch. Other failures fail the waiters and propagate.</summary>
    /// <returns>A task that finishes after a commit.</returns>
    private async Task CommitWithRetryAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                _database.Commit(_mutations);
                _counters.NoteBatch(_mutations.Count, Stopwatch.GetElapsedTime(started));
                return;
            }
            catch (SqliteException ex) when (IsBusy(ex) && attempt < _settings.CommitAttempts)
            {
                _counters.NoteBusy();
                _counters.NoteRetry();
                ArticleIndexLogMessages.Busy(_logger, attempt, ex.SqliteErrorCode);
                await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _counters.NoteFailure();
                ArticleIndexLogMessages.BatchFailed(_logger, ex, _mutations.Count, attempt);
                throw;
            }
        }
    }

    /// <summary>Publishes committed identities into the membership index.</summary>
    private void Publish()
    {
        foreach (var mutation in _mutations)
        {
            var present = mutation.HasInsert
                || mutation.TouchDisposition is ArticleIndexWriteDisposition.Updated or ArticleIndexWriteDisposition.Unchanged;
            if (!present || mutation.TouchDisposition == ArticleIndexWriteDisposition.Missing && !mutation.HasInsert)
            {
                continue;
            }

            try
            {
                _presence.Add(mutation.Id);
            }
            catch (Exception ex)
            {
                _counters.NoteCachePublishFailure();
                ArticleIndexLogMessages.CachePublishFailed(_logger, ex);
            }
        }
    }

    /// <summary>Completes every waiter from the last built batch.</summary>
    /// <param name="exception">Null after a commit. Otherwise the rollback failure.</param>
    private void CompleteWaiters(Exception? exception)
    {
        foreach (var mutation in _mutations)
        {
            if (exception is not null)
            {
                Fail(mutation.InsertWaiters, exception);
                Fail(mutation.TouchWaiters, exception);
                Fail(mutation.CoalescedTouches, exception);
                continue;
            }

            Note(mutation);
            var insert = new ArticleIndexWriteResult(mutation.InsertDisposition);
            foreach (var waiter in mutation.InsertWaiters)
            {
                waiter.Completion.TrySetResult(insert);
            }

            var touch = new ArticleIndexWriteResult(mutation.TouchDisposition);
            foreach (var waiter in mutation.TouchWaiters)
            {
                waiter.Completion.TrySetResult(touch);
            }

            if (mutation.CoalescedTouches is null)
            {
                continue;
            }

            foreach (var waiter in mutation.CoalescedTouches)
            {
                waiter.TrySetResult(touch);
            }
        }
    }

    /// <summary>Updates counters from a committed mutation.</summary>
    /// <param name="mutation">Committed group.</param>
    private void Note(ArticleIndexMutation mutation)
    {
        if (mutation.HasInsert)
        {
            if (mutation.InsertDisposition == ArticleIndexWriteDisposition.Inserted)
            {
                _counters.NoteInserted();
            }
            else
            {
                _counters.NoteDuplicate();
            }
        }

        if (!mutation.HasTouch)
        {
            return;
        }

        switch (mutation.TouchDisposition)
        {
            case ArticleIndexWriteDisposition.Updated:
                _counters.NoteTouchUpdated();
                break;
            case ArticleIndexWriteDisposition.Unchanged:
                _counters.NoteTouchUnchanged();
                break;
            case ArticleIndexWriteDisposition.Missing:
                _counters.NoteTouchMissing();
                break;
        }
    }

    /// <summary>Fails commands that were read but not successfully committed.</summary>
    /// <param name="requests">Commands from the current batch. Already completed commands are left unchanged.</param>
    /// <param name="exception">The failure those callers observe.</param>
    private static void FailRequests(List<ArticleIndexRequest> requests, Exception exception)
    {
        foreach (var request in requests)
        {
            request.Completion.TrySetException(exception);
        }
    }

    /// <summary>Fails queued commands.</summary>
    /// <param name="waiters">Commands whose transaction rolled back.</param>
    /// <param name="exception">The failure.</param>
    private static void Fail(List<ArticleIndexRequest>? waiters, Exception exception)
    {
        if (waiters is null)
        {
            return;
        }

        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(exception);
        }
    }

    /// <summary>Fails coalesced touch completions.</summary>
    /// <param name="waiters">Joined completions.</param>
    /// <param name="exception">The failure.</param>
    private static void Fail(List<TaskCompletionSource<ArticleIndexWriteResult>>? waiters, Exception exception)
    {
        if (waiters is null)
        {
            return;
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetException(exception);
        }
    }

    /// <summary>Runs a passive or truncating checkpoint and logs it.</summary>
    /// <param name="truncate">True on shutdown. False during the run.</param>
    /// <returns><see langword="false"/> when another spool holds the checkpoint gate and this call was skipped.</returns>
    internal bool Checkpoint(bool truncate)
    {
        var held = false;
        if (_checkpointGate is not null)
        {
            if (!truncate && !_checkpointGate.Wait(0))
            {
                return false;
            }

            if (truncate)
            {
                _checkpointGate.Wait();
            }

            held = true;
        }

        try
        {
            var started = Stopwatch.GetTimestamp();
            var result = _database.Checkpoint(truncate);
            _counters.NoteCheckpoint(result.Busy != 0, Stopwatch.GetElapsedTime(started), result.LogFrames, result.CheckpointedFrames);
            ArticleIndexLogMessages.Checkpoint(
                _logger,
                truncate ? "TRUNCATE" : "PASSIVE",
                result.Busy,
                result.LogFrames,
                result.CheckpointedFrames,
                _database.WalBytes);
            return true;
        }
        finally
        {
            if (held)
            {
                _checkpointGate!.Release();
            }
        }
    }

    /// <summary>Emits a summary on the configured interval.</summary>
    private void MaybeSummarize()
    {
        var now = _time.GetTimestamp();
        if (_lastSummary != 0 && _time.GetElapsedTime(_lastSummary, now) < TimeSpan.FromMilliseconds(_settings.SummaryIntervalMilliseconds))
        {
            return;
        }

        _lastSummary = now;
        _counters.NoteQueue(QueueDepth, QueueCapacity);
        ArticleIndexLogMessages.Summary(
            _logger,
            _counters.InsertsCommitted,
            _counters.Batches,
            _counters.QueueDepth,
            _counters.QueueCapacity,
            _counters.LastBatchSize,
            _counters.LastBatchMilliseconds,
            _counters.Failures,
            _counters.Retries,
            _database.WalBytes,
            _presence.Count);
    }

    /// <summary>Primary SQLite busy and locked codes. Extended codes still report these primary values.</summary>
    /// <param name="exception">SQLite failure.</param>
    /// <returns><see langword="true"/> when another attempt is safe.</returns>
    private static bool IsBusy(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;
}
