using System.IO.Pipelines;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Bench-only <see cref="PipeReader"/> wrapper that counts <see cref="ReadAsync"/> and
/// <see cref="AdvanceTo"/>. Does not change production reader behaviour.
/// </summary>
internal sealed class CountingPipeReader : PipeReader
{
    private readonly PipeReader _inner;

    public CountingPipeReader(PipeReader inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public int ReadAsyncCount { get; private set; }

    public int AdvanceToCount { get; private set; }

    public int BufferInspections => ReadAsyncCount;

    public override void AdvanceTo(SequencePosition consumed)
    {
        AdvanceToCount++;
        _inner.AdvanceTo(consumed);
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        AdvanceToCount++;
        _inner.AdvanceTo(consumed, examined);
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ReadAsyncCount++;
        return _inner.ReadAsync(cancellationToken);
    }

    public override bool TryRead(out ReadResult result) => _inner.TryRead(out result);
}
