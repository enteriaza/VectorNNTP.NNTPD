using System.Buffers.Binary;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.Common.Tests.Transport.ArticleTransfer;

public sealed class VatpHelloTests
{
    [Fact]
    public void EncodeDecode_ValidHello_Succeeds()
    {
        var encoded = VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload);
        var frame = VatpFrameEncoder.ToSingleBuffer(encoded);
        var parsed = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
        Assert.Equal(VatpFrameParseStatus.Success, parsed.Status);
        Assert.Equal(VatpFrameType.Hello, parsed.Frame!.Value.Header.Type);
        Assert.Equal(0u, parsed.Frame.Value.Header.StreamId);

        Assert.True(VatpHello.TryDecode(parsed.Frame.Value.Payload, out var hello, out var error));
        Assert.Equal(VatpErrorCode.None, error);
        Assert.Equal(VatpProtocol.DefaultMaxFramePayload, hello.MaxFramePayload);
    }

    [Fact]
    public void Decode_BadMagic_Fails()
    {
        Span<byte> payload = stackalloc byte[12];
        VatpHello.Encode(payload, 1024);
        payload[0] = (byte)'X';
        Assert.False(VatpHello.TryDecode(payload, out _, out var error));
        Assert.Equal(VatpErrorCode.InvalidHello, error);
    }

    [Fact]
    public void Decode_ZeroMaxFrame_Fails()
    {
        Span<byte> payload = stackalloc byte[12];
        VatpProtocol.HelloMagic.CopyTo(payload);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(8, 4), 0);
        Assert.False(VatpHello.TryDecode(payload, out _, out var error));
        Assert.Equal(VatpErrorCode.InvalidMaxFramePayload, error);
    }

    [Fact]
    public void Decode_Truncated_Fails()
    {
        Assert.False(VatpHello.TryDecode("VNATP01"u8, out _, out var error));
        Assert.Equal(VatpErrorCode.InvalidHello, error);
    }

    [Fact]
    public void Parser_HelloWrongPayloadLength_Fails()
    {
        var header = new byte[16];
        VatpFrameHeader.Create(VatpFrameType.Hello, 0, 8).WriteTo(header);
        var frame = VatpTestArticles.Concat(header, new byte[8]);
        var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
        Assert.Equal(VatpErrorCode.InvalidHello, result.Error);
    }
}
