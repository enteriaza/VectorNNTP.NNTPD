using System.Diagnostics;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Cache;

namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>
/// Disabled-by-default timing for the existing IndexCommitted phase.
/// Armed only with <see cref="PhysicalProofProbe"/>. Does not change persistence.
/// </summary>
internal static class IndexCommittedProbe
{
    private const int BatchCapacity = 1024;

    private const int AcceptCapacity = 262144;

    private static int _enabled;
    private static ArticleSample[]? _articles;
    private static BatchSample[]? _batches;
    private static int[]? _ages;
    private static AcceptGateSample[]? _accepts;
    private static int _articleCount;
    private static int _batchCount;
    private static int _acceptCount;
    private static int _acceptOverflow;
    private static int _ageCount;
    private static int _stamp;
    private static Dictionary<ArticleId, int>? _stamps;
    private static long _hits;
    private static long _misses;
    private static long _inserts;
    private static long _idempotent;
    private static long _conflicts;
    private static long _invalid;
    private static long _oversized;
    private static long _disabled;
    private static long _indexMisses;
    private static long _createFailures;
    private static long _bytesInserted;
    private static long _bytesEvicted;
    private static long _evictions;
    private static long _currentBytes;
    private static long _peakCurrentBytes;
    private static long _maxBytes;
    private static long _maxArticleBytes;
    private static long _articlesLargerThanCapacity;
    private static long _physicalDuringArticle;
    private static long _readyTicks;
    private static long _recordArrayTicks;
    private static int _gen2AtArm;

    [ThreadStatic]
    private static bool _inArticle;

    [ThreadStatic]
    private static int _articleIndex;

    [ThreadStatic]
    private static bool _inBatch;

    [ThreadStatic]
    private static int _batchSlot;

    [ThreadStatic]
    private static int _layer;

    [ThreadStatic]
    private static bool _inAccept;

    [ThreadStatic]
    private static bool _inAcceptAppend;

    [ThreadStatic]
    private static int _acceptIndex;

    internal static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    internal static long CacheHits => Interlocked.Read(ref _hits);

    internal static long CacheMisses => Interlocked.Read(ref _misses);

    internal static void Arm(int capacity)
    {
        _articles = new ArticleSample[capacity];
        _batches = new BatchSample[BatchCapacity];
        _accepts = new AcceptGateSample[AcceptCapacity];
        _ages = new int[capacity];
        _stamps = new Dictionary<ArticleId, int>();
        _articleCount = 0;
        _batchCount = 0;
        _acceptCount = 0;
        _acceptOverflow = 0;
        _ageCount = 0;
        _stamp = 0;
        _hits = 0;
        _misses = 0;
        _inserts = 0;
        _idempotent = 0;
        _conflicts = 0;
        _invalid = 0;
        _oversized = 0;
        _disabled = 0;
        _indexMisses = 0;
        _createFailures = 0;
        _bytesInserted = 0;
        _bytesEvicted = 0;
        _evictions = 0;
        _currentBytes = 0;
        _peakCurrentBytes = 0;
        _maxBytes = 0;
        _maxArticleBytes = 0;
        _articlesLargerThanCapacity = 0;
        _physicalDuringArticle = 0;
        _readyTicks = 0;
        _recordArrayTicks = 0;
        _inArticle = false;
        _inBatch = false;
        _layer = 0;
        _articleIndex = -1;
        _batchSlot = -1;
        _inAccept = false;
        _inAcceptAppend = false;
        _acceptIndex = -1;
        _gen2AtArm = GC.CollectionCount(2);
        Volatile.Write(ref _enabled, 1);
    }

    internal static void Disarm() => Volatile.Write(ref _enabled, 0);

    internal static void BeginArticle()
    {
        _inArticle = false;
        _articleIndex = -1;
        _layer = 0;
        if (!IsEnabled || _articles is null)
        {
            return;
        }

        var index = _articleCount;
        if ((uint)index >= (uint)_articles.Length)
        {
            return;
        }

        _articleCount = index + 1;
        _articleIndex = index;
        _inArticle = true;
    }

    internal static void EndArticle(long started)
    {
        if (_inArticle && started != 0 && _articles is not null && (uint)_articleIndex < (uint)_articles.Length)
        {
            _articles[_articleIndex].ArticleTicks += Stopwatch.GetTimestamp() - started;
        }

        _inArticle = false;
        _layer = 0;
        _articleIndex = -1;
    }

    internal static long MarkArticle() => _inArticle ? Stopwatch.GetTimestamp() : 0;

    internal static long MarkEnabled() => IsEnabled ? Stopwatch.GetTimestamp() : 0;

    internal static long Allocated() =>
        _inArticle ? GC.GetAllocatedBytesForCurrentThread() : 0;

    internal static long AllocatedEnabled() =>
        IsEnabled ? GC.GetAllocatedBytesForCurrentThread() : 0;

