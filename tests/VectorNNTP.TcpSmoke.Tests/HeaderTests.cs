using System.Buffers.Binary;
using System.Security.Cryptography;
using VectorNNTP.TcpSmoke.Benchmark;
using VectorNNTP.TcpSmoke.Payload;
using VectorNNTP.TcpSmoke.Protocol;
using Xunit;

namespace VectorNNTP.TcpSmoke.Tests;

public sealed class HeaderTests
{
    [Fact]
    public void Write_UsesNetworkByteOrder()
    {
        var hash = SHA256.HashData("article"u8.ToArray());
        var header = new ArticleHeader(ArticleOperation.Put, ArticleStatus.Ok, 0x01020304, 0x0102030405060708, 0x090A0B0C0D0E0F10, hash);
        var buffer = new byte[ArticleProtocol.HeaderLength];
        header.Write(buffer);

        Assert.Equal(ArticleProtocol.Version, buffer[0]);
        Assert.Equal((byte)ArticleOperation.Put, buffer[1]);
        Assert.Equal((ushort)ArticleStatus.Ok, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2)));
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4)));
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(8)));
        Assert.Equal(0x090A0B0C0D0E0F10UL, BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(16)));
        Assert.Equal(hash, buffer.AsSpan(24, 32).ToArray());
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 }, buffer.AsSpan(8, 8).ToArray());
    }

    [Fact]
    public void TryRead_RoundTripsEveryField()
    {
        var hash = SHA256.HashData([1, 2, 3]);
        var written = new ArticleHeader(ArticleOperation.Get, ArticleStatus.NotFound, 0, 99, 7, hash);
        var buffer = new byte[ArticleProtocol.HeaderLength];
        written.Write(buffer);

        Assert.True(ArticleHeader.TryRead(buffer, out var read, out var error));
        Assert.Equal(ArticleStatus.Ok, error);
        Assert.Equal(ArticleOperation.Get, read.Operation);
        Assert.Equal(ArticleStatus.NotFound, read.Status);
        Assert.Equal(0u, read.PayloadLength);
        Assert.Equal(99UL, read.ArticleId);
        Assert.Equal(7UL, read.TransferId);
        Assert.Equal(hash, read.Sha256);
    }

    [Fact]
    public void TryRead_RejectsAShortBuffer()
    {
        Assert.False(ArticleHeader.TryRead(new byte[55], out _, out var error));
        Assert.Equal(ArticleStatus.Incomplete, error);
    }

    [Fact]
    public void TryRead_RejectsTheWrongVersion()
    {
        var buffer = Header(ArticleOperation.Put, 0);
        buffer[0] = 9;
        Assert.False(ArticleHeader.TryRead(buffer, out _, out var error));
        Assert.Equal(ArticleStatus.BadVersion, error);
    }

    [Fact]
    public void TryRead_RejectsAnUnknownOperation()
    {
        var buffer = Header(ArticleOperation.Put, 0);
        buffer[1] = 9;
        Assert.False(ArticleHeader.TryRead(buffer, out _, out var error));
        Assert.Equal(ArticleStatus.BadOperation, error);
    }

    [Fact]
    public void TryRead_RejectsAResultThatCarriesAPayload()
    {
        var buffer = Header(ArticleOperation.Result, 1);
        Assert.False(ArticleHeader.TryRead(buffer, out _, out var error));
        Assert.Equal(ArticleStatus.BadOperation, error);
    }

    [Fact]
    public void TryRead_RejectsAGetBodyWhoseHashIsEmpty()
    {
        var buffer = Header(ArticleOperation.Get, 12);
        Assert.False(ArticleHeader.TryRead(buffer, out _, out var error));
        Assert.Equal(ArticleStatus.BadOperation, error);
    }

    [Fact]
    public void TryRead_AcceptsAZeroLengthGetRequest()
    {
        var buffer = Header(ArticleOperation.Get, 0);
        Assert.True(ArticleHeader.TryRead(buffer, out var header, out _));
        Assert.Equal(0u, header.PayloadLength);
    }

    [Fact]
    public void Payload_IsDeterministicAndNotAllZero()
    {
        var first = ArticlePayload.CreatePseudorandom(ArticlePayload.DefaultBytes, 1);
        var second = ArticlePayload.CreatePseudorandom(ArticlePayload.DefaultBytes, 1);
        Assert.Equal(first, second);
        Assert.Contains(first, value => value != 0);
        Assert.NotEqual(first, ArticlePayload.CreatePseudorandom(ArticlePayload.DefaultBytes, 2));
        Assert.Equal("28fea82d564e69f7e9376919a8e9b718bf7c9c04c9dc7911d7f4164325ac7a01", ArticlePayload.Sha256Hex(SHA256.HashData(first)));
    }

    [Fact]
    public void Percentile_UsesNearestRank()
    {
        Assert.Equal(0, BenchmarkRunner.Percentile([], 0.95));
        Assert.Equal(5, BenchmarkRunner.Percentile([1, 2, 3, 4, 5], 0.99));
        Assert.Equal(1, BenchmarkRunner.Percentile([1, 2, 3, 4, 5], 0.01));
    }

    private static byte[] Header(ArticleOperation operation, uint length)
    {
        var buffer = new byte[ArticleProtocol.HeaderLength];
        new ArticleHeader(operation, ArticleStatus.Ok, length, 1, 1, new byte[32]).Write(buffer);
        return buffer;
    }
}
