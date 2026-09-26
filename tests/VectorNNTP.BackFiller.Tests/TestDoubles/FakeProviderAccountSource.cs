using VectorNNTP.BackFiller.Accounts;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

internal sealed class FakeProviderAccountSource : IProviderAccountSource
{
    private readonly object _gate = new();
    private IReadOnlyList<ProviderAccountRow> _rows = [];
    private int _queryCount;

    public Exception? QueryException { get; set; }

    public TaskCompletionSource? Block { get; set; }

    public int BlockAfterQueryCount { get; set; } = 1;

    public int QueryCount => Volatile.Read(ref _queryCount);

    public IReadOnlyList<ProviderAccountRow> Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _rows = value;
            }
        }
    }

    public async Task<IReadOnlyList<ProviderAccountRow>> QueryAsync(CancellationToken cancellationToken)
    {
        var queryCount = Interlocked.Increment(ref _queryCount);
        if (Block is not null && queryCount >= BlockAfterQueryCount)
        {
            await Block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (QueryException is not null)
        {
            throw QueryException;
        }

        return Rows;
    }
}