    internal static void AddReady(long started)
    {
        if (started != 0)
        {
            _readyTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    internal static void AddRecordArray(long started)
    {
        if (started != 0)
        {
            _recordArrayTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    internal static int BeginBatch()
    {
        _inBatch = false;
        _batchSlot = -1;
        if (!IsEnabled || _batches is null)
        {
            return -1;
        }

        var slot = _batchCount;
        if ((uint)slot >= (uint)_batches.Length)
        {
            return -1;
        }

        _batchCount = slot + 1;
        _batchSlot = slot;
        _inBatch = true;
        return slot;
    }

    internal static void EndBatch(int slot, long started, long allocatedBefore)
    {
        if (slot >= 0 && started != 0 && _batches is not null && (uint)slot < (uint)_batches.Length)
        {
            _batches[slot].WallTicks += Stopwatch.GetTimestamp() - started;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            if (allocated >= allocatedBefore)
            {
                _batches[slot].AllocBytes += allocated - allocatedBefore;
            }
        }

        _inBatch = false;
        _batchSlot = -1;
    }

    internal static long MarkBatch() => _inBatch ? Stopwatch.GetTimestamp() : 0;

    internal static void AddEncode(long started, int frames)
    {
        AddBatch(started, static (ref BatchSample sample, long ticks) => sample.EncodeTicks += ticks);
        if (started != 0 && _inBatch && _batches is not null && (uint)_batchSlot < (uint)_batches.Length)
        {
            _batches[_batchSlot].Frames += frames;
        }
    }

    internal static void AddConcat(long started) =>
        AddBatch(started, static (ref BatchSample sample, long ticks) => sample.ConcatTicks += ticks);

    internal static void AddWrite(long started) =>
        AddBatch(started, static (ref BatchSample sample, long ticks) => sample.WriteTicks += ticks);

    internal static void AddFlush(long started) =>
        AddBatch(started, static (ref BatchSample sample, long ticks) => sample.FlushTicks += ticks);

    internal static void AddPendingFlush(long started) =>
        AddBatch(started, static (ref BatchSample sample, long ticks) => sample.PendingFlushTicks += ticks);

    internal static void AddIndexLookup(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.IndexLookupTicks += ticks);

    internal static void NoteIndexMiss()
    {
        if (IsEnabled)
        {
            _indexMisses++;
        }
    }

    internal static void EnterCreateProof()
    {
        if (_inArticle)
        {
            _layer = 1;
        }
    }

    internal static void EnterPutProof()
    {
        if (_inArticle)
        {
            _layer = 2;
        }
    }

    internal static void ExitProof() => _layer = 0;

    internal static long MarkLayer() => _layer == 0 ? 0 : Stopwatch.GetTimestamp();

    internal static void AddLayerXx(long started) => AddLayer(started, xx: true, message: false, blake: false);

    internal static void AddLayerMessage(long started) => AddLayer(started, xx: false, message: true, blake: false);

    internal static void AddLayerBlake(long started) => AddLayer(started, xx: false, message: false, blake: true);

    internal static void AddCreateProve(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.CreateProveTicks += ticks);

    internal static void AddCreateCopy(long started, int bytes)
    {
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.CreateCopyTicks += ticks);
        if (started != 0 && TryArticle(out var index))
        {
            _articles![index].CreateCopyBytes += bytes;
        }
    }

    internal static void AddCreateLocate(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.CreateLocateTicks += ticks);

    internal static void AddCreateWall(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.CreateWallTicks += ticks);

    internal static void AddCreateAlloc(long bytes)
    {
        if (bytes > 0 && TryArticle(out var index))
        {
            _articles![index].CreateAllocBytes += bytes;
        }
    }

    internal static void NoteCreateFailed()
    {
        if (IsEnabled)
        {
            _createFailures++;
        }
    }

    internal static void AddPutWall(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.PutWallTicks += ticks);

    internal static void AddPutAlloc(long bytes)
    {
        if (bytes > 0 && TryArticle(out var index))
        {
            _articles![index].PutAllocBytes += bytes;
        }
    }

    internal static long MarkPut() => _inArticle ? Stopwatch.GetTimestamp() : 0;

    internal static void AddPutProve(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.PutProveTicks += ticks);

    internal static void AddLock(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.LockTicks += ticks);

    internal static void AddLookup(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.LookupTicks += ticks);

    internal static void AddEvict(long started)
    {
        AddArticle(started, static (ref ArticleSample sample, long ticks) =>
        {
            sample.EvictTicks += ticks;
            sample.EvictCount++;
        });
    }

    internal static void AddClone(long started, int bytes)
    {
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.CloneTicks += ticks);
        if (started != 0 && TryArticle(out var index))
        {
            _articles![index].CloneBytes += bytes;
        }
    }

    internal static void AddDict(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.DictTicks += ticks);

    internal static void AddBytesAccounting(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.BytesAccountingTicks += ticks);

    internal static void AddLink(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.LinkTicks += ticks);

    internal static void AddAfter(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.AfterTicks += ticks);

    internal static void AddToString(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.ToStringTicks += ticks);

    internal static void AddLog(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.LogTicks += ticks);

    internal static void AddRetryLog(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.RetryLogTicks += ticks);

    internal static void AddClearWall(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.ClearWallTicks += ticks);

    internal static long MarkClear() => _inArticle ? Stopwatch.GetTimestamp() : 0;

    internal static void AddClearWait(long started, long acquired)
    {
        if (started == 0 || acquired < started || !TryArticle(out var index))
        {
            return;
        }

        ref var sample = ref _articles![index];
        sample.ClearWaitTicks += acquired - started;
        sample.ClearWaitStartAbs = started;
        sample.ClearWaitEndAbs = acquired;
    }

    internal static void AddClearDict(long started) =>
        AddArticle(started, static (ref ArticleSample sample, long ticks) => sample.ClearDictTicks += ticks);

    internal static void AddClearHold(long acquired)
    {
        if (acquired == 0 || !TryArticle(out var index))
        {
            return;
        }

        _articles![index].ClearHoldTicks += Stopwatch.GetTimestamp() - acquired;
    }

    internal static void AddClearAfter(long released)
    {
        if (released == 0 || !TryArticle(out var index))
        {
            return;
        }

        _articles![index].ClearAfterTicks += Stopwatch.GetTimestamp() - released;
    }

    internal static void BeginAccept()
    {
        _inAccept = false;
        _inAcceptAppend = false;
        _acceptIndex = -1;
        if (!IsEnabled || _accepts is null)
        {
            return;
        }

        var index = _acceptCount;
        if ((uint)index >= (uint)_accepts.Length)
        {
            _acceptOverflow++;
            return;
        }

        _acceptCount = index + 1;
        _acceptIndex = index;
        _inAccept = true;
    }

    internal static void EndAccept()
    {
        _inAccept = false;
        _inAcceptAppend = false;
        _acceptIndex = -1;
    }

    internal static long MarkAccept() => _inAccept ? Stopwatch.GetTimestamp() : 0;

    internal static void AddAcceptWait(long started) =>
        AddAccept(started, static (ref AcceptGateSample sample, long ticks) => sample.WaitTicks += ticks);

    internal static void AddAcceptHold(long started)
    {
        if (started == 0 || !_inAccept || _accepts is null || (uint)_acceptIndex >= (uint)_accepts.Length)
        {
            return;
        }

        var end = Stopwatch.GetTimestamp();
        ref var sample = ref _accepts[_acceptIndex];
        sample.HoldTicks += end - started;
        sample.HoldStartAbs = started;
        sample.HoldEndAbs = end;
    }

    internal static void AddAcceptAppend(long started) =>
        AddAccept(started, static (ref AcceptGateSample sample, long ticks) => sample.AppendTicks += ticks);

    internal static void EnterAcceptAppend() => _inAcceptAppend = _inAccept;

    internal static void ExitAcceptAppend() => _inAcceptAppend = false;

    internal static long MarkAcceptFlush() => _inAcceptAppend ? Stopwatch.GetTimestamp() : 0;

    internal static void AddAcceptFlush(long started) =>
        AddAccept(started, static (ref AcceptGateSample sample, long ticks) => sample.FlushTicks += ticks);

    internal static void NoteCapacity(long maxBytes)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (_maxBytes == 0)
        {
            _maxBytes = maxBytes;
        }
    }

    internal static void NoteArticleBytes(int artSize, long maxBytes)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (artSize > _maxArticleBytes)
        {
            _maxArticleBytes = artSize;
        }

        if (maxBytes > 0 && artSize > maxBytes)
        {
            _articlesLargerThanCapacity++;
        }
    }

