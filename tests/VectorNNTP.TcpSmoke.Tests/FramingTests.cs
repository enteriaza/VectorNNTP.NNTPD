using System.Security.Cryptography;
using VectorNNTP.TcpSmoke.Protocol;
using Xunit;

namespace VectorNNTP.TcpSmoke.Tests;

public sealed class FramingTests
{
    [Fact]
    public async Task ReadExact_AssemblesOneByteReads()
    {
        var payload = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        var stream = new ChunkedStream(payload, maxRead: 1);
        var buffer = new byte[payload.Length];
        Assert.True(await ArticleIo.ReadExactOrCleanEndAsync(stream, buffer, CancellationToken.None));
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task ReadExact_ReturnsFalseOnAnImmediateClose()
    {
        var stream = new ChunkedStream([], maxRead: 8);
        Assert.False(await ArticleIo.ReadExactOrCleanEndAsync(stream, new byte[56], CancellationToken.None));
    }

    [Fact]
    public async Task ReadExact_ThrowsWhenTheCloseArrivesAfterAPartialHeader()
    {
        var stream = new ChunkedStream(new byte[10], maxRead: 10);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ArticleIo.ReadExactOrCleanEndAsync(stream, new byte[56], CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ReadPayload_HashesAStreamThatArrivesInPieces()
    {
        var payload = Enumerable.Range(0, 200).Select(value => (byte)(value * 3)).ToArray();
        var stream = new ChunkedStream(payload, maxRead: 7);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16];
        var copy = new MemoryStream();
        await ArticleIo.ReadPayloadAsync(stream, (uint)payload.Length, buffer, hash, copy, CancellationToken.None);
        Assert.Equal(payload, copy.ToArray());
        Assert.Equal(SHA256.HashData(payload), hash.GetHashAndReset());
    }

    [Fact]
    public async Task ReadPayload_ThrowsWhenTheBodyIsCutShort()
    {
        var stream = new ChunkedStream(new byte[4], maxRead: 4);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ArticleIo.ReadPayloadAsync(stream, 10, new byte[8], hash, null, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SeparateWrites_AreReadAsOneHeaderAndOnePayload()
    {
        var payload = new byte[] { 9, 8, 7, 6, 5 };
        var hash = SHA256.HashData(payload);
        var header = new ArticleHeader(ArticleOperation.Put, ArticleStatus.Ok, (uint)payload.Length, 4, 5, hash);
        var stream = new MemoryStream();
        await ArticleIo.WriteHeaderAsync(stream, header, CancellationToken.None);
        await stream.WriteAsync(payload);

        stream.Position = 0;
        var headerBytes = new byte[ArticleProtocol.HeaderLength];
        Assert.True(await ArticleIo.ReadExactOrCleanEndAsync(stream, headerBytes, CancellationToken.None));
        Assert.True(ArticleHeader.TryRead(headerBytes, out var read, out _));
        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await ArticleIo.ReadPayloadAsync(stream, read.PayloadLength, new byte[2], actual, null, CancellationToken.None);
        Assert.Equal(hash, actual.GetHashAndReset());
        Assert.Equal(0, stream.Length - stream.Position);
    }

    [Fact]
    public async Task OneBuffer_StillFramesAHeaderAndItsPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var hash = SHA256.HashData(payload);
        var bytes = new byte[ArticleProtocol.HeaderLength + payload.Length];
        new ArticleHeader(ArticleOperation.Put, ArticleStatus.Ok, (uint)payload.Length, 1, 1, hash).Write(bytes);
        payload.CopyTo(bytes, ArticleProtocol.HeaderLength);
        var stream = new MemoryStream(bytes);
        var headerBytes = new byte[ArticleProtocol.HeaderLength];
        Assert.True(await ArticleIo.ReadExactOrCleanEndAsync(stream, headerBytes, CancellationToken.None));
        using var hashEngine = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await ArticleIo.ReadPayloadAsync(stream, 4, new byte[64], hashEngine, null, CancellationToken.None);
        Assert.Equal(hash, hashEngine.GetHashAndReset());
    }

    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxRead;
        private int _position;

        public ChunkedStream(byte[] data, int maxRead)
        {
            _data = data;
            _maxRead = maxRead;
        }

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

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length)
            {
                return new ValueTask<int>(0);
            }

            var count = Math.Min(_maxRead, Math.Min(buffer.Length, _data.Length - _position));
            _data.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return new ValueTask<int>(count);
        }
    }
}
