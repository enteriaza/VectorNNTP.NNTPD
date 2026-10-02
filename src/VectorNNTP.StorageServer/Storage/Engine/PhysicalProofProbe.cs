using System.Diagnostics;

namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>
/// Optional timings for the physical proof and the append that precedes it.
/// Inert until <see cref="Arm"/>. Does not change proof results.
/// </summary>
internal static class PhysicalProofProbe
{
    internal const byte StageSegment = 1;

    internal const byte StagePhysicalWritten = 2;

    internal const byte StagePresentBegin = 3;

    internal const byte StagePresentCommit = 4;

    internal const byte StageIndexCommitted = 5;

    private const int StageCapacity = 4096;

    private static bool _enabled;

    private static ProofSample[]? _proof;

    private static AppendSample[]? _append;

    private static byte[]? _stages;

    private static int _proofCount;

    private static int _appendCount;

    private static int _stageCount;

    private static int _published;

    private static int _orderViolations;

    private static int _proofsOutsidePresent;

    private static int _scanSkipped;

    private static int _scanPerformed;

    private static int _lastStage;

    private static string? _firstOrderViolation;

    private static long _phaseSegmentTicks;

    private static long _phasePhysicalWrittenTicks;

    private static long _phasePresentTicks;

    private static long _phaseIndexCommittedTicks;

    private static long _phaseSegmentArticles;

    private static long _phasePhysicalWrittenArticles;

    private static long _phasePresentArticles;

    private static long _phaseIndexCommittedArticles;

    [ThreadStatic]
    private static bool _inSample;

    [ThreadStatic]
    private static int _index;

    [ThreadStatic]
    private static long _sampleStart;

    [ThreadStatic]
    private static long _allocEnter;

    [ThreadStatic]
    private static ProofLayer _layer;

    [ThreadStatic]
    private static ProofLayer _savedLayer;

    [ThreadStatic]
    private static bool _inAppend;

    [ThreadStatic]
    private static int _appendIndex;

    private enum ProofLayer
    {
        None = 0,
        Inner = 1,
        Outer = 2,
    }

    internal static bool Enabled => Volatile.Read(ref _enabled);

    internal static bool IsActive => Enabled && _inSample;

    internal static void Arm(int capacity)
    {
        if (Volatile.Read(ref _enabled))
        {
            return;
        }

        _proof = new ProofSample[capacity];
        _append = new AppendSample[capacity];
        _stages = new byte[StageCapacity];
        _proofCount = 0;
        _appendCount = 0;
        _stageCount = 0;
        _published = 0;
        _orderViolations = 0;
        _proofsOutsidePresent = 0;
        _scanSkipped = 0;
        _scanPerformed = 0;
        _lastStage = 0;
        _firstOrderViolation = null;
        _phaseSegmentTicks = 0;
        _phasePhysicalWrittenTicks = 0;
        _phasePresentTicks = 0;
        _phaseIndexCommittedTicks = 0;
        _phaseSegmentArticles = 0;
        _phasePhysicalWrittenArticles = 0;
        _phasePresentArticles = 0;
        _phaseIndexCommittedArticles = 0;
        IndexCommittedProbe.Arm(capacity);
        Volatile.Write(ref _enabled, true);
    }

    internal static void Disarm()
    {
        Volatile.Write(ref _enabled, false);
        IndexCommittedProbe.Disarm();
    }

    internal static long MarkEnabled() => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static long Mark() => IsActive ? Stopwatch.GetTimestamp() : 0;

    internal static long MarkAppend() => Enabled && _inAppend ? Stopwatch.GetTimestamp() : 0;

    internal static void Begin()
    {
        if (!Enabled)
        {
            return;
        }

        if (_lastStage != StagePresentBegin)
        {
            _ = Interlocked.Increment(ref _proofsOutsidePresent);
        }

        var index = Interlocked.Increment(ref _proofCount) - 1;
        _inSample = true;
        _index = index;
        _layer = ProofLayer.None;
        _sampleStart = Stopwatch.GetTimestamp();
        _allocEnter = GC.GetAllocatedBytesForCurrentThread();
    }

