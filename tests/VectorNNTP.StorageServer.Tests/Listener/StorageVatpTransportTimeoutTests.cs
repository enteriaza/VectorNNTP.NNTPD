using VectorNNTP.StorageServer.Listener;

namespace VectorNNTP.StorageServer.Tests.Listener;

public sealed class StorageVatpTransportTimeoutTests
{
    [Fact]
    public async Task Successive_reads_complete()
    {
        await using var transport = new StreamStorageVatpTransport(new ScriptedStream(2), TimeSpan.FromSeconds(2));
        Assert.Equal(1, await transport.ReadAsync(new byte[4], CancellationToken.None));
        Assert.Equal(1, await transport.ReadAsync(new byte[4], CancellationToken.None));
    }

    [Fact]
    public async Task Second_stalled_read_still_times_out_after_a_successful_read()
    {
        await using var transport = new StreamStorageVatpTransport(new ScriptedStream(1), TimeSpan.FromMilliseconds(80));
        Assert.Equal(1, await transport.ReadAsync(new byte[4], CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await transport.ReadAsync(new byte[4], CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_an_idle_timeout()
    {
        await using var transport = new StreamStorageVatpTransport(new ScriptedStream(0), TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transport.ReadAsync(new byte[4], cts.Token));
    }

    private sealed class ScriptedStream : Stream
    {
        private readonly int _immediateReads;
        private int _reads;

        public ScriptedStream(int immediateReads) => _immediateReads = immediateReads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ < _immediateReads)
            {
                buffer.Span[0] = 1;
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
