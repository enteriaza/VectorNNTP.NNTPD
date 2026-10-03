namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Exclusive ownership of one pooled NNTP session. Dispose releases or retires exactly once.
/// </summary>
internal sealed class NntpSessionLease : IAsyncDisposable
{
    /// <summary>Pool that receives the session when this lease is disposed.</summary>
    private readonly NntpSessionPool _pool;

    /// <summary>Leased session. The first <see cref="DisposeAsync"/> clears it.</summary>
    private NntpProviderSession? _session;

    /// <summary>Set by <see cref="Retire"/> and read when the session is returned.</summary>
    private bool _retire;

    /// <summary>Binds <paramref name="session"/> to <paramref name="pool"/> until dispose.</summary>
    /// <param name="pool">Pool that accepts the session on dispose.</param>
    /// <param name="session">Session exclusively owned by this lease until dispose.</param>
    internal NntpSessionLease(NntpSessionPool pool, NntpProviderSession session)
    {
        _pool = pool;
        _session = session;
    }

    /// <summary>Gets the leased session.</summary>
    /// <exception cref="ObjectDisposedException">The lease has been disposed.</exception>
    internal NntpProviderSession Session =>
        _session ?? throw new ObjectDisposedException(nameof(NntpSessionLease));

    /// <summary>Marks the session to be retired instead of returned to the idle pool.</summary>
    internal void Retire()
    {
        _retire = true;
    }

    /// <summary>
    /// Returns the leased session to its pool once. A later call does nothing.
    /// </summary>
    /// <returns>
    /// The pool return, or a completed task when this lease was already disposed.
    /// </returns>
    /// <remarks>
    /// The return requests retirement when <see cref="Retire"/> was called or
    /// <see cref="NntpProviderSession.IsReusable"/> is false. Otherwise the session is eligible
    /// to rejoin the idle pool. <see cref="_retire"/> is a plain write read by this method;
    /// callers set it before dispose on the same lease flow.
    /// </remarks>
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