    internal static void NoteOutcome(ArticleMemoryCachePutOutcome outcome, int artSize, long currentBytes)
    {
        if (!IsEnabled)
        {
            return;
        }

        switch (outcome)
        {
            case ArticleMemoryCachePutOutcome.Inserted:
                _inserts++;
                _bytesInserted += artSize;
                _currentBytes = currentBytes;
                if (currentBytes > _peakCurrentBytes)
                {
                    _peakCurrentBytes = currentBytes;
                }

                break;
            case ArticleMemoryCachePutOutcome.IdempotentNoOp:
                _idempotent++;
                break;
            case ArticleMemoryCachePutOutcome.RejectedConflict:
                _conflicts++;
                break;
            case ArticleMemoryCachePutOutcome.RejectedInvalid:
                _invalid++;
                break;
            case ArticleMemoryCachePutOutcome.RejectedOversized:
                _oversized++;
                break;
            case ArticleMemoryCachePutOutcome.RejectedDisabled:
                _disabled++;
                break;
        }
    }

    internal static void Remember(ArticleId artId)
    {
        if (!IsEnabled || _stamps is null)
        {
            return;
        }

        _stamp++;
        _stamps[artId] = _stamp;
    }

    internal static void Forget(ArticleId artId, int artSize)
    {
        if (!IsEnabled || _stamps is null)
        {
            return;
        }

        _evictions++;
        _bytesEvicted += artSize;
        if (!_stamps.Remove(artId, out var stamp) || _ages is null)
        {
            return;
        }

        var age = _stamp - stamp;
        if ((uint)_ageCount < (uint)_ages.Length)
        {
            _ages[_ageCount++] = age < 0 ? 0 : age;
        }
    }