    internal static void End(bool success)
    {
        if (!_inSample)
        {
            return;
        }

        var index = _index;
        var total = Stopwatch.GetTimestamp() - _sampleStart;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocEnter;
        _inSample = false;
        _layer = ProofLayer.None;
        if (_proof is null || (uint)index >= (uint)_proof.Length)
        {
            return;
        }

        ref var sample = ref _proof[index];
        sample.TotalTicks = total;
        sample.AllocBytes = allocated;
        sample.Success = success;
        sample.Completed = true;
        Volatile.Write(ref _published, index + 1);
    }

    internal static void SetOuter()
    {
        if (IsActive)
        {
            _layer = ProofLayer.Outer;
        }
    }

    internal static void ClearLayer()
    {
        if (IsActive)
        {
            _layer = ProofLayer.None;
        }
    }

    internal static void PushInner()
    {
        if (!IsActive)
        {
            return;
        }

        _savedLayer = _layer;
        _layer = ProofLayer.Inner;
    }

    internal static void PopLayer()
    {
        if (!IsActive)
        {
            return;
        }

        _layer = _savedLayer;
    }

    internal static void AddGate(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        ref var sample = ref _proof![index];
        sample.GateTicks += Stopwatch.GetTimestamp() - started;
        sample.GateCalls++;
    }

