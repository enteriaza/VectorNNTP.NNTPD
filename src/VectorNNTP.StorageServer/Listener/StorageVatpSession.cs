using System.Buffers;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Listener;

/// <summary>
/// Minimal VATP server session: requires client HELLO, replies with server HELLO,
/// rejects OPEN with <see cref="VatpErrorCode.OpenRejected"/>, and ignores META/DATA.
/// </summary>
public sealed class StorageVatpSession : IAsyncDisposable
{
    private readonly IStorageVatpTransport _transport;
    private readonly IStorageArticleOpenBoundary _openBoundary;
    private readonly StorageServerListenerRuntimeOptions _listener;
    private readonly ILogger _logger;
    private uint _maxFramePayload = VatpProtocol.DefaultMaxFramePayload;
    private bool _clientHelloComplete;
    private bool _serverHelloSent;
    private int _disposed;

    /// <summary>Initializes a VATP session over an established transport.</summary>
    public StorageVatpSession(
        IStorageVatpTransport transport,
        IStorageArticleOpenBoundary openBoundary,
        StorageServerListenerRuntimeOptions listener,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(openBoundary);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _openBoundary = openBoundary;
        _listener = listener;
        _logger = logger;
    }

    /// <summary>Runs until peer close, cancellation, or fatal protocol error.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var parseBuffer = ArrayPool<byte>.Shared.Rent(
            VatpProtocol.HeaderLengthBytes + (int)VatpProtocol.DefaultMaxFramePayload);
        var buffered = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _transport
                        .ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (TimeoutException)
                {
                    return;
                }

                if (bytesRead == 0)
                {
                    return;
                }

                if (buffered + bytesRead > parseBuffer.Length
                    || buffered + bytesRead > _listener.ParserAccumulationMaxBytes)
                {
                    await WriteFailAsync(
                            VatpProtocol.ConnectionStreamId,
                            VatpErrorCode.FrameTooLarge,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                Buffer.BlockCopy(readBuffer, 0, parseBuffer, buffered, bytesRead);
                buffered += bytesRead;

                var consumedTotal = 0;
                while (true)
                {
                    var remaining = new ReadOnlySequence<byte>(parseBuffer, consumedTotal, buffered - consumedTotal);
                    var parsed = VatpFrameParser.ParseOneFrame(remaining, _maxFramePayload);
                    if (parsed.Status == VatpFrameParseStatus.Incomplete)
                    {
                        break;
                    }

                    if (parsed.Status == VatpFrameParseStatus.Invalid)
                    {
                        await WriteFailAsync(
                                VatpProtocol.ConnectionStreamId,
                                parsed.Error,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    consumedTotal += checked((int)parsed.ConsumedBytes);
                    if (parsed.Frame is not { } frame)
                    {
                        await WriteFailAsync(
                                VatpProtocol.ConnectionStreamId,
                                VatpErrorCode.InvalidFrameType,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var continueSession = await HandleFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                    if (!continueSession)
                    {
                        return;
                    }
                }

                if (consumedTotal > 0)
                {
                    var remainingBytes = buffered - consumedTotal;
                    if (remainingBytes > 0)
                    {
                        Buffer.BlockCopy(parseBuffer, consumedTotal, parseBuffer, 0, remainingBytes);
                    }

                    buffered = remainingBytes;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(parseBuffer);
            StorageVatpSessionLogMessages.ConnectionClosed(_logger);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> HandleFrameAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Header.Type)
        {
            case VatpFrameType.Hello:
                return await HandleHelloAsync(frame, cancellationToken).ConfigureAwait(false);
            case VatpFrameType.Open:
                return await HandleOpenAsync(frame, cancellationToken).ConfigureAwait(false);
            default:
                await WriteFailAsync(
                        frame.Header.StreamId == VatpProtocol.ConnectionStreamId
                            ? VatpProtocol.ConnectionStreamId
                            : frame.Header.StreamId,
                        VatpErrorCode.InvalidFrameType,
                        cancellationToken)
                    .ConfigureAwait(false);
                return false;
        }
    }

    private async Task<bool> HandleHelloAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Header.StreamId != VatpProtocol.ConnectionStreamId)
        {
            await WriteFailAsync(VatpProtocol.ConnectionStreamId, VatpErrorCode.InvalidStreamId, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (_clientHelloComplete)
        {
            await WriteFailAsync(VatpProtocol.ConnectionStreamId, VatpErrorCode.InvalidHello, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (!VatpHello.TryDecode(frame.Payload, out var hello, out var error))
        {
            await WriteFailAsync(VatpProtocol.ConnectionStreamId, error, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _maxFramePayload = Math.Min(hello.MaxFramePayload, VatpProtocol.DefaultMaxFramePayload);
        _clientHelloComplete = true;
        StorageVatpSessionLogMessages.ClientHelloAccepted(_logger, _maxFramePayload);

        if (!_serverHelloSent)
        {
            _serverHelloSent = true;
            var encoded = VatpFrameEncoder.EncodeHello(_maxFramePayload);
            await WriteEncodedAsync(encoded, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> HandleOpenAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        if (!_clientHelloComplete)
        {
            await WriteFailAsync(streamId, VatpErrorCode.InvalidStateTransition, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!VatpOpenPayload.TryDecode(frame.Payload, out var open, out var decodeError))
        {
            await WriteFailAsync(streamId, decodeError, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var result = _openBoundary.TryOpen(open.RequestId, open.ArticleId);
        if (!result.Accepted)
        {
            StorageVatpSessionLogMessages.OpenRejected(_logger, streamId, open.RequestId);
            await WriteFailAsync(streamId, VatpErrorCode.OpenRejected, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Skeleton never accepts; keep a defensive FAIL if a future boundary returns Accepted.
        await WriteFailAsync(streamId, VatpErrorCode.OpenRejected, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task WriteFailAsync(uint streamId, VatpErrorCode error, CancellationToken cancellationToken)
    {
        var encoded = VatpFrameEncoder.EncodeFail(streamId, error);
        await WriteEncodedAsync(encoded, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteEncodedAsync(VatpFrameEncoder.EncodedFrame encoded, CancellationToken cancellationToken)
    {
        if (!encoded.Header.IsEmpty)
        {
            await _transport.WriteAsync(encoded.Header, cancellationToken).ConfigureAwait(false);
        }

        if (!encoded.Payload.IsEmpty)
        {
            await _transport.WriteAsync(encoded.Payload, cancellationToken).ConfigureAwait(false);
        }
    }
}
