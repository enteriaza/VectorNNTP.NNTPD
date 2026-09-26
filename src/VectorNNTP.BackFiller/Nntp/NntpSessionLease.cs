namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Exclusive ownership of one pooled NNTP session. Dispose releases or retires exactly once.
/// </summary>
public sealed class NntpSessionLease : IAsyncDisposable
{
    private readonly NntpSessionPool _pool;
    private NntpProviderSession? _session;
    private bool _retire;

    internal NntpSessionLease(NntpSessionPool pool, NntpProviderSession session)
    {
        _pool = pool;
        _session = session;
    }

    /// <summary>Gets the leased session. Null after dispose.</summary>
    public NntpProviderSession Session =>
        _session ?? throw new ObjectDisposedException(nameof(NntpSessionLease));

    /// <summary>Marks the session to be retired instead of returned to the idle pool.</summary>
    public void Retire()
    {
        _retire = true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        await _pool.ReturnAsync(session, _retire || !session.IsReusable).ConfigureAwait(false);
    }
}
