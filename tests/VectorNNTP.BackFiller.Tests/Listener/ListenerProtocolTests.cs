using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Listener;

public sealed class ListenerProtocolTests
{
    [Fact]
    public void GetRequest_round_trips_the_canonical_md5()
    {
        var md5 = ArticleIdentity.FromExactMessageId(ArticleWorkTestDeliveries.CanonicalMessageId).Md5Hex;
        var frame = ListenerProtocolEncoder.EncodeGetRequest(7, md5);
        var parsed = ListenerProtocolParser.ParseOneFrame(frame);

        Assert.Equal(ListenerFrameParseStatus.Success, parsed.Status);
        Assert.Equal(ListenerOpcode.GetRequest, parsed.Frame!.Value.Header.Opcode);
        Assert.Equal(7u, parsed.Frame.Value.Header.RequestId);
        Assert.Equal(md5, Encoding.ASCII.GetString(parsed.Frame.Value.Payload.ToArray()));
    }

    [Fact]
    public void Found_header_does_not_copy_or_mutate_payload_bytes()
    {
        var payload = new byte[] { 0x00, 0x0A, 0x0D, 0xFF, (byte)'A' };
        var found = ListenerProtocolEncoder.EncodeGetResponseFound(3, payload);

        Assert.True(found.Payload.Span.SequenceEqual(payload));
        Assert.Equal(payload.Length, (int)ListenerFrameHeader.ReadFrom(found.Header.Span).PayloadLength);
        payload[0] = 0x11;
        Assert.Equal(0x11, found.Payload.Span[0]);
    }

    [Fact]
    public void NotFound_and_error_and_ack_have_exact_payload_shapes()
    {
        var notFound = ListenerProtocolParser.ParseOneFrame(ListenerProtocolEncoder.EncodeGetResponseNotFound(2));
        var error = ListenerProtocolParser.ParseOneFrame(
            ListenerProtocolEncoder.EncodeGetResponseError(2, ListenerProtocolErrorCode.InvalidMessageIdMd5));
        var ack = ListenerProtocolParser.ParseOneFrame(ListenerProtocolEncoder.EncodeGetReceiptAck(2));

        Assert.Equal(ListenerOpcode.GetResponseNotFound, notFound.Frame!.Value.Header.Opcode);
        Assert.Equal(ListenerProtocol.NotFoundReasonUnavailable, notFound.Frame.Value.Payload.FirstSpan[0]);
        Assert.Equal(
            (ushort)ListenerProtocolErrorCode.InvalidMessageIdMd5,
            BinaryPrimitives.ReadUInt16BigEndian(error.Frame!.Value.Payload.ToArray()));
        Assert.Equal(0u, ack.Frame!.Value.Header.PayloadLength);
    }

    [Fact]
    public void Uppercase_md5_and_zero_request_id_are_invalid()
    {
        var uppercase = ListenerProtocolEncoder.EncodeGetRequest(1, "30edc94157aa16fe644a45a1f1ffe160");
        uppercase[16] = (byte)'A';
        Assert.Equal(ListenerFrameParseError.InvalidMessageIdMd5, ListenerProtocolParser.ParseOneFrame(uppercase).Error);

        var header = new byte[16];
        new ListenerFrameHeader(1, ListenerOpcode.GetReceiptAck, 16, 0, 0, 0).WriteTo(header);
        Assert.Equal(ListenerFrameParseError.InvalidRequestId, ListenerProtocolParser.ParseOneFrame(header).Error);
    }

    [Fact]
    public void Incomplete_and_unsupported_version_are_classified()
    {
        Assert.Equal(ListenerFrameParseStatus.Incomplete, ListenerProtocolParser.ParseOneFrame([0x01]).Status);
        var frame = ListenerProtocolEncoder.EncodeGetReceiptAck(4);
        frame[0] = 0x02;
        var parsed = ListenerProtocolParser.ParseOneFrame(frame);
        Assert.Equal(ListenerFrameParseStatus.Invalid, parsed.Status);
        Assert.Equal(ListenerFrameParseError.UnsupportedVersion, parsed.Error);
    }
}