    internal static void AddRecordAlloc(long started, int bytes)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        ref var sample = ref _proof![index];
        sample.RecordAllocTicks += Stopwatch.GetTimestamp() - started;
        sample.RecordBytes += bytes;
        sample.RecordAllocCalls++;
    }

    internal static void AddRead(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        ref var sample = ref _proof![index];
        sample.ReadTicks += Stopwatch.GetTimestamp() - started;
        sample.ReadCalls++;
    }

    internal static void AddCrc(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        ref var sample = ref _proof![index];
        sample.CrcTicks += Stopwatch.GetTimestamp() - started;
        sample.CrcCalls++;
    }

    internal static void AddHeader(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        ref var sample = ref _proof![index];
        sample.HeaderTicks += Stopwatch.GetTimestamp() - started;
        sample.HeaderCalls++;
    }

    internal static void AddArtIdCompare(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        ref var sample = ref _proof![index];
        sample.ArtIdTicks += Stopwatch.GetTimestamp() - started;
        sample.ArtIdCalls++;
    }

    internal static void AddArtHashCompare(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        ref var sample = ref _proof![index];
        sample.ArtHashTicks += Stopwatch.GetTimestamp() - started;
        sample.ArtHashCalls++;
    }

    internal static void AddArtSizeCompare(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        ref var sample = ref _proof![index];
        sample.ArtSizeTicks += Stopwatch.GetTimestamp() - started;
        sample.ArtSizeCalls++;
    }

    internal static void AddCopy(long started, int bytes)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        ref var sample = ref _proof![index];
        sample.CopyTicks += Stopwatch.GetTimestamp() - started;
        sample.PayloadBytes += bytes;
        sample.CopyCalls++;
    }

    internal static void AddXx(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - started;
        ref var sample = ref _proof![index];
        if (_layer == ProofLayer.Outer)
        {
            sample.OuterXxTicks += ticks;
            sample.OuterXxCalls++;
        }
        else
        {
            sample.InnerXxTicks += ticks;
            sample.InnerXxCalls++;
        }
    }

    internal static void AddMessageId(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - started;
        ref var sample = ref _proof![index];
        if (_layer == ProofLayer.Outer)
        {
            sample.OuterMsgTicks += ticks;
            sample.OuterMsgCalls++;
        }
        else
        {
            sample.InnerMsgTicks += ticks;
            sample.InnerMsgCalls++;
        }
    }

    internal static void AddBlake(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - started;
        ref var sample = ref _proof![index];
        if (_layer == ProofLayer.Outer)
        {
            sample.OuterBlakeTicks += ticks;
            sample.OuterBlakeCalls++;
        }
        else
        {
            sample.InnerBlakeTicks += ticks;
            sample.InnerBlakeCalls++;
        }
    }

    internal static void AddOuterWall(long started)
    {
        if (started == 0 || !TryProof(out var index))
        {
            return;
        }

        ref var sample = ref _proof![index];
        sample.OuterWallTicks += Stopwatch.GetTimestamp() - started;
        sample.OuterWallCalls++;
    }

    internal static void CheckpointAllocAfterRead()
    {
        if (!TryProof(out var index))
        {
            return;
        }

        _proof![index].AllocAfterRead = GC.GetAllocatedBytesForCurrentThread() - _allocEnter;
    }

    internal static void NoteFallback()
    {
        if (!TryProof(out var index))
        {
            return;
        }

        _proof![index].Fallback = true;
    }

    internal static void BeginAppend()
    {
        if (!Enabled || _inSample)
        {
            return;
        }

        var index = Interlocked.Increment(ref _appendCount) - 1;
        _inAppend = true;
        _appendIndex = index;
    }

    internal static void EndAppend()
    {
        if (!_inAppend)
        {
            return;
        }

        var index = _appendIndex;
        _inAppend = false;
        if (_append is null || (uint)index >= (uint)_append.Length)
        {
            return;
        }

        _append[index].Completed = true;
    }

    internal static void AddAppendMessage(long started)
    {
        AddAppend(started, static (ref AppendSample sample, long ticks) => sample.MsgTicks += ticks);
    }

    internal static void AddAppendBlake(long started)
    {
        AddAppend(started, static (ref AppendSample sample, long ticks) => sample.BlakeTicks += ticks);
    }

    internal static void AddAppendXx(long started)
    {
        AddAppend(started, static (ref AppendSample sample, long ticks) => sample.XxTicks += ticks);
    }

    internal static void AddAppendProve(long started)
    {
        AddAppend(started, static (ref AppendSample sample, long ticks) => sample.ProveTicks += ticks);
    }

    internal static void AddAppendEncodeAlloc(long started, int bytes)
    {
        if (started == 0 || !TryAppend(out var index))
        {
            return;
        }

        ref var sample = ref _append![index];
        sample.EncodeAllocTicks += Stopwatch.GetTimestamp() - started;
        sample.RecordBytes += bytes;
    }

    internal static void AddAppendEncodeCopy(long started)
    {
        AddAppend(started, static (ref AppendSample sample, long ticks) => sample.EncodeCopyTicks += ticks);
    }

    internal static void AddAppendEncodeCrc(long started)
    {
        AddAppend(started, static (ref AppendSample sample, long ticks) => sample.EncodeCrcTicks += ticks);
    }

    internal static void AddAppendWrite(long started)
    {
        AddAppend(started, static (ref AppendSample sample, long ticks) => sample.WriteTicks += ticks);
    }

    internal static void NoteScanSkipped()
    {
        if (Enabled)
        {
            _ = Interlocked.Increment(ref _scanSkipped);
        }
    }

    internal static void NoteScanPerformed()
    {
        if (Enabled)
        {
            _ = Interlocked.Increment(ref _scanPerformed);
        }
    }

    internal static void NoteStage(byte stage)
    {
        if (!Enabled)
        {
            return;
        }

        var previous = _lastStage;
        if (!IsLegalTransition(previous, stage))
        {
            _ = Interlocked.Increment(ref _orderViolations);
            _firstOrderViolation ??= previous + " -> " + stage;
        }

        _lastStage = stage;
        var index = _stageCount;
        if (_stages is not null && (uint)index < (uint)_stages.Length)
        {
            _stages[index] = stage;
        }

        _stageCount = index + 1;
    }

    internal static void AddPhase(byte phase, long started, int articles)
    {
        if (started == 0)
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - started;
        switch (phase)
        {
            case StageSegment:
                _ = Interlocked.Add(ref _phaseSegmentTicks, ticks);
                _ = Interlocked.Add(ref _phaseSegmentArticles, articles);
                break;
            case StagePhysicalWritten:
                _ = Interlocked.Add(ref _phasePhysicalWrittenTicks, ticks);
                _ = Interlocked.Add(ref _phasePhysicalWrittenArticles, articles);
                break;
            case StagePresentBegin:
                _ = Interlocked.Add(ref _phasePresentTicks, ticks);
                _ = Interlocked.Add(ref _phasePresentArticles, articles);
                break;
            case StageIndexCommitted:
                _ = Interlocked.Add(ref _phaseIndexCommittedTicks, ticks);
                _ = Interlocked.Add(ref _phaseIndexCommittedArticles, articles);
                break;
        }
    }

    internal static int Published => Volatile.Read(ref _published);

    internal static PhysicalProofReport Snapshot()
    {
        _ = Volatile.Read(ref _published);
        var proof = _proof ?? [];
        var append = _append ?? [];
        var success = new List<int>();
        var fallback = 0;
        long allocSum = 0;
        long allocAfterReadSum = 0;
        long recordSum = 0;
        long payloadSum = 0;
        var recordAllocCalls = 0;
        var copyCalls = 0;
        var crcCalls = 0;
        var innerXxCalls = 0;
        var innerMsgCalls = 0;
        var innerBlakeCalls = 0;
        var outerWallCalls = 0;
        var outerXxCalls = 0;
        var outerMsgCalls = 0;
        var outerBlakeCalls = 0;
        var artIdCalls = 0;
        var artHashCalls = 0;
        var artSizeCalls = 0;
        var gateCalls = 0;
        var headerCalls = 0;
        var readCalls = 0;
        for (var i = 0; i < proof.Length; i++)
        {
            ref var sample = ref proof[i];
            if (!sample.Completed || !sample.Success)
            {
                continue;
            }

            success.Add(i);
            if (sample.Fallback)
            {
                fallback++;
            }

            allocSum += sample.AllocBytes;
            allocAfterReadSum += sample.AllocAfterRead;
            recordSum += sample.RecordBytes;
            payloadSum += sample.PayloadBytes;
            recordAllocCalls += sample.RecordAllocCalls;
            copyCalls += sample.CopyCalls;
            crcCalls += sample.CrcCalls;
            innerXxCalls += sample.InnerXxCalls;
            innerMsgCalls += sample.InnerMsgCalls;
            innerBlakeCalls += sample.InnerBlakeCalls;
            outerWallCalls += sample.OuterWallCalls;
            outerXxCalls += sample.OuterXxCalls;
            outerMsgCalls += sample.OuterMsgCalls;
            outerBlakeCalls += sample.OuterBlakeCalls;
            artIdCalls += sample.ArtIdCalls;
            artHashCalls += sample.ArtHashCalls;
            artSizeCalls += sample.ArtSizeCalls;
            gateCalls += sample.GateCalls;
            headerCalls += sample.HeaderCalls;
            readCalls += sample.ReadCalls;
        }

        var appendIndexes = new List<int>();
        long appendRecordBytes = 0;
        for (var i = 0; i < append.Length; i++)
        {
            if (append[i].Completed)
            {
                appendIndexes.Add(i);
                appendRecordBytes += append[i].RecordBytes;
            }
        }

        var proofStats = new List<ComponentStats>
        {
            Stats("gate wait", proof, success, static sample => sample.GateTicks),
            Stats("physical read", proof, success, static sample => sample.ReadTicks),
            Stats("decode/CRC", proof, success, static sample => sample.CrcTicks + sample.HeaderTicks),
            Stats("CRC32", proof, success, static sample => sample.CrcTicks),
            Stats("header decode", proof, success, static sample => sample.HeaderTicks),
            Stats("identity checks", proof, success, static sample => sample.ArtIdTicks + sample.ArtHashTicks + sample.ArtSizeTicks),
            Stats("ArticleId compare", proof, success, static sample => sample.ArtIdTicks),
            Stats("ArtHash compare", proof, success, static sample => sample.ArtHashTicks),
            Stats("ArtSize compare", proof, success, static sample => sample.ArtSizeTicks),
            Stats("XxHash3", proof, success, static sample => sample.InnerXxTicks),
            Stats("Message-ID scan", proof, success, static sample => sample.InnerMsgTicks),
            Stats("Message-ID BLAKE3", proof, success, static sample => sample.InnerBlakeTicks),
            Stats("Message-ID/integrity", proof, success, static sample => sample.InnerMsgTicks + sample.InnerBlakeTicks),
            Stats("redundant proof", proof, success, static sample => sample.OuterWallTicks),
            Stats("redundant XxHash3", proof, success, static sample => sample.OuterXxTicks),
            Stats("redundant Message-ID", proof, success, static sample => sample.OuterMsgTicks + sample.OuterBlakeTicks),
            Stats("ToArray/copy", proof, success, static sample => sample.CopyTicks),
            Stats("record buffer allocation", proof, success, static sample => sample.RecordAllocTicks),
            Stats("unattributed", proof, success, Unattributed),
            Stats("total proof", proof, success, static sample => sample.TotalTicks),
            StatsBytes("allocated bytes", proof, success, static sample => sample.AllocBytes),
            StatsBytes("bytes allocated inside read+decode", proof, success, static sample => sample.AllocAfterRead),
        };

        var appendStats = new List<ComponentStats>
        {
            Stats("append Message-ID scan", append, appendIndexes, static sample => sample.MsgTicks),
            Stats("append BLAKE3", append, appendIndexes, static sample => sample.BlakeTicks),
            Stats("append XxHash3", append, appendIndexes, static sample => sample.XxTicks),
            Stats("append TryProve", append, appendIndexes, static sample => sample.ProveTicks),
            Stats("append record allocation", append, appendIndexes, static sample => sample.EncodeAllocTicks),
            Stats("append payload copy", append, appendIndexes, static sample => sample.EncodeCopyTicks),
            Stats("append CRC32", append, appendIndexes, static sample => sample.EncodeCrcTicks),
            Stats("append FileStream.Write", append, appendIndexes, static sample => sample.WriteTicks),
        };

        var completed = 0;
        var succeeded = success.Count;
        for (var i = 0; i < proof.Length; i++)
        {
            if (proof[i].Completed)
            {
                completed++;
            }
        }

        return new PhysicalProofReport
        {
            ProofAttempts = _proofCount,
            ProofsRecorded = completed,
            ProofsSucceeded = succeeded,
            FallbackProofs = fallback,
            ProofsOutsidePresent = _proofsOutsidePresent,
            OrderViolations = _orderViolations,
            FirstOrderViolation = _firstOrderViolation,
            Order = DescribeOrder(),
            ScanSkipped = _scanSkipped,
            ScanPerformed = _scanPerformed,
            RecordAllocCalls = recordAllocCalls,
            CopyCalls = copyCalls,
            CrcCalls = crcCalls,
            ReadCalls = readCalls,
            GateCalls = gateCalls,
            HeaderCalls = headerCalls,
            ArtIdCalls = artIdCalls,
            ArtHashCalls = artHashCalls,
            ArtSizeCalls = artSizeCalls,
            InnerXxCalls = innerXxCalls,
            InnerMessageCalls = innerMsgCalls,
            InnerBlakeCalls = innerBlakeCalls,
            OuterProofCalls = outerWallCalls,
            OuterXxCalls = outerXxCalls,
            OuterMessageCalls = outerMsgCalls,
            OuterBlakeCalls = outerBlakeCalls,
            AverageAppendRecordBytes = appendIndexes.Count == 0 ? 0 : appendRecordBytes / (double)appendIndexes.Count,
            AverageRecordBytes = succeeded == 0 ? 0 : recordSum / (double)succeeded,
            AveragePayloadBytes = succeeded == 0 ? 0 : payloadSum / (double)succeeded,
            AverageAllocatedBytes = succeeded == 0 ? 0 : allocSum / (double)succeeded,
            AverageAllocatedBytesAfterRead = succeeded == 0 ? 0 : allocAfterReadSum / (double)succeeded,
            SegmentPhase = Phase(_phaseSegmentTicks, _phaseSegmentArticles),
            PhysicalWrittenPhase = Phase(_phasePhysicalWrittenTicks, _phasePhysicalWrittenArticles),
            PresentPhase = Phase(_phasePresentTicks, _phasePresentArticles),
            IndexCommittedPhase = Phase(_phaseIndexCommittedTicks, _phaseIndexCommittedArticles),
            IndexCommitted = IndexCommittedProbe.Snapshot(_phaseIndexCommittedTicks, _phaseIndexCommittedArticles),
            Proof = proofStats,
            Append = appendStats,
        };
    }

    private static long Unattributed(ProofSample sample)
    {
        var attributed = sample.GateTicks
            + sample.RecordAllocTicks
            + sample.ReadTicks
            + sample.CrcTicks
            + sample.HeaderTicks
            + sample.ArtIdTicks
            + sample.ArtHashTicks
            + sample.ArtSizeTicks
            + sample.CopyTicks
            + sample.InnerXxTicks
            + sample.InnerMsgTicks
            + sample.InnerBlakeTicks
            + sample.OuterWallTicks;
        return sample.TotalTicks - attributed;
    }

    private static bool TryProof(out int index)
    {
        index = _index;
        return _inSample && _proof is not null && (uint)index < (uint)_proof.Length;
    }

    private static bool TryAppend(out int index)
    {
        index = _appendIndex;
        return _inAppend && _append is not null && (uint)index < (uint)_append.Length;
    }

    private delegate void AppendAdd(ref AppendSample sample, long ticks);

    private static void AddAppend(long started, AppendAdd add)
    {
        if (started == 0 || !TryAppend(out var index))
        {
            return;
        }

        add(ref _append![index], Stopwatch.GetTimestamp() - started);
    }

    private static bool IsLegalTransition(int previous, byte stage) =>
        (previous, stage) switch
        {
            (0, StageSegment) => true,
            (StageSegment, StagePhysicalWritten) => true,
            (StagePhysicalWritten, StagePresentBegin) => true,
            (StagePresentBegin, StagePresentCommit) => true,
            (StagePresentCommit, StageIndexCommitted) => true,
            (StageIndexCommitted, StageSegment) => true,
            _ => false,
        };

    private static string DescribeOrder()
    {
        if (_stages is null || _stageCount == 0)
        {
            return "no stages";
        }

        var names = new[] { "?", "SegmentFlushed", "PhysicalWritten", "PresentBegin", "PresentCommit", "IndexCommitted" };
        var shown = Math.Min(_stageCount, 10);
        var first = new string[shown];
        for (var i = 0; i < shown; i++)
        {
            var stage = _stages[i];
            first[i] = stage < names.Length ? names[stage] : stage.ToString();
        }

        var counts = new int[6];
        var limit = Math.Min(_stageCount, _stages.Length);
        for (var i = 0; i < limit; i++)
        {
            var stage = _stages[i];
            if (stage < counts.Length)
            {
                counts[stage]++;
            }
        }

        return "first=" + string.Join(" → ", first)
            + "; counts segment=" + counts[StageSegment]
            + " physicalWritten=" + counts[StagePhysicalWritten]
            + " presentBegin=" + counts[StagePresentBegin]
            + " presentCommit=" + counts[StagePresentCommit]
            + " indexCommitted=" + counts[StageIndexCommitted]
            + "; violations=" + _orderViolations
            + "; proofsOutsidePresent=" + _proofsOutsidePresent;
    }

    private static PhaseStats Phase(long ticks, long articles) =>
        new(articles, Ms(ticks), articles == 0 ? 0 : Ms(ticks) / articles);

    private static ComponentStats Stats<T>(string name, T[] samples, List<int> indexes, Func<T, long> select)
    {
        var count = indexes.Count;
        if (count == 0)
        {
            return new ComponentStats(name, 0, 0, 0, 0, 0, 0, 0, "ms");
        }

        var ticks = new long[count];
        long sum = 0;
        for (var i = 0; i < count; i++)
        {
            var value = select(samples[indexes[i]]);
            ticks[i] = value;
            sum += value;
        }

        Array.Sort(ticks);
        return new ComponentStats(
            name,
            count,
            Ms(sum),
            Ms((long)Percentile(ticks, 50)),
            Ms(sum) / count,
            Ms((long)Percentile(ticks, 90)),
            Ms((long)Percentile(ticks, 99)),
            Ms(ticks[^1]),
            "ms");
    }

    private static ComponentStats StatsBytes<T>(string name, T[] samples, List<int> indexes, Func<T, long> select)
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

    private static double Percentile(long[] values, double percentile)
    {
        var rank = percentile / 100.0 * (values.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high)
        {
            return values[low];
        }

        return values[low] + ((values[high] - values[low]) * (rank - low));
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private struct ProofSample
    {
        public long TotalTicks;
        public long GateTicks;
        public int GateCalls;
        public long RecordAllocTicks;
        public int RecordAllocCalls;
        public int RecordBytes;
        public long ReadTicks;
        public int ReadCalls;
        public long CrcTicks;
        public int CrcCalls;
        public long HeaderTicks;
        public int HeaderCalls;
        public long ArtIdTicks;
        public int ArtIdCalls;
        public long ArtHashTicks;
        public int ArtHashCalls;
        public long ArtSizeTicks;
        public int ArtSizeCalls;
        public long CopyTicks;
        public int CopyCalls;
        public int PayloadBytes;
        public long InnerXxTicks;
        public int InnerXxCalls;
        public long InnerMsgTicks;
        public int InnerMsgCalls;
        public long InnerBlakeTicks;
        public int InnerBlakeCalls;
        public long OuterWallTicks;
        public int OuterWallCalls;
        public long OuterXxTicks;
        public int OuterXxCalls;
        public long OuterMsgTicks;
        public int OuterMsgCalls;
        public long OuterBlakeTicks;
        public int OuterBlakeCalls;
        public long AllocBytes;
        public long AllocAfterRead;
        public bool Success;
        public bool Completed;
        public bool Fallback;
    }

    private struct AppendSample
    {
        public long MsgTicks;
        public long BlakeTicks;
        public long XxTicks;
        public long ProveTicks;
        public long EncodeAllocTicks;
        public long EncodeCopyTicks;
        public long EncodeCrcTicks;
        public long WriteTicks;
        public int RecordBytes;
        public bool Completed;
    }
}

