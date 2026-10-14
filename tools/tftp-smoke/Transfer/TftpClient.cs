using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using VectorNNTP.TftpSmoke.Protocol;
using VectorNNTP.TftpSmoke.Transport;

namespace VectorNNTP.TftpSmoke.Transfer;

/// <summary>Uploads and downloads one article at a time over TFTP. Each call binds its own transfer identifier.</summary>
public sealed class TftpClient
{
    private readonly IDatagramTransportFactory _factory;
    private readonly IPAddress _localAddress;
    private readonly TftpTiming _timing;
    private readonly TransferDiagnostics? _diagnostics;

    /// <summary>Creates a client that binds ephemeral ports on <paramref name="localAddress"/>.</summary>
    public TftpClient(IDatagramTransportFactory factory, IPAddress localAddress, TftpTiming timing, TransferDiagnostics? diagnostics = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _localAddress = localAddress ?? throw new ArgumentNullException(nameof(localAddress));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
        _diagnostics = diagnostics;
        if (_timing.AcknowledgementTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timing));
        }
    }

    /// <summary>WRQ the payload to <paramref name="server"/> under <paramref name="fileName"/>.</summary>
    public async Task<TftpTransferResult> UploadAsync(
        IPEndPoint server,
        string fileName,
        ReadOnlyMemory<byte> payload,
        int blockSize,
        int windowSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ValidateRequest(fileName, blockSize, windowSize);
        var started = Stopwatch.GetTimestamp();
        var meter = new TransferMeter();
        var transport = _factory.Bind(new IPEndPoint(_localAddress, 0));
        var exchange = new TftpExchange(transport, server, _timing, meter, remoteIsTransferId: false, _diagnostics);
        try
        {
            var request = EncodeRequest(TftpOpcode.WriteRequest, fileName, blockSize, windowSize);
            var handshake = await exchange.SendRequestAndWaitAsync(request, cancellationToken).ConfigureAwait(false);
            if (handshake.Status != TftpTransferStatus.Succeeded)
            {
                return FromHandshake(handshake, meter, started);
            }

            if (handshake.Opcode == TftpOpcode.Data)
            {
                return Failed("unexpected DATA in response to WRQ", TftpErrorCode.IllegalOperation, meter, started);
            }

            if (!TryNegotiate(blockSize, windowSize, handshake, out var agreedBlock, out var agreedWindow, out var negotiateError))
            {
                await exchange.SendTransferErrorAsync(TftpErrorCode.OptionNegotiationFailed, negotiateError, cancellationToken).ConfigureAwait(false);
                return Failed(negotiateError, TftpErrorCode.OptionNegotiationFailed, meter, started);
            }

            return await exchange.SendBlocksAsync(payload, agreedBlock, agreedWindow, started, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed("cancelled", 0, meter, started, TftpTransferStatus.Cancelled);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// RRQ <paramref name="fileName"/> and publish it at <paramref name="destinationPath"/> only after the bytes and hash are complete.
    /// The final ACK is sent after that publish. An existing destination is left unchanged.
    /// </summary>
    public async Task<TftpTransferResult> DownloadAsync(
        IPEndPoint server,
        string fileName,
        string destinationPath,
        int blockSize,
        int windowSize,
        long maxArticleBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidateRequest(fileName, blockSize, windowSize);
        if (File.Exists(destinationPath))
        {
            throw new IOException($"Destination already exists: {destinationPath}");
        }

        var started = Stopwatch.GetTimestamp();
        var meter = new TransferMeter();
        var transport = _factory.Bind(new IPEndPoint(_localAddress, 0));
        var exchange = new TftpExchange(transport, server, _timing, meter, remoteIsTransferId: false, _diagnostics);
        var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        ReceivedArticle? article = null;
        try
        {
            var request = EncodeRequest(TftpOpcode.ReadRequest, fileName, blockSize, windowSize);
            var handshake = await exchange.SendRequestAndWaitAsync(request, cancellationToken).ConfigureAwait(false);
            if (handshake.Status != TftpTransferStatus.Succeeded)
            {
                return FromHandshake(handshake, meter, started);
            }

            if (!TryNegotiate(blockSize, windowSize, handshake, out var agreedBlock, out var agreedWindow, out var negotiateError))
            {
                await exchange.SendTransferErrorAsync(TftpErrorCode.OptionNegotiationFailed, negotiateError, cancellationToken).ConfigureAwait(false);
                return Failed(negotiateError, TftpErrorCode.OptionNegotiationFailed, meter, started);
            }

            var directory = Path.GetDirectoryName(tempPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (handshake.Opcode == TftpOpcode.Acknowledgement)
            {
                return Failed("unexpected ACK in response to RRQ", TftpErrorCode.IllegalOperation, meter, started);
            }

            if (handshake.Opcode == TftpOpcode.OptionAcknowledgement)
            {
                var ack = new byte[4];
                TftpCodec.EncodeAck(ack, 0);
                exchange.ArmHandshake(ack);
                await exchange.SendArmedHandshakeAsync(cancellationToken).ConfigureAwait(false);
            }

            var received = await exchange.ReceiveBlocksAsync(
                agreedBlock,
                agreedWindow,
                maxArticleBytes,
                stream,
                hash,
                handshake.InitialData,
                handshake.Block,
                handshake.HasInitialData,
                cancellationToken).ConfigureAwait(false);
            if (!received.Completed)
            {
                return Failed(received.ErrorMessage, received.ErrorCode, meter, started, received.Status);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            var sha = hash.GetHashAndReset();
            await stream.DisposeAsync().ConfigureAwait(false);
            article = new ReceivedArticle(fileName, tempPath, destinationPath, received.PayloadBytes, sha);
            article.Publish();
            await exchange.SendAckAsync(received.FinalBlock, cancellationToken).ConfigureAwait(false);
            await exchange.LingerAsync(received.FinalBlock, cancellationToken).ConfigureAwait(false);
            return new TftpTransferResult
            {
                Status = TftpTransferStatus.Succeeded,
                Retries = meter.Retries,
                Timeouts = meter.Timeouts,
                DatagramsSent = meter.DatagramsSent,
                DatagramsReceived = meter.DatagramsReceived,
                WireBytesSent = meter.WireBytesSent,
                WireBytesReceived = meter.WireBytesReceived,
                PayloadBytes = received.PayloadBytes,
                Elapsed = Stopwatch.GetElapsedTime(started),
                Sha256 = sha,
                BlockSize = agreedBlock,
                WindowSize = agreedWindow,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed("cancelled", 0, meter, started, TftpTransferStatus.Cancelled);
        }
        finally
        {
            article?.Dispose();
            if (article is null)
            {
                TryDelete(tempPath);
            }

            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static byte[] EncodeRequest(ushort opcode, string fileName, int blockSize, int windowSize)
    {
        TftpOption[] options =
        [
            new(TftpLimits.BlockSizeOption, blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(TftpLimits.WindowSizeOption, windowSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];
        var buffer = new byte[TftpLimits.MaxRequestLength];
        var written = TftpCodec.EncodeRequest(buffer, opcode, fileName, TftpLimits.OctetMode, options);
        return buffer[..written];
    }

    private static bool TryNegotiate(
        int requestedBlock,
        int requestedWindow,
        HandshakeResult handshake,
        out int blockSize,
        out int windowSize,
        out string error)
    {
        if (handshake.Opcode == TftpOpcode.OptionAcknowledgement)
        {
            return TftpNegotiation.TryAdopt(true, requestedBlock, true, requestedWindow, handshake.Options, out blockSize, out windowSize, out error);
        }

        blockSize = TftpLimits.DefaultBlockSize;
        windowSize = TftpLimits.DefaultWindowSize;
        error = "";
        return handshake.Opcode is TftpOpcode.Acknowledgement or TftpOpcode.Data;
    }

    private static void ValidateRequest(string fileName, int blockSize, int windowSize)
    {
        if (!TftpCodec.IsSafeFileName(fileName))
        {
            throw new ArgumentException("File name must be one ASCII segment of letters, digits, '.', '_' or '-'.", nameof(fileName));
        }

        if (blockSize is < TftpLimits.MinBlockSize or > TftpLimits.MaxBlockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }

        if (windowSize is < TftpLimits.MinWindowSize or > TftpLimits.MaxWindowSize)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize));
        }
    }

    private static TftpTransferResult FromHandshake(HandshakeResult handshake, TransferMeter meter, long started)
    {
        return Failed(handshake.ErrorMessage, handshake.ErrorCode, meter, started, handshake.Status);
    }

    private static TftpTransferResult Failed(string message, ushort code, TransferMeter meter, long started, TftpTransferStatus status = TftpTransferStatus.Failed)
    {
        return new TftpTransferResult
        {
            Status = status,
            ErrorMessage = message,
            ErrorCode = code,
            Retries = meter.Retries,
            Timeouts = meter.Timeouts,
            DatagramsSent = meter.DatagramsSent,
            DatagramsReceived = meter.DatagramsReceived,
            WireBytesSent = meter.WireBytesSent,
            WireBytesReceived = meter.WireBytesReceived,
            DataDatagramsSent = meter.DataDatagramsSent,
            DataDatagramsRetransmitted = meter.DataDatagramsRetransmitted,
            AckDatagramsSent = meter.AckDatagramsSent,
            AckDatagramsReceived = meter.AckDatagramsReceived,
            HandshakeTimeouts = meter.HandshakeTimeouts,
            Elapsed = Stopwatch.GetElapsedTime(started),
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
