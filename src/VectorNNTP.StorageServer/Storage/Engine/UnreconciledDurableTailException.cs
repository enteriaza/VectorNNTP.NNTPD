namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>
/// An append extended a durable file and the ambiguous tail could not be truncated
/// back to a known valid end. The attempt reservation must stay held.
/// </summary>
internal sealed class UnreconciledDurableTailException : IOException
{
    /// <summary>
    /// Initializes the exception.
    /// </summary>
    /// <param name="message">Failure explanation.</param>
    /// <param name="innerException">The flush, truncate, or identity failure.</param>
    /// <param name="createdByThisCall">
    /// True only when this invocation created the pending payload or blocked tail.
    /// False when this invocation found an existing owner and must not claim it.
    /// </param>
    internal UnreconciledDurableTailException(string message, Exception innerException, bool createdByThisCall = false)
        : base(message, innerException)
    {
        CreatedByThisCall = createdByThisCall;
    }

    /// <summary>
    /// True when this invocation created the ambiguous tail. A mismatch against an existing
    /// pending payload is false.
    /// </summary>
    internal bool CreatedByThisCall { get; }
}