    internal static void NoteCacheLookup(bool hit)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (hit)
        {
            Interlocked.Increment(ref _hits);
        }
        else
        {
            Interlocked.Increment(ref _misses);
        }
    }

    internal static void NotePhysicalDuringArticle()
    {
        if (_inArticle)
        {
            Interlocked.Increment(ref _physicalDuringArticle);
        }
    }

    internal static IndexCommittedReport Snapshot(long phaseTicks, long phaseArticles)
    {
        var articles = _articles ?? [];
        var count = Math.Min(_articleCount, articles.Length);
        var indexes = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            indexes.Add(i);
        }

        var batches = _batches ?? [];
        var batchCount = Math.Min(_batchCount, batches.Length);
        var batchIndexes = new List<int>(batchCount);
        for (var i = 0; i < batchCount; i++)
        {
            batchIndexes.Add(i);
        }

        var ages = _ages ?? [];
        var ageCount = Math.Min(_ageCount, ages.Length);
        Array.Sort(ages, 0, ageCount);
        var ageAtMost1 = 0;
        var ageAtMost10 = 0;
        for (var i = 0; i < ageCount; i++)
        {
            if (ages[i] <= 1)
            {
                ageAtMost1++;
            }

            if (ages[i] <= 10)
            {
                ageAtMost10++;
            }
        }

        long createRemainder = 0;
        long putRemainder = 0;
        long putWallRemainder = 0;
        long createWallRemainder = 0;
        long articleRemainder = 0;
        long afterRemainder = 0;
        long clearRemainder = 0;
        var putsThatEvict = 0;
        for (var i = 0; i < count; i++)
        {
            ref var sample = ref articles[i];
            createRemainder += NonNegative(sample.CreateProveTicks - sample.CreateXxTicks - sample.CreateMessageTicks - sample.CreateBlakeTicks);
            putRemainder += NonNegative(sample.PutProveTicks - sample.PutXxTicks - sample.PutMessageTicks - sample.PutBlakeTicks);
            createWallRemainder += NonNegative(sample.CreateWallTicks - sample.CreateProveTicks - sample.CreateCopyTicks - sample.CreateLocateTicks);
            putWallRemainder += NonNegative(
                sample.PutWallTicks
                - sample.PutProveTicks
                - sample.LockTicks
                - sample.LookupTicks
                - sample.EvictTicks
                - sample.CloneTicks
                - sample.DictTicks
                - sample.BytesAccountingTicks
                - sample.LinkTicks);
            var attributed = sample.IndexLookupTicks
                + sample.CreateWallTicks
                + sample.PutWallTicks
                + sample.AfterTicks;
            articleRemainder += NonNegative(sample.ArticleTicks - attributed);
            afterRemainder += NonNegative(
                sample.AfterTicks - sample.ToStringTicks - sample.LogTicks - sample.ClearWallTicks - sample.RetryLogTicks);
            clearRemainder += NonNegative(
                sample.ClearWallTicks - sample.ClearWaitTicks - sample.ClearHoldTicks - sample.ClearAfterTicks);
            if (sample.EvictCount > 0)
            {
                putsThatEvict++;
            }
        }

        long journalRemainder = 0;
        for (var i = 0; i < batchCount; i++)
        {
            ref var sample = ref batches[i];
            var attributed = sample.EncodeTicks + sample.ConcatTicks + sample.WriteTicks + sample.FlushTicks + sample.PendingFlushTicks;
            journalRemainder += NonNegative(sample.WallTicks - attributed);
        }

        var accepts = _accepts ?? [];
        var acceptCount = Math.Min(_acceptCount, accepts.Length);
        var acceptIndexes = new List<int>(acceptCount);
        var flushedIndexes = new List<int>();
        for (var i = 0; i < acceptCount; i++)
        {
            acceptIndexes.Add(i);
            if (accepts[i].FlushTicks > 0)
            {
                flushedIndexes.Add(i);
            }
        }

        var overlapTicks = OverlapClearWaitWithAccept(articles, count, accepts, acceptCount);
        long clearWaitSum = 0;
        for (var i = 0; i < count; i++)
        {
            clearWaitSum += articles[i].ClearWaitTicks;
        }

        var outsideTicks = NonNegative(clearWaitSum - overlapTicks);

        var phaseAccounted = _readyTicks + _recordArrayTicks;
        long articleSum = 0;
        long appendSum = 0;
        for (var i = 0; i < count; i++)
        {
            articleSum += articles[i].ArticleTicks;
        }

        for (var i = 0; i < batchCount; i++)
        {
            appendSum += batches[i].WallTicks;
        }

        phaseAccounted += articleSum + appendSum;
        var phaseRemainder = NonNegative(phaseTicks - phaseAccounted);

        var rows = new List<ComponentStats>
        {
            TickTotal("readyList", _readyTicks, batchCount),
            TickTotal("journalRecordArray", _recordArrayTicks, batchCount),
            Stats("journalEncode", batches, batchIndexes, static sample => sample.EncodeTicks),
            Stats("journalConcat", batches, batchIndexes, static sample => sample.ConcatTicks),
            Stats("journalWrite", batches, batchIndexes, static sample => sample.WriteTicks),
            Stats("journalFlush", batches, batchIndexes, static sample => sample.FlushTicks),
            Stats("journalPendingFlush", batches, batchIndexes, static sample => sample.PendingFlushTicks),
            Stats("journalAppendWall", batches, batchIndexes, static sample => sample.WallTicks),
            StatsBatchBytes("journalAppendAllocBytes", batches, batchIndexes, static sample => sample.AllocBytes),
            TickTotal("journalAppendUnattributed", journalRemainder, batchCount),
            Stats("indexLookup", articles, indexes, static sample => sample.IndexLookupTicks),
            Stats("createWall", articles, indexes, static sample => sample.CreateWallTicks),
            Stats("createProve", articles, indexes, static sample => sample.CreateProveTicks),
            Stats("createXxHash3", articles, indexes, static sample => sample.CreateXxTicks),
            Stats("createMessageId", articles, indexes, static sample => sample.CreateMessageTicks),
            Stats("createBlake3", articles, indexes, static sample => sample.CreateBlakeTicks),
            TickTotal("createProveRemainder", createRemainder, count),
            TickTotal("createWallUnattributed", createWallRemainder, count),
            Stats("createToArray", articles, indexes, static sample => sample.CreateCopyTicks),
            StatsBytes("createToArrayBytes", articles, indexes, static sample => sample.CreateCopyBytes),
            Stats("createLocate", articles, indexes, static sample => sample.CreateLocateTicks),
            StatsBytes("createAllocBytes", articles, indexes, static sample => sample.CreateAllocBytes),
            Stats("putWall", articles, indexes, static sample => sample.PutWallTicks),
            Stats("putProve", articles, indexes, static sample => sample.PutProveTicks),
            Stats("putXxHash3", articles, indexes, static sample => sample.PutXxTicks),
            Stats("putMessageId", articles, indexes, static sample => sample.PutMessageTicks),
            Stats("putBlake3", articles, indexes, static sample => sample.PutBlakeTicks),
            TickTotal("putProveRemainder", putRemainder, count),
            TickTotal("putWallUnattributed", putWallRemainder, count),
            Stats("putLockWait", articles, indexes, static sample => sample.LockTicks),
            Stats("putDictionaryLookup", articles, indexes, static sample => sample.LookupTicks),
            Stats("putEvict", articles, indexes, static sample => sample.EvictTicks),
            Stats("putCloneOwned", articles, indexes, static sample => sample.CloneTicks),
            StatsBytes("putCloneBytes", articles, indexes, static sample => sample.CloneBytes),
            Stats("putDictionaryInsert", articles, indexes, static sample => sample.DictTicks),
            Stats("putBytesAccounting", articles, indexes, static sample => sample.BytesAccountingTicks),
            Stats("putLinkInsert", articles, indexes, static sample => sample.LinkTicks),
            StatsBytes("putAllocBytes", articles, indexes, static sample => sample.PutAllocBytes),
            Stats("afterPut", articles, indexes, static sample => sample.AfterTicks),
            Stats("postToString", articles, indexes, static sample => sample.ToStringTicks),
            Stats("postLog", articles, indexes, static sample => sample.LogTicks),
            Stats("postRetryLog", articles, indexes, static sample => sample.RetryLogTicks),
            TickTotal("afterPutUnattributed", afterRemainder, count),
            Stats("clearWall", articles, indexes, static sample => sample.ClearWallTicks),
            Stats("clearGateWait", articles, indexes, static sample => sample.ClearWaitTicks),
            Stats("clearGateHold", articles, indexes, static sample => sample.ClearHoldTicks),
            Stats("clearDictionary", articles, indexes, static sample => sample.ClearDictTicks),
            Stats("clearAfterGate", articles, indexes, static sample => sample.ClearAfterTicks),
            TickTotal("clearWallUnattributed", clearRemainder, count),
            TickTotal("clearWaitOverlapAccept", overlapTicks, count),
            TickTotal("clearWaitOutsideAccept", outsideTicks, count),
            StatsAccept("acceptGateWait", accepts, acceptIndexes, static sample => sample.WaitTicks),
            StatsAccept("acceptGateHold", accepts, acceptIndexes, static sample => sample.HoldTicks),
            StatsAccept("acceptTryAppend", accepts, acceptIndexes, static sample => sample.AppendTicks),
            StatsAccept("acceptDurableFlush", accepts, acceptIndexes, static sample => sample.FlushTicks),
            StatsAccept(
                "acceptHoldOutsideAppend",
                accepts,
                acceptIndexes,
                static sample => NonNegative(sample.HoldTicks - sample.AppendTicks)),
            StatsAccept(
                "acceptAppendOutsideFlush",
                accepts,
                acceptIndexes,
                static sample => NonNegative(sample.AppendTicks - sample.FlushTicks)),
            StatsAccept("acceptFlushedHold", accepts, flushedIndexes, static sample => sample.HoldTicks),
            StatsAccept("acceptFlushedFlush", accepts, flushedIndexes, static sample => sample.FlushTicks),
            Stats("articleWall", articles, indexes, static sample => sample.ArticleTicks),
            TickTotal("articleUnattributed", articleRemainder, count),
            TickTotal("phaseUnattributed", phaseRemainder, phaseArticles == 0 ? 0 : (int)Math.Min(phaseArticles, int.MaxValue)),
        };

        return new IndexCommittedReport
        {
            ArticlesSampled = count,
            PhaseArticles = phaseArticles,
            Batches = batchCount,
            PhaseMilliseconds = Ms(phaseTicks),
            PhysicalProofsDuringArticle = _physicalDuringArticle,
            CacheHits = _hits,
            CacheMisses = _misses,
            Inserts = _inserts,
            Idempotent = _idempotent,
            Conflicts = _conflicts,
            Invalid = _invalid,
            Oversized = _oversized,
            Disabled = _disabled,
            IndexMisses = _indexMisses,
            CreateFailures = _createFailures,
            BytesInserted = _bytesInserted,
            BytesEvicted = _bytesEvicted,
            CurrentBytes = _currentBytes,
            PeakCurrentBytes = _peakCurrentBytes,
            MaxBytes = _maxBytes,
            MaxArticleBytes = _maxArticleBytes,
            ArticlesLargerThanCapacity = _articlesLargerThanCapacity,
            Evictions = _evictions,
            PutsThatEvict = putsThatEvict,
            EvictionAgeCount = ageCount,
            EvictionAgeMin = ageCount == 0 ? 0 : ages[0],
            EvictionAgeMedian = ageCount == 0 ? 0 : Percentile(ages, ageCount, 50),
            EvictionAgeP90 = ageCount == 0 ? 0 : Percentile(ages, ageCount, 90),
            EvictionAgeMax = ageCount == 0 ? 0 : ages[ageCount - 1],
            EvictionsAgeAtMost1 = ageAtMost1,
            EvictionsAgeAtMost10 = ageAtMost10,
            AcceptGateSamples = acceptCount,
            AcceptGateOverflow = _acceptOverflow,
            AcceptFlushedSamples = flushedIndexes.Count,
            ClearWaitOverlapAcceptMilliseconds = Ms(overlapTicks),
            ClearWaitOutsideAcceptMilliseconds = Ms(outsideTicks),
            Gen2AtArm = _gen2AtArm,
            Gen2AtSnapshot = GC.CollectionCount(2),
            Components = rows,
            Order = "AppendIndexCommittedBatch (encode, write, Flush) completes before per-article cache population",
        };
    }

    private static void AddLayer(long started, bool xx, bool message, bool blake)
    {
        if (started == 0 || !TryArticle(out var index))
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - started;
        ref var sample = ref _articles![index];
        if (_layer == 1)
        {
            if (xx)
            {
                sample.CreateXxTicks += ticks;
            }
            else if (message)
            {
                sample.CreateMessageTicks += ticks;
            }
            else if (blake)
            {
                sample.CreateBlakeTicks += ticks;
            }
        }
        else if (_layer == 2)
        {
            if (xx)
            {
                sample.PutXxTicks += ticks;
            }
            else if (message)
            {
                sample.PutMessageTicks += ticks;
            }
            else if (blake)
            {
                sample.PutBlakeTicks += ticks;
            }
        }
    }

    private delegate void ArticleAdd(ref ArticleSample sample, long ticks);

    private delegate void BatchAdd(ref BatchSample sample, long ticks);

    private static void AddArticle(long started, ArticleAdd add)
    {
        if (started == 0 || !TryArticle(out var index))
        {
            return;
        }

        add(ref _articles![index], Stopwatch.GetTimestamp() - started);
    }

    private delegate void AcceptAdd(ref AcceptGateSample sample, long ticks);

    private static void AddAccept(long started, AcceptAdd add)
    {
        if (started == 0 || !_inAccept || _accepts is null || (uint)_acceptIndex >= (uint)_accepts.Length)
        {
            return;
        }

        add(ref _accepts[_acceptIndex], Stopwatch.GetTimestamp() - started);
    }

    private static void AddBatch(long started, BatchAdd add)
    {
        if (started == 0 || !_inBatch || _batches is null || (uint)_batchSlot >= (uint)_batches.Length)
        {
            return;
        }

        add(ref _batches[_batchSlot], Stopwatch.GetTimestamp() - started);
    }

    private static bool TryArticle(out int index)
    {
        index = _articleIndex;
        return _inArticle && _articles is not null && (uint)index < (uint)_articles.Length;
    }

    private static long NonNegative(long value) => value < 0 ? 0 : value;

    private static ComponentStats TickTotal(string name, long ticks, int count)
    {
        if (count <= 0 || ticks == 0)
        {
            return new ComponentStats(name, count, Ms(ticks), 0, count <= 0 ? 0 : Ms(ticks) / count, 0, 0, 0, "ms");
        }

        var total = Ms(ticks);
        return new ComponentStats(name, count, total, 0, total / count, 0, 0, 0, "ms");
    }

    private static ComponentStats Stats(
        string name,
        ArticleSample[] samples,
        List<int> indexes,
        Func<ArticleSample, long> select) =>
        StatsTicks(name, indexes.Count, indexes, i => select(samples[i]));

    private static ComponentStats Stats(
        string name,
        BatchSample[] samples,
        List<int> indexes,
        Func<BatchSample, long> select) =>
        StatsTicks(name, indexes.Count, indexes, i => select(samples[i]));

    private static ComponentStats StatsTicks(string name, int count, List<int> indexes, Func<int, long> select)
    {
        if (count == 0)
        {
            return new ComponentStats(name, 0, 0, 0, 0, 0, 0, 0, "ms");
        }

        var ticks = new long[count];
        long sum = 0;
        for (var i = 0; i < count; i++)
        {
            var value = select(indexes[i]);
            ticks[i] = value;
            sum += value;
        }

        Array.Sort(ticks);
        return new ComponentStats(
            name,
            count,
            Ms(sum),
            Ms(Percentile(ticks, 50)),
            Ms(sum) / count,
            Ms(Percentile(ticks, 90)),
            Ms(Percentile(ticks, 99)),
            Ms(ticks[^1]),
            "ms");
    }

    private static ComponentStats StatsBatchBytes(
        string name,
        BatchSample[] samples,
        List<int> indexes,
        Func<BatchSample, long> select)
    {
        var count = indexes.Count;
        if (count == 0)
        {
            return new ComponentStats(name, 0, 0, 0, 0, 0, 0, 0, "bytes");
        }

        var values = new long[count];
        long sum = 0;
        for (var i = 0; i < count; i++)
        {
            var value = select(samples[indexes[i]]);
            values[i] = value;
            sum += value;
        }

        Array.Sort(values);
        return new ComponentStats(
            name,
            count,
            sum,
            Percentile(values, 50),
            sum / (double)count,
            Percentile(values, 90),
            Percentile(values, 99),
            values[^1],
            "bytes");
    }

    private static ComponentStats StatsAccept(
        string name,
        AcceptGateSample[] samples,
        List<int> indexes,
        Func<AcceptGateSample, long> select) =>
        StatsTicks(name, indexes.Count, indexes, i => select(samples[i]));

    private static ComponentStats StatsBytes(
        string name,
        ArticleSample[] samples,
        List<int> indexes,
        Func<ArticleSample, long> select)
    {
        var count = indexes.Count;
        if (count == 0)
        {
            return new ComponentStats(name, 0, 0, 0, 0, 0, 0, 0, "bytes");
        }

        var values = new long[count];
        long sum = 0;
        for (var i = 0; i < count; i++)
        {
            var value = select(samples[indexes[i]]);
            values[i] = value;
            sum += value;
        }

        Array.Sort(values);
        return new ComponentStats(
            name,
            count,
            sum,
            Percentile(values, 50),
            sum / (double)count,
            Percentile(values, 90),
            Percentile(values, 99),
            values[^1],
            "bytes");
    }

    private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static long Percentile(long[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = (percentile / 100d) * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = rank - lower;
        return (long)Math.Round((sorted[lower] * (1 - weight)) + (sorted[upper] * weight));
    }

    private static int Percentile(int[] sorted, int count, double percentile)
    {
        if (count <= 0)
        {
            return 0;
        }

        var rank = (percentile / 100d) * (count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = rank - lower;
        return (int)Math.Round((sorted[lower] * (1 - weight)) + (sorted[upper] * weight));
    }

    private struct ArticleSample
    {
        public long IndexLookupTicks;
        public long CreateWallTicks;
        public long CreateProveTicks;
        public long CreateXxTicks;
        public long CreateMessageTicks;
        public long CreateBlakeTicks;
        public long CreateCopyTicks;
        public long CreateCopyBytes;
        public long CreateLocateTicks;
        public long CreateAllocBytes;
        public long PutWallTicks;
        public long PutProveTicks;
        public long PutXxTicks;
        public long PutMessageTicks;
        public long PutBlakeTicks;
        public long LockTicks;
        public long LookupTicks;
        public long EvictTicks;
        public int EvictCount;
        public long CloneTicks;
        public long CloneBytes;
        public long DictTicks;
        public long BytesAccountingTicks;
        public long LinkTicks;
        public long PutAllocBytes;
        public long AfterTicks;
        public long ToStringTicks;
        public long LogTicks;
        public long RetryLogTicks;
        public long ClearWallTicks;
        public long ClearWaitTicks;
        public long ClearHoldTicks;
        public long ClearDictTicks;
        public long ClearAfterTicks;
        public long ClearWaitStartAbs;
        public long ClearWaitEndAbs;
        public long ArticleTicks;
    }

    private struct AcceptGateSample
    {
        public long WaitTicks;
        public long HoldTicks;
        public long AppendTicks;
        public long FlushTicks;
        public long HoldStartAbs;
        public long HoldEndAbs;
    }

    private readonly struct GateInterval(long start, long end)
    {
        public long Start { get; } = start;

        public long End { get; } = end;
    }

    private static long OverlapClearWaitWithAccept(
        ArticleSample[] articles,
        int articleCount,
        AcceptGateSample[] accepts,
        int acceptCount)
    {
        if (articleCount == 0 || acceptCount == 0)
        {
            return 0;
        }

        var intervals = new GateInterval[acceptCount];
        var intervalCount = 0;
        for (var i = 0; i < acceptCount; i++)
        {
            if (accepts[i].HoldEndAbs > accepts[i].HoldStartAbs)
            {
                intervals[intervalCount++] = new GateInterval(accepts[i].HoldStartAbs, accepts[i].HoldEndAbs);
            }
        }

        if (intervalCount == 0)
        {
            return 0;
        }

        Array.Sort(intervals, 0, intervalCount, Comparer<GateInterval>.Create(static (left, right) => left.Start.CompareTo(right.Start)));
        long overlap = 0;
        for (var article = 0; article < articleCount; article++)
        {
            var start = articles[article].ClearWaitStartAbs;
            var end = articles[article].ClearWaitEndAbs;
            if (end <= start)
            {
                continue;
            }

            var lo = 0;
            var hi = intervalCount;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (intervals[mid].End <= start)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (var i = lo; i < intervalCount && intervals[i].Start < end; i++)
            {
                var from = Math.Max(start, intervals[i].Start);
                var to = Math.Min(end, intervals[i].End);
                if (to > from)
                {
                    overlap += to - from;
                }
            }
        }

        return overlap;
    }

    private struct BatchSample
    {
        public long EncodeTicks;
        public long ConcatTicks;
        public long WriteTicks;
        public long FlushTicks;
        public long PendingFlushTicks;
        public long WallTicks;
        public long AllocBytes;
        public int Frames;
    }
}

/// <summary>IndexCommitted phase breakdown from one armed run.</summary>
internal sealed class IndexCommittedReport
{
    public int ArticlesSampled { get; init; }

    public long PhaseArticles { get; init; }

    public int Batches { get; init; }

    public double PhaseMilliseconds { get; init; }

    public long PhysicalProofsDuringArticle { get; init; }

    public long CacheHits { get; init; }

    public long CacheMisses { get; init; }

    public long Inserts { get; init; }

    public long Idempotent { get; init; }

    public long Conflicts { get; init; }

    public long Invalid { get; init; }

    public long Oversized { get; init; }

    public long Disabled { get; init; }

    public long IndexMisses { get; init; }

    public long CreateFailures { get; init; }

    public long BytesInserted { get; init; }

    public long BytesEvicted { get; init; }

    public long CurrentBytes { get; init; }

    public long PeakCurrentBytes { get; init; }

    public long MaxBytes { get; init; }

    public long MaxArticleBytes { get; init; }

    public long ArticlesLargerThanCapacity { get; init; }

    public long Evictions { get; init; }

    public int PutsThatEvict { get; init; }

    public int EvictionAgeCount { get; init; }

    public int EvictionAgeMin { get; init; }

    public int EvictionAgeMedian { get; init; }

    public int EvictionAgeP90 { get; init; }

    public int EvictionAgeMax { get; init; }

    public int EvictionsAgeAtMost1 { get; init; }

    public int EvictionsAgeAtMost10 { get; init; }

    public int AcceptGateSamples { get; init; }

    public int AcceptGateOverflow { get; init; }

    public int AcceptFlushedSamples { get; init; }

    public double ClearWaitOverlapAcceptMilliseconds { get; init; }

    public double ClearWaitOutsideAcceptMilliseconds { get; init; }

    public int Gen2AtArm { get; init; }

    public int Gen2AtSnapshot { get; init; }

    public int Gen2Delta => Gen2AtSnapshot - Gen2AtArm;

    public string Order { get; init; } = "";

    public IReadOnlyList<ComponentStats> Components { get; init; } = [];
}
