using System.Buffers.Binary;
using System.Diagnostics;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Index-frame membership used while startup rebuilds process-local capacity reservations.
/// </summary>
public sealed class IndexFrameReservationLookupTests
{
    private const long FrameBytes = ArticleIndexRecordCodec.RecordLength;

    [Fact]
    public void Many_distinct_frames_reserve_once_and_a_duplicate_does_not_add_bytes()
    {
        const int count = 40_000;
        var ledger = new ProcessLocalCapacityLedger();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++)
        {
            ledger.AddRetainedIndexFrame(Id(i), fileOffset: (long)i * FrameBytes, snapshotGeneration: 0, FrameBytes);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.Equal(count, ledger.IndexFrameCount);
        Assert.Equal(count * FrameBytes, ledger.IndexReservedBytes);
        Assert.True(ledger.HoldsIndexFrame(Id(0), fileOffset: 0, snapshotGeneration: 0));
        Assert.True(ledger.HoldsIndexFrame(Id(count - 1), fileOffset: (count - 1) * FrameBytes, snapshotGeneration: 0));
        Assert.False(ledger.HoldsIndexFrame(Id(count), fileOffset: count * FrameBytes, snapshotGeneration: 0));

        ledger.AddRetainedIndexFrame(Id(0), fileOffset: 0, snapshotGeneration: 0, FrameBytes);
        Assert.Equal(count, ledger.IndexFrameCount);
        Assert.Equal(count * FrameBytes, ledger.IndexReservedBytes);

        // Safety bound against a linear scan of every retained frame. The count assertions
        // above are the contract. Forty thousand hash inserts finish well under this limit.
        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            $"Retaining {count} index frames took {elapsed.TotalMilliseconds} ms.");
    }

    [Fact]
    public void Prefix_retirement_rebinds_frame_identity_without_double_counting()
    {
        var ledger = new ProcessLocalCapacityLedger();
        var retired = Id(1);
        var kept = Id(2);
        var snapshot = Id(3);
        ledger.AddRetainedIndexFrame(retired, fileOffset: 0, snapshotGeneration: 0, FrameBytes);
        ledger.AddRetainedIndexFrame(kept, fileOffset: FrameBytes, snapshotGeneration: 0, FrameBytes);
        ledger.AddRetainedIndexFrame(kept, fileOffset: FrameBytes, snapshotGeneration: 0, FrameBytes);

        Assert.Equal(2, ledger.IndexFrameCount);
        Assert.Equal(FrameBytes * 2, ledger.IndexReservedBytes);

        ledger.ApplyIndexPrefixRetirement(
            coveredIndexLength: FrameBytes,
            newFrameBase: ArticleIndexDeltaFile.HeaderLength,
            snapshotGeneration: 7,
            frameBytes: FrameBytes,
            snapshotArticleIds: new[] { snapshot });

        Assert.False(ledger.HoldsIndexFrame(retired, fileOffset: 0, snapshotGeneration: 0));
        Assert.True(ledger.HoldsIndexFrame(kept, fileOffset: ArticleIndexDeltaFile.HeaderLength, snapshotGeneration: 0));
        Assert.True(ledger.HoldsIndexFrame(snapshot, fileOffset: -1, snapshotGeneration: 7));
        Assert.Equal(2, ledger.IndexFrameCount);
        Assert.Equal(FrameBytes * 2, ledger.IndexReservedBytes);

        ledger.AddRetainedIndexFrame(snapshot, fileOffset: -1, snapshotGeneration: 7, FrameBytes);
        Assert.Equal(2, ledger.IndexFrameCount);
        Assert.Equal(FrameBytes * 2, ledger.IndexReservedBytes);
    }

    private static ArticleId Id(int value)
    {
        Span<byte> digest = stackalloc byte[ArticleId.Length];
        BinaryPrimitives.WriteInt32LittleEndian(digest, value);
        return ArticleId.FromSpan(digest);
    }
}
