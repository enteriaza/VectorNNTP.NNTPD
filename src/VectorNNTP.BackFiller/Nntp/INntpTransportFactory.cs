namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Opens the byte transport for one provider session. Does not speak NNTP.</summary>
public interface INntpTransportFactory
{
    /// <summary>
    /// Connects to <paramref name="provider"/> and returns an owned stream.
    /// </summary>
    /// <param name="provider">Provider endpoint. Credentials are not used here.</param>
    /// <param name="options">Connect/TLS timeouts and buffer sizes.</param>
    /// <param name="cancellationToken">Connect cancellation.</param>
    /// <returns>An owned bidirectional stream. The caller disposes it.</returns>
    Task<Stream> ConnectAsync(
        BackFillerProviderDefinition provider,
        NntpSessionOptions options,
        CancellationToken cancellationToken);
}
