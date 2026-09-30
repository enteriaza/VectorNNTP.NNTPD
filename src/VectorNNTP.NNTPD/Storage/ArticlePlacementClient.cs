using System.Buffers;
using System.Net.Security;
using System.Net.Sockets;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.NNTPD.Transport.Vatp;

namespace VectorNNTP.NNTPD.Storage;

/// <summary>
/// Dedicated STORE client. Does not acquire connections from <see cref="VatpConnectionPool"/>.
/// </summary>
public sealed class ArticlePlacementClient : IArticlePlacementClient
{
    private readonly ILogger<ArticlePlacementClient> _logger;
    private readonly VatpClientOptions _options;
    private readonly ArticleTransferLimits _limits;

    /// <summary>Optional certificate callback for tests only.</summary>
    internal RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }

    /// <summary>Optional TCP connect host override for tests. TLS TargetHost remains the advertised FQDN.</summary>
    internal string? TestTcpConnectHost { get; set; }

    /// <summary>Initializes a placement client with the existing VATP timeout defaults.</summary>
    public ArticlePlacementClient(ILogger<ArticlePlacementClient> logger)
        : this(logger, new VatpClientOptions(), ArticleTransferLimits.Default)
    {
    }

    internal ArticlePlacementClient(
        ILogger<ArticlePlacementClient> logger,
        VatpClientOptions options,
        ArticleTransferLimits limits)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);
        _logger = logger;
        _options = options;
        _limits = limits;
    }

    /// <inheritdoc />
    public async ValueTask<ArticlePlacementResult> PlaceAsync(
        ArticleRecord record,
        StorageServerFleetEntry target,
        CancellationToken cancellationToken)
    {
        if (target.VatpPort is not int port || port is < 1 or > 65535)
        {
            return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "vatp-port");
        }

        if (!ArticlePlacementMeta.TryFromRecord(in record, out var meta))
        {
            return new ArticlePlacementResult(ArticlePlacementKind.RejectedInvalid, "meta");
        }

        TcpClient? tcp = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            tcp = new TcpClient();
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(_options.ConnectTimeout);
                await tcp.ConnectAsync(TestTcpConnectHost ?? target.Fqdn, port, connectCts.Token)
                    .ConfigureAwait(false);
            }

            var ssl = await VatpTlsClient.AuthenticateAsClientAsync(
                    tcp.GetStream(),
                    target.Fqdn,
                    _options.TlsHandshakeTimeout,
                    ServerCertificateValidationCallback,
                    cancellationToken)
                .ConfigureAwait(false);
            await using (ssl)
            {
                return await VatpPlacementTransfer.PlaceAsync(
                        ssl,
                        record,
                        meta,
                        _limits,
                        _options.IoTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "timeout");
        }
        catch (OperationCanceledException)
        {
            return new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
        }
        catch (Exception ex)
        {
            _ = _logger;
            return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, ex.GetType().Name);
        }
        finally
        {
            tcp?.Dispose();
        }
    }
}

