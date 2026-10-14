using VectorNNTP.TftpSmoke;
using VectorNNTP.TftpSmoke.Benchmark;
using VectorNNTP.TftpSmoke.Payload;
using VectorNNTP.TftpSmoke.Protocol;
using Xunit;

namespace VectorNNTP.TftpSmoke.Tests;

public sealed class CodecAndNegotiationTests
{
    [Fact]
    public void WriteRequest_RoundTripsOptions()
    {
        var options = new TftpOption[]
        {
            new(TftpLimits.BlockSizeOption, "1428"),
            new(TftpLimits.WindowSizeOption, "16"),
        };
        var buffer = new byte[256];
        var length = TftpCodec.EncodeRequest(buffer, TftpOpcode.WriteRequest, "article.bin", TftpLimits.OctetMode, options);
        Assert.True(TftpCodec.TryRead(buffer.AsSpan(0, length), out var packet, out var error), error);
        Assert.Equal(TftpOpcode.WriteRequest, packet.Opcode);
        Assert.Equal("article.bin", packet.FileName);
        Assert.Equal("octet", packet.Mode);
        Assert.Equal(options, packet.Options);
    }

    [Fact]
    public void ReadRequest_RoundTrips()
    {
        var buffer = new byte[128];
        var length = TftpCodec.EncodeRequest(buffer, TftpOpcode.ReadRequest, "a.bin", "octet", []);
        Assert.True(TftpCodec.TryRead(buffer.AsSpan(0, length), out var packet, out _));
        Assert.Equal(TftpOpcode.ReadRequest, packet.Opcode);
        Assert.Equal("a.bin", packet.FileName);
        Assert.Empty(packet.Options);
    }

    [Fact]
    public void Oack_RoundTripsAcceptedOptionsOnly()
    {
        TftpOption[] options = [new("blksize", "512"), new("windowsize", "4")];
        var buffer = new byte[128];
        var length = TftpCodec.EncodeOack(buffer, options);
        Assert.True(TftpCodec.TryRead(buffer.AsSpan(0, length), out var packet, out var error), error);
        Assert.Equal(TftpOpcode.OptionAcknowledgement, packet.Opcode);
        Assert.Equal(options, packet.Options);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(9, 512)]
    [InlineData(65535, 1428)]
    public void DataAndAck_UseNetworkOrder(int block, int size)
    {
        var payload = new byte[size];
        payload.AsSpan().Fill(0x5A);
        var buffer = new byte[4 + size];
        var length = TftpCodec.EncodeData(buffer, (ushort)block, payload);
        Assert.Equal(4 + size, length);
        Assert.Equal(0x00, buffer[0]);
        Assert.Equal(0x03, buffer[1]);
        Assert.True(TftpCodec.TryRead(buffer.AsSpan(0, length), out var data, out _));
        Assert.Equal((ushort)block, data.Block);
        Assert.True(data.Data.SequenceEqual(payload));

        var ack = new byte[4];
        Assert.Equal(4, TftpCodec.EncodeAck(ack, (ushort)block));
        Assert.Equal(0x00, ack[0]);
        Assert.Equal(0x04, ack[1]);
        Assert.True(TftpCodec.TryRead(ack, out var ackPacket, out _));
        Assert.Equal((ushort)block, ackPacket.Block);
    }

