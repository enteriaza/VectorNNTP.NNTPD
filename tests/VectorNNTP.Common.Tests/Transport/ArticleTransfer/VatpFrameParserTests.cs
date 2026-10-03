using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.Common.Tests.Transport.ArticleTransfer
{
    public sealed class VatpFrameParserTests
    {
        [Fact]
        public void ParseOneFrame_CompleteFrame_Succeeds()
        {
            var encoded = VatpFrameEncoder.EncodeEnd(42);
            var frame = VatpFrameEncoder.ToSingleBuffer(encoded);
            var result = VatpFrameParser.ParseOneFrame(frame, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, result.Status);
            Assert.Equal(16, result.ConsumedBytes);
            Assert.Equal(VatpFrameType.End, result.Frame!.Value.Header.Type);
            Assert.Equal(42u, result.Frame.Value.Header.StreamId);
        }

        [Fact]
        public void ParseOneFrame_PartialHeader_IsIncomplete()
        {
            var frame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1));
            var result = VatpFrameParser.ParseOneFrame(frame.AsSpan(0, 8).ToArray(), VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Incomplete, result.Status);
            Assert.Equal(0, result.ConsumedBytes);
        }

        [Fact]
        public void ParseOneFrame_PartialPayload_IsIncomplete()
        {
            var payload = new byte[32];
            var encoded = VatpFrameEncoder.EncodeData(1, payload);
            var frame = VatpFrameEncoder.ToSingleBuffer(encoded);
            var result = VatpFrameParser.ParseOneFrame(frame.AsSpan(0, 20).ToArray(), VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Incomplete, result.Status);
        }

        [Fact]
        public void ParseOneFrame_ByteAtATime_EventuallySucceeds()
        {
            var frame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeWindow(9, 1024));
            VatpFrameParseResult result = default;
            for (var i = 1; i <= frame.Length; i++)
            {
                result = VatpFrameParser.ParseOneFrame(frame.AsSpan(0, i).ToArray(), VatpProtocol.DefaultMaxFramePayload);
                if (i < frame.Length)
                {
                    Assert.Equal(VatpFrameParseStatus.Incomplete, result.Status);
                }
            }

            Assert.Equal(VatpFrameParseStatus.Success, result.Status);
            Assert.Equal(frame.Length, result.ConsumedBytes);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(7)]
        [InlineData(15)]
        [InlineData(17)]
        public void ParseOneFrame_FragmentedSequence_Succeeds(int chunkSize)
        {
            var frame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(3));
            var sequence = VatpTestArticles.Fragment(frame, chunkSize);
            var result = VatpFrameParser.ParseOneFrame(in sequence, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, result.Status);
            Assert.Equal(VatpFrameType.Cancel, result.Frame!.Value.Header.Type);
        }

        [Fact]
        public void ParseOneFrame_CompleteThenPartial_LeavesIncompleteTail()
        {
            var complete = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(1, new byte[64], fin: false));
            var next = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(1, new byte[64], fin: true));
            var buffer = VatpTestArticles.Concat(complete, next.AsSpan(0, 20).ToArray());

            var one = VatpFrameParser.ParseOneFrame(buffer, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, one.Status);
            Assert.Equal(complete.Length, one.ConsumedBytes);

            var remaining = buffer.AsMemory((int)one.ConsumedBytes).ToArray();
            var two = VatpFrameParser.ParseOneFrame(remaining, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Incomplete, two.Status);
        }

        [Fact]
        public void ParseOneFrame_MalformedPayloadLengthOverflow_Rejected()
        {
            var header = new byte[16];
            // PayloadLength = uint.MaxValue would overflow Header+Payload as checked long path;
            // parser rejects via FrameTooLarge against maxFramePayload first.
            VatpFrameHeader.Create(VatpFrameType.Data, 1, payloadLength: uint.MaxValue).WriteTo(header);
            var result = VatpFrameParser.ParseOneFrame(header, maxFramePayload: 1024);
            Assert.Equal(VatpFrameParseStatus.Invalid, result.Status);
            Assert.Equal(VatpErrorCode.FrameTooLarge, result.Error);
        }

        [Fact]
        public void ParseOneFrame_MultipleFramesInOneBuffer_ParsesFirstThenSecond()
        {
            var first = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1));
            var second = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(2));
            var buffer = VatpTestArticles.Concat(first, second);

            var one = VatpFrameParser.ParseOneFrame(buffer, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, one.Status);
            Assert.Equal(1u, one.Frame!.Value.Header.StreamId);

            var remaining = buffer.AsMemory((int)one.ConsumedBytes);
            var two = VatpFrameParser.ParseOneFrame(remaining.ToArray(), VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpFrameParseStatus.Success, two.Status);
            Assert.Equal(2u, two.Frame!.Value.Header.StreamId);
            Assert.Equal(VatpFrameType.Cancel, two.Frame.Value.Header.Type);
        }

        [Fact]
        public void ParseOneFrame_TransferFrameOnStreamZero_Rejected()
        {
            var header = new byte[16];
            VatpFrameHeader.Create(VatpFrameType.End, 0, 0).WriteTo(header);
            var result = VatpFrameParser.ParseOneFrame(header, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpErrorCode.InvalidStreamId, result.Error);
        }

        [Fact]
        public void ParseOneFrame_HelloOnNonZeroStream_Rejected()
        {
            var hello = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(64 * 1024));
            hello[4] = 0;
            hello[5] = 0;
            hello[6] = 0;
            hello[7] = 1; // StreamId = 1
            var result = VatpFrameParser.ParseOneFrame(hello, VatpProtocol.DefaultMaxFramePayload);
            Assert.Equal(VatpErrorCode.InvalidStreamId, result.Error);
        }

        [Fact]
        public void ParseOneFrame_DoesNotAllocateArticleSizedBufferForOversizedDeclaredPayload()
        {
            // Declares a huge payload but only supplies a header; must be Incomplete, not allocate.
            var header = new byte[16];
            VatpFrameHeader.Create(VatpFrameType.Data, 1, payloadLength: 5 * 1024 * 1024).WriteTo(header);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = VatpFrameParser.ParseOneFrame(header, VatpProtocol.DefaultMaxFramePayload);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(VatpFrameParseStatus.Invalid, result.Status);
            Assert.Equal(VatpErrorCode.FrameTooLarge, result.Error);
            Assert.True(allocated < 64 * 1024, $"allocated {allocated}");
        }
    }
}