/// <summary>STORE/META/DATA/END sender over an already connected stream.</summary>
internal static class VatpPlacementTransfer
{
    public static async Task<ArticlePlacementResult> PlaceAsync(
        Stream stream,
        ArticleRecord record,
        ArticleCanonicalTransferMeta meta,
        ArticleTransferLimits limits,
        TimeSpan ioTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new FrameReader(stream, ioTimeout);
        var endWritten = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var maxFrame = limits.DefaultMaxFramePayload;
            await WriteAsync(stream, VatpFrameEncoder.EncodeHello(maxFrame), cancellationToken).ConfigureAwait(false);
            var hello = await reader.ReadAsync(maxFrame, cancellationToken).ConfigureAwait(false);
            if (hello is null)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "hello-closed");
            }

            if (hello.Value.Header.Type != VatpFrameType.Hello
                || !VatpHello.TryDecode(hello.Value.Payload, out var decodedHello, out _))
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "hello");
            }

            maxFrame = Math.Min(decodedHello.MaxFramePayload, VatpProtocol.DefaultMaxFramePayload);
            const uint streamId = 1;
            var id = new byte[ArticleId.Length];
            record.ArtId.CopyTo(id);
            await WriteAsync(stream, VatpFrameEncoder.EncodeStore(streamId, id), cancellationToken).ConfigureAwait(false);
            var metaBytes = VatpMetaCodec.Encode(in meta);
            await WriteAsync(stream, VatpFrameEncoder.EncodeMeta(streamId, metaBytes), cancellationToken).ConfigureAwait(false);

            var window = new ArticleTransferWindow(limits.InitialStreamWindowBytes, limits.MaxStreamCreditBytes);
            var offset = 0;
            var artData = record.ArtData;
            while (offset < record.ArtSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = ArticleTransferReadyRing.ComputeDataPayloadLength(
                    record.ArtSize - offset,
                    window.Credit,
                    maxFrame);
                if (length <= 0)
                {
                    var paced = await ReadControlAsync(reader, maxFrame, endWritten: false, cancellationToken)
                        .ConfigureAwait(false);
                    if (paced.Terminal is { } terminal)
                    {
                        return terminal;
                    }

                    if (paced.AddedCredit > 0)
                    {
                        _ = window.Add(paced.AddedCredit);
                    }

                    continue;
                }

                if (!window.TryConsume(length))
                {
                    return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "window");
                }

                var fin = offset + length == record.ArtSize;
                await WriteAsync(
                        stream,
                        VatpFrameEncoder.EncodeData(streamId, artData.Slice(offset, length), fin),
                        cancellationToken)
                    .ConfigureAwait(false);
                offset += length;
            }

            await WriteAsync(stream, VatpFrameEncoder.EncodeEnd(streamId), cancellationToken).ConfigureAwait(false);
            endWritten = true;
            while (true)
            {
                var final = await ReadControlAsync(reader, maxFrame, endWritten: true, cancellationToken)
                    .ConfigureAwait(false);
                if (final.Terminal is { } terminal)
                {
                    return terminal;
                }

                if (final.AddedCredit > 0)
                {
                    _ = window.Add(final.AddedCredit);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!endWritten)
            {
                try
                {
                    await WriteAsync(stream, VatpFrameEncoder.EncodeCancel(1), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }

                return new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }

            return new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            return endWritten
                ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, ex.GetType().Name);
        }
    }

    private readonly record struct ControlRead(ArticlePlacementResult? Terminal, uint AddedCredit);

    private static async Task<ControlRead> ReadControlAsync(
        FrameReader reader,
        uint maxFrame,
        bool endWritten,
        CancellationToken cancellationToken)
    {
        var frame = await reader.ReadAsync(maxFrame, cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return new ControlRead(
                endWritten
                    ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                    : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "closed"),
                0);
        }

        switch (frame.Value.Header.Type)
        {
            case VatpFrameType.Window:
                return VatpControlPayload.TryDecodeWindow(Copy(frame.Value.Payload), out var add)
                    ? new ControlRead(null, add)
                    : new ControlRead(new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "window"), 0);
            case VatpFrameType.Result:
                if (!VatpResultPayload.TryDecode(frame.Value.Payload, out var outcome, out _))
                {
                    return new ControlRead(new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "result"), 0);
                }

                return new ControlRead(ArticlePlacementResult.FromOutcomeByte(outcome), 0);
            case VatpFrameType.Fail:
                if (!VatpControlPayload.TryDecodeFail(Copy(frame.Value.Payload), out var error, out _))
                {
                    return new ControlRead(
                        new ArticlePlacementResult(
                            endWritten
                                ? ArticlePlacementKind.AcknowledgementNotObserved
                                : ArticlePlacementKind.TransportFailure,
                            "fail"),
                        0);
                }

                if (error == VatpErrorCode.Cancelled)
                {
                    return new ControlRead(new ArticlePlacementResult(ArticlePlacementKind.Cancelled), 0);
                }

                return new ControlRead(new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, error.ToString()), 0);
            default:
                return new ControlRead(
                    new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, frame.Value.Header.Type.ToString()),
                    0);
        }
    }

    private static byte[] Copy(ReadOnlySequence<byte> payload)
    {
        var bytes = new byte[payload.Length];
        payload.CopyTo(bytes);
        return bytes;
    }

    private static async Task WriteAsync(
        Stream stream,
        VatpFrameEncoder.EncodedFrame frame,
        CancellationToken cancellationToken)
    {
        if (!frame.Header.IsEmpty)
        {
            await stream.WriteAsync(frame.Header, cancellationToken).ConfigureAwait(false);
        }

        if (!frame.Payload.IsEmpty)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FrameReader
    {
        private readonly Stream _stream;
        private readonly TimeSpan _ioTimeout;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private readonly List<byte> _pending = [];

        public FrameReader(Stream stream, TimeSpan ioTimeout)
        {
            _stream = stream;
            _ioTimeout = ioTimeout;
        }

        public async Task<VatpParsedFrame?> ReadAsync(uint maxFramePayload, CancellationToken cancellationToken)
        {
            while (true)
            {
                var parsed = VatpFrameParser.ParseOneFrame(new ReadOnlySequence<byte>(_pending.ToArray()), maxFramePayload);
                if (parsed.Status == VatpFrameParseStatus.Invalid)
                {
                    throw new IOException(parsed.Error.ToString());
                }

                if (parsed.Status == VatpFrameParseStatus.Success && parsed.Frame is { } frame)
                {
                    _pending.RemoveRange(0, checked((int)parsed.ConsumedBytes));
                    return CopyFrame(frame);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_ioTimeout);
                int read;
                try
                {
                    read = await _stream.ReadAsync(_buffer, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("placement-io");
                }

                if (read == 0)
                {
                    return null;
                }

                for (var i = 0; i < read; i++)
                {
                    _pending.Add(_buffer[i]);
                }
            }
        }

        private static VatpParsedFrame CopyFrame(VatpParsedFrame frame)
        {
            var payload = new byte[frame.Payload.Length];
            frame.Payload.CopyTo(payload);
            return new VatpParsedFrame(frame.Header, new ReadOnlySequence<byte>(payload));
        }
    }
}
