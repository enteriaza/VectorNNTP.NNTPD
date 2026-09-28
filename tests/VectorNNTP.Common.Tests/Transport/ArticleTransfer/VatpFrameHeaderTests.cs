using System.Buffers.Binary;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.Common.Tests.Transport.ArticleTransfer;

public sealed class VatpFrameHeaderTests
{
    [Fact]
    public void WriteTo_RoundTripsExactBigEndianLayout()
    {
        var header = VatpFrameHeader.Create(VatpFrameType.Data, streamId: 0x01020304, payloadLength: 0x05060708, flags: VatpProtocol.FlagFin);
        Span<byte> buffer = stackalloc byte[16];
        header.WriteTo(buffer);

        Assert.Equal(VatpProtocol.Version1, buffer[0]);
        Assert.Equal((byte)VatpFrameType.Data, buffer[1]);
        Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2)));
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)));
        Assert.Equal(0x05060708u, BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4)));
        Assert.Equal(VatpProtocol.FlagFin, BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(12, 4)));

        var decoded = VatpFrameHeader.ReadFrom(buffer);
        Assert.Equal(header, decoded);
        Assert.True(decoded.HasFin);
    }

    [Fact]
    public void Parser_RejectsInvalidHeaderLength()
    {
        var frame = EncodeRaw(VatpFrameType.End, streamId: 1, payloadLength: 0, flags: 0, headerLength: 15);
        var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
        Assert.Equal(VatpFrameParseStatus.Invalid, result.Status);
        Assert.Equal(VatpErrorCode.InvalidHeaderLength, result.Error);
    }

    [Fact]
    public void Parser_RejectsUnsupportedVersion()
    {
        var frame = EncodeRaw(VatpFrameType.End, streamId: 1, payloadLength: 0, flags: 0, version: 2);
        var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
        Assert.Equal(VatpFrameParseStatus.Invalid, result.Status);
        Assert.Equal(VatpErrorCode.UnsupportedVersion, result.Error);
    }

    [Fact]
    public void Parser_RejectsInvalidFrameType()
    {
        var frame = new byte[16];
        frame[0] = VatpProtocol.Version1;
        frame[1] = 0xFF;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 16);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), 1);
        var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
        Assert.Equal(VatpErrorCode.InvalidFrameType, result.Error);
    }

    [Fact]
    public void Parser_RejectsReservedFlags()
    {
        var frame = EncodeRaw(VatpFrameType.End, streamId: 1, payloadLength: 0, flags: 0x2);
        var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
        Assert.Equal(VatpErrorCode.InvalidFlags, result.Error);
    }

    [Fact]
    public void Parser_RejectsFinOnNonData()
    {
        var frame = EncodeRaw(VatpFrameType.End, streamId: 1, payloadLength: 0, flags: VatpProtocol.FlagFin);
        var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
        Assert.Equal(VatpErrorCode.InvalidFlags, result.Error);
    }

    [Fact]
    public void Parser_RejectsPayloadAboveMaxFramePayload()
    {
        var payload = new byte[17];
        var header = new byte[16];
        VatpFrameHeader.Create(VatpFrameType.Data, 1, (uint)payload.Length).WriteTo(header);
        var frame = VatpTestArticles.Concat(header, payload);
        var result = VatpFrameParser.ParseOneFrame(frame, maxFramePayload: 16);
        Assert.Equal(VatpErrorCode.FrameTooLarge, result.Error);
    }

    [Fact]
    public void Parser_AcceptsMaximumDefaultPayload()
    {
        var payload = new byte[VatpProtocol.DefaultMaxFramePayload];
        var encoded = VatpFrameEncoder.EncodeData(7, payload, fin: true);
        var frame = VatpFrameEncoder.ToSingleBuffer(encoded);
        var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
        Assert.Equal(VatpFrameParseStatus.Success, result.Status);
        Assert.True(result.Frame!.Value.Header.HasFin);
        Assert.Equal(VatpProtocol.DefaultMaxFramePayload, result.Frame.Value.Header.PayloadLength);
    }

    private static byte[] EncodeRaw(
        VatpFrameType type,
        uint streamId,
        uint payloadLength,
        uint flags,
        ushort headerLength = 16,
        byte version = VatpProtocol.Version1)
    {
        var frame = new byte[16 + (int)payloadLength];
        frame[0] = version;
        frame[1] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), headerLength);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), streamId);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), payloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12, 4), flags);
        return frame;
    }
}
