using System.IO.Pipelines;
using System.Net;
using VectorNNTP.NNTPD.Networking.Certificates;
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
/// <para>
/// <see cref="UpgradeToTlsAsync"/> upgrades an established plaintext connection to TLS on the same
/// TCP socket. <see cref="UpgradeToDeflateAsync"/> activates bidirectional raw DEFLATE (RFC 8054)
/// above the current byte stream (plain or TLS). The NNTP STARTTLS and COMPRESS commands are not
/// implemented by the transport; a future session layer decides when to invoke these upgrades.
/// </para>
/// </remarks>
public interface INntpConnection : IAsyncDisposable
{
    /// <summary>Gets octets received from the peer (after TLS decryption / DEFLATE inflate when applicable).</summary>
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

    /// <summary>Gets a value indicating whether bidirectional raw DEFLATE is active.</summary>
    bool IsCompressed { get; }

    /// <summary>Gets a token that is cancelled when the connection transport is shutting down.</summary>
    CancellationToken ConnectionClosed { get; }

    /// <summary>
    /// Completes the transport (stops pumps, closes the socket). Safe to call multiple times.
    /// </summary>
    /// <param name="exception">Optional exception indicating abortive completion.</param>
    Task CompleteAsync(Exception? exception = null);

    /// <summary>
    /// Performs an in-place server TLS handshake on the existing TCP socket.
    /// </summary>
    /// <param name="certificateProvider">Provider used to acquire a connection-lifetime certificate lease.</param>
    /// <param name="cancellationToken">Token that cancels the handshake (and aborts the connection on failure).</param>
    /// <remarks>
    /// <para>
    /// Preconditions: the connection must be plaintext and not DEFLATE-compressed; the caller must not
    /// hold outstanding <see cref="Input"/> reads or <see cref="Output"/> writes; all plaintext
    /// application data that belongs before TLS must already be consumed from <see cref="Input"/>;
    /// any plaintext that must be visible to the peer before TLS (for example a future STARTTLS
    /// response) must already be flushed to <see cref="Output"/>.
    /// </para>
    /// <para>
    /// On success, subsequent <see cref="Input"/>/<see cref="Output"/> traffic is TLS application data
    /// on the same socket. <see cref="ClientIdentity"/> is unchanged. On failure, the connection is
    /// completed and does not fall back to plaintext.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Already TLS, already compressed, upgrade in progress, or unconsumed plaintext remains in
    /// <see cref="Input"/> (precondition failure does not complete the connection).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The connection is closed or disposed.</exception>
    Task UpgradeToTlsAsync(
        ITlsCertificateContextProvider certificateProvider,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates bidirectional raw DEFLATE on the existing connection byte stream.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels activation (and aborts the connection on failure).</param>
    /// <remarks>
    /// <para>
    /// Layering (RFC 8054): DEFLATE sits above TLS when both are active —
    /// <c>NNTP → DEFLATE → TLS → TCP</c>. TLS must be negotiated first when both are used; this method
    /// rejects activation when DEFLATE is already active, and <see cref="UpgradeToTlsAsync"/> rejects
    /// TLS after DEFLATE.
    /// </para>
    /// <para>
    /// Preconditions: DEFLATE not already active; no outstanding <see cref="Input"/> reads /
    /// <see cref="Output"/> writes; application data that belongs before compression must already be
    /// consumed from <see cref="Input"/>; any bytes that must reach the peer uncompressed (for example
    /// a future <c>206</c> COMPRESS response) must already be flushed to <see cref="Output"/>.
    /// </para>
    /// <para>
    /// On success, subsequent pipe traffic is compressed in both directions on the same socket and
    /// pipes. <see cref="ClientIdentity"/> is unchanged. Post-quiescence failure completes the
    /// connection with no uncompressed fallback. The NNTP <c>COMPRESS</c> command is not implemented.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Already compressed, upgrade in progress, or unconsumed application data remains in
    /// <see cref="Input"/> (precondition failure does not complete the connection).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The connection is closed or disposed.</exception>
    Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default);
}