/// <summary>One timed component, in milliseconds.</summary>
internal readonly record struct ComponentStats(
    string Name,
    int Count,
    double Total,
    double Median,
    double Average,
    double P90,
    double P99,
    double Max,
    string Unit);

/// <summary>Aggregate worker-phase time.</summary>
internal readonly record struct PhaseStats(long Articles, double TotalMilliseconds, double AverageMilliseconds);

/// <summary>Snapshot of one armed proof measurement.</summary>
internal sealed class PhysicalProofReport
{
    public int ProofAttempts { get; init; }

    public int ProofsRecorded { get; init; }

    public int ProofsSucceeded { get; init; }

    public int FallbackProofs { get; init; }

    public int ProofsOutsidePresent { get; init; }

    public int OrderViolations { get; init; }

    public string? FirstOrderViolation { get; init; }

    public string Order { get; init; } = "";

    public int ScanSkipped { get; init; }

    public int ScanPerformed { get; init; }

    public int RecordAllocCalls { get; init; }

    public int CopyCalls { get; init; }

    public int CrcCalls { get; init; }

    public int ReadCalls { get; init; }

    public int GateCalls { get; init; }

    public int HeaderCalls { get; init; }

    public int ArtIdCalls { get; init; }

    public int ArtHashCalls { get; init; }

    public int ArtSizeCalls { get; init; }

    public int InnerXxCalls { get; init; }

    public int InnerMessageCalls { get; init; }

    public int InnerBlakeCalls { get; init; }

    public int OuterProofCalls { get; init; }

    public int OuterXxCalls { get; init; }

    public int OuterMessageCalls { get; init; }

    public int OuterBlakeCalls { get; init; }

    public double AverageAppendRecordBytes { get; init; }

    public double AverageRecordBytes { get; init; }

    public double AveragePayloadBytes { get; init; }

    public double AverageAllocatedBytes { get; init; }

    public double AverageAllocatedBytesAfterRead { get; init; }

    public PhaseStats SegmentPhase { get; init; }

    public PhaseStats PhysicalWrittenPhase { get; init; }

    public PhaseStats PresentPhase { get; init; }

    public PhaseStats IndexCommittedPhase { get; init; }

    public IndexCommittedReport? IndexCommitted { get; init; }

    public IReadOnlyList<ComponentStats> Proof { get; init; } = [];

    public IReadOnlyList<ComponentStats> Append { get; init; } = [];
}
