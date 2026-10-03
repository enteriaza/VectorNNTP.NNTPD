using System.Net.Sockets;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Production transport: TCP plus optional implicit TLS using platform certificate validation.
/// </summary>
internal sealed class TcpNntpTransportFactory : INntpTransportFactory
{
    /// <inheritdoc />
    public async Task<Stream> ConnectAsync(
        BackFillerProviderDefinition provider,
        NntpSessionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);

        var client = new TcpClient
        {
            ReceiveBufferSize = options.ReceiveBufferBytes,
            SendBufferSize = options.ReceiveBufferBytes,
        };

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(options.ConnectTimeout);
            await client.ConnectAsync(provider.Host, provider.Port, connectCts.Token).ConfigureAwait(false);
            Stream stream = client.GetStream();
            if (!provider.UseTls)
            {
                return new TcpOwnedStream(client, stream);
            }

            var ssl = await NntpTlsClient.AuthenticateAsClientAsync(
                    stream,
                    provider.Host,
                    options.ConnectTimeout,
                    options.ServerCertificateValidationCallback,
                    cancellationToken)
                .ConfigureAwait(false);
            return new TcpOwnedStream(client, ssl);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class TcpOwnedStream(TcpClient client, Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Use ReadAsync.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Use WriteAsync.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
        }
    }
}