    [Fact]
    public void Error_RoundTripsCodeAndMessage()
    {
        var buffer = new byte[64];
        var length = TftpCodec.EncodeError(buffer, TftpErrorCode.FileExists, "file already exists");
        Assert.True(TftpCodec.TryRead(buffer.AsSpan(0, length), out var packet, out _));
        Assert.Equal(TftpOpcode.Error, packet.Opcode);
        Assert.Equal(TftpErrorCode.FileExists, packet.ErrorCode);
        Assert.Equal("file already exists", packet.ErrorMessage);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x00, 0x03, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x04, 0x00, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x02, 0x61, 0x00 })]
    public void TruncatedOrPaddedPackets_AreRejected(byte[] packet)
    {
        Assert.False(TftpCodec.TryRead(packet, out _, out _));
    }

    [Fact]
    public void NonAsciiRequest_IsRejected()
    {
        byte[] packet = [0x00, 0x02, 0xC2, 0x00, 0x6F, 0x63, 0x74, 0x65, 0x74, 0x00];
        Assert.False(TftpCodec.TryRead(packet, out _, out _));
    }

    [Fact]
    public void Server_ReducesBlockAndWindow_AndIgnoresUnknownOptions()
    {
        TftpOption[] requested =
        [
            new("blksize", "8192"),
            new("windowsize", "64"),
            new("tsize", "768000"),
        ];
        Assert.True(TftpNegotiation.TryAccept(requested, maxBlockSize: 1428, maxWindowSize: 16, out var negotiated, out var code, out var error), error);
        Assert.Equal((ushort)0, code);
        Assert.Equal(1428, negotiated.BlockSize);
        Assert.Equal(16, negotiated.WindowSize);
        Assert.True(TftpCodec.TryRead(negotiated.Oack, out var oack, out _));
        Assert.Equal(2, oack.Options.Count);
        Assert.DoesNotContain(oack.Options, option => option.Name == "tsize");
    }

    [Theory]
    [InlineData("7")]
    [InlineData("65465")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("512 ")]
    public void IllegalBlockSize_FailsNegotiation(string value)
    {
        Assert.False(TftpNegotiation.TryAccept([new("blksize", value)], 1428, 16, out _, out var code, out _));
        Assert.Equal(TftpErrorCode.OptionNegotiationFailed, code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    public void IllegalWindowSize_FailsNegotiation(string value)
    {
        Assert.False(TftpNegotiation.TryAccept([new("windowsize", value)], 512, 32, out _, out var code, out _));
        Assert.Equal(TftpErrorCode.OptionNegotiationFailed, code);
    }

    [Fact]
    public void DuplicateBlockSize_FailsNegotiation()
    {
        TftpOption[] requested = [new("blksize", "512"), new("BLKSIZE", "1024")];
        Assert.False(TftpNegotiation.TryAccept(requested, 1428, 8, out _, out _, out var error));
        Assert.Contains("duplicate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_RejectsRaisedOrUnrequestedOptions_AndDefaultsOmittedOnes()
    {
        Assert.False(TftpNegotiation.TryAdopt(true, 512, true, 4, [new("blksize", "1024")], out _, out _, out _));
        Assert.False(TftpNegotiation.TryAdopt(true, 512, false, 1, [new("windowsize", "1")], out _, out _, out _));
        Assert.True(TftpNegotiation.TryAdopt(true, 1428, true, 16, [new("blksize", "512")], out var block, out var window, out var error), error);
        Assert.Equal(512, block);
        Assert.Equal(1, window);
    }

    [Fact]
    public void Payloads_AreDeterministicAndNotAllZero()
    {
        var first = ArticlePayload.CreatePseudorandom(4096, 7);
        var second = ArticlePayload.CreatePseudorandom(4096, 7);
        var other = ArticlePayload.CreatePseudorandom(4096, 8);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Contains(first, value => value != 0);
        var article = ArticlePayload.CreateArticle(ArticlePayload.DefaultBytes, 7);
        Assert.Equal(ArticlePayload.DefaultBytes, article.Length);
        ReadOnlySpan<byte> prefix = "Path: tftp.vectornntp.local"u8;
        Assert.True(article.AsSpan(0, prefix.Length).SequenceEqual(prefix));
    }

    [Fact]
    public void Percentile_UsesNearestRank()
    {
        double[] sample = [1, 2, 3, 4];
        Assert.Equal(2, BenchmarkRunner.Percentile(sample, 0.50));
        Assert.Equal(4, BenchmarkRunner.Percentile(sample, 0.95));
        Assert.Equal(0, BenchmarkRunner.Percentile([], 0.99));
    }

    [Fact]
    public void Help_DescribesVerificationAndVstpComparison()
    {
        var parsed = CommandLine.Parse(["help"], new StringWriter());
        Assert.True(parsed.ShowHelp);
        Assert.Contains("not durable", CommandLine.HelpText, StringComparison.Ordinal);
        Assert.Contains("768000", CommandLine.HelpText, StringComparison.Ordinal);
        Assert.Contains("storage-smoke", CommandLine.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void BenchmarkCommand_DefaultsToLoopbackArticleSize()
    {
        var parsed = CommandLine.Parse(["benchmark"], new StringWriter());
        Assert.NotNull(parsed.Benchmark);
        var plan = parsed.Benchmark;
        Assert.True(plan.SelfHost);
        Assert.Equal(768_000, plan.PayloadBytes);
        Assert.Equal(1428, plan.BlockSize);
        Assert.Equal(16, plan.WindowSize);
        Assert.Equal(1, plan.Concurrency);
        Assert.Equal(16, plan.Transfers);
    }

    [Fact]
    public void ServerCommand_RequiresRoot()
    {
        var error = new StringWriter();
        var parsed = CommandLine.Parse(["server"], error);
        Assert.Equal(2, parsed.ExitCode);
        Assert.Contains("root", error.ToString(), StringComparison.Ordinal);
    }
}
