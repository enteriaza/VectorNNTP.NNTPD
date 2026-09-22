using System.IO.Pipelines;
using System.Net;
using VectorNNTP.NNTPD.Networking.Proxy;

namespace VectorNNTP.NNTPD.Networking.Transport;

/// <summary>
/// Byte-oriented NNTP connection transport exposed to a future session/protocol layer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Input"/> carries octets received from the peer (after TLS decryption when applicable).
/// <see cref="Output"/> carries octets to send to the peer (before TLS encryption when applicable).
/// </para>
/// <para>
/// No NNTP greeting or command semantics are implemented. The transport does not keep an unused
/// connection alive beyond normal TCP/TLS lifetime and pipeline backpressure.
/// </para>
/// </remarks>
public interface INntpConnection : IAsyncDisposable
{
    /// <summary>Gets octets received from the network for the application to consume.</summary>
    PipeReader Input { get; }

    /// <summary>Gets the writer used by the application to send octets to the network.</summary>
    PipeWriter Output { get; }

    /// <summary>Gets the remote TCP peer endpoint when known.</summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>Gets the local endpoint when known.</summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>
    /// Gets the immutable effective client identity established at connection initialization.
    /// </summary>
    ConnectionClientIdentity ClientIdentity { get; }

    /// <summary>Gets a value indicating whether the connection is TLS-protected.</summary>
    bool IsTls { get; }

    /// <summary>Gets a token that is cancelled when the connection transport is shutting down.</summary>
    CancellationToken ConnectionClosed { get; }

    /// <summary>
    /// Completes the transport (stops pumps, closes the socket). Safe to call multiple times.
    /// </summary>
    /// <param name="exception">Optional exception indicating abortive completion.</param>
    Task CompleteAsync(Exception? exception = null);
}
