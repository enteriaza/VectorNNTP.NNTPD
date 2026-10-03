using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.Common.Tests.Transport.ArticleTransfer
{
    public sealed class ArticleTransferWindowAndFairnessTests
    {
        [Fact]
        public void Window_InitialCredit_ConsumeAndRestore()
        {
            var window = new ArticleTransferWindow(initialCredit: 1024, maxCredit: 4096);
            Assert.Equal(1024, window.Credit);
            Assert.True(window.TryConsume(100));
            Assert.Equal(924, window.Credit);
            Assert.False(window.TryConsume(1000));
            Assert.Equal(924, window.Credit);
            Assert.Equal(1000, window.Add(1000));
            Assert.Equal(1924, window.Credit);
        }

        [Fact]
        public void Window_Add_SaturatesAtMax()
        {
            var window = new ArticleTransferWindow(initialCredit: 100, maxCredit: 150);
            Assert.Equal(50, window.Add(1000));
            Assert.Equal(150, window.Credit);
            Assert.Equal(0, window.Add(10));
        }

        [Fact]
        public void Window_Consume_CannotUnderflow()
        {
            var window = new ArticleTransferWindow(10, 100);
            Assert.False(window.TryConsume(11));
            Assert.Equal(10, window.Credit);
            Assert.True(window.TryConsume(10));
            Assert.Equal(0, window.Credit);
            Assert.False(window.HasCredit);
        }

        [Fact]
        public void ReceiveStream_DataExceedingCredit_Fails()
        {
            var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 8, MaxStreamCreditBytes = 8 };
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            Assert.True(artData.Length > 8);
            var stream = new ArticleTransferReceiveStream(1, Guid.NewGuid(), record.ArtId, limits);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            var result = stream.TryAcceptData(artData.AsSpan(0, 9), fin: false);
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.FlowControlViolation, result.Error);
        }

        [Fact]
        public void ReceiveStream_WindowRestoresCredit()
        {
            var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 4, MaxStreamCreditBytes = 1024 };
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(2, Guid.NewGuid(), record.ArtId, limits);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData.AsSpan(0, 4), fin: false).Success);
            Assert.Equal(0, stream.ReceiveCredit);
            Assert.True(stream.TryAcceptWindow((uint)(artData.Length - 4)).Success);
            Assert.True(stream.TryAcceptData(artData.AsSpan(4), fin: true).Success);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            Assert.False(stream.HasConsumableRecord);
            Assert.True(stream.TryAcceptEnd().Success);
            Assert.True(stream.HasConsumableRecord);
        }

        [Fact]
        public void ReceiveStream_MultipleWindowReplenishments_ReachExactArtSize()
        {
            // Mirror DefaultInitialStreamWindowBytes pacing with a tiny window so several
            // replenishments are required before ArtSize is complete.
            const int initialWindow = 8;
            const int chunk = 8;
            var limits = new ArticleTransferLimits
            {
                InitialStreamWindowBytes = initialWindow,
                MaxStreamCreditBytes = 1024,
            };
            var body = new string('x', 40) + "\r\n";
            var (record, selected, artData) = VatpTestArticles.CreateCanonical("<multi-window@example.test>", body);
            Assert.True(artData.Length > initialWindow * 2);

            var stream = new ArticleTransferReceiveStream(3, Guid.NewGuid(), record.ArtId, limits);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);

            var offset = 0;
            var replenishments = 0;
            while (offset < artData.Length)
            {
                var length = Math.Min(chunk, artData.Length - offset);
                var fin = offset + length >= artData.Length;
                Assert.True(stream.TryAcceptData(artData.AsSpan(offset, length), fin).Success);
                Assert.True(stream.TryAcceptWindow((uint)length).Success);
                replenishments++;
                offset += length;
                Assert.True(stream.ReceiveCredit <= limits.MaxStreamCreditBytes);
                Assert.True(stream.ReceiveCredit >= 0);
            }

            Assert.True(replenishments >= 3);
            Assert.Equal(artData.Length, stream.ReceivedBytes);
            Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
            Assert.True(stream.TryAcceptEnd().Success);
            Assert.True(stream.HasConsumableRecord);
        }

        [Fact]
        public void ReceiveStream_Cancel_DoesNotAcceptFurtherWindow()
        {
            var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 16, MaxStreamCreditBytes = 1024 };
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var stream = new ArticleTransferReceiveStream(4, Guid.NewGuid(), record.ArtId, limits);
            Assert.True(stream.TryAcceptMeta(VatpMetaCodec.Encode(ArticleCanonicalTransferMeta.FromRecord(record, selected))).Success);
            Assert.True(stream.TryAcceptData(artData.AsSpan(0, 4), fin: false).Success);
            Assert.True(stream.TryCancel().Success);
            Assert.False(stream.TryAcceptWindow(4).Success);
            Assert.Equal(VatpErrorCode.UnknownStream, stream.TryAcceptWindow(4).Error);
            Assert.Equal(ArticleTransferPhase.Cancelled, stream.Phase);
        }

        [Fact]
        public void ReadyRing_RoundRobinsAndSkipsRemoved()
        {
            var ring = new ArticleTransferReadyRing();
            ring.Enqueue(1);
            ring.Enqueue(2);
            ring.Enqueue(3);
            Assert.True(ring.TryTakeNext(out var a));
            Assert.Equal(1u, a);
            Assert.True(ring.TryTakeNext(out var b));
            Assert.Equal(2u, b);
            Assert.True(ring.Remove(3));
            Assert.True(ring.TryTakeNext(out var c));
            Assert.Equal(1u, c);
        }

        [Fact]
        public void ComputeDataPayloadLength_RespectsRemainingCreditAndMaxFrame()
        {
            Assert.Equal(0, ArticleTransferReadyRing.ComputeDataPayloadLength(100, credit: 0, maxFramePayload: 64));
            Assert.Equal(50, ArticleTransferReadyRing.ComputeDataPayloadLength(100, credit: 50, maxFramePayload: 64));
            Assert.Equal(64, ArticleTransferReadyRing.ComputeDataPayloadLength(100, credit: 1000, maxFramePayload: 64));
            Assert.Equal(20, ArticleTransferReadyRing.ComputeDataPayloadLength(20, credit: 1000, maxFramePayload: 64));
        }

        [Fact]
        public void StreamTable_RejectsDuplicateAndZeroAndOverflow()
        {
            var limits = new ArticleTransferLimits { MaxStreamsPerConnection = 1 };
            var table = new ArticleTransferStreamTable(limits);
            var artId = ArticleId.FromMessageId("<a@b>"u8);
            Assert.False(table.TryOpen(0, Guid.NewGuid(), artId, out _).Success);
            Assert.True(table.TryOpen(1, Guid.NewGuid(), artId, out var stream).Success);
            Assert.NotNull(stream);
            Assert.False(table.TryOpen(1, Guid.NewGuid(), artId, out _).Success);
            Assert.False(table.TryOpen(2, Guid.NewGuid(), artId, out _).Success);
            Assert.True(table.TryRemove(1));
            Assert.True(table.TryOpen(1, Guid.NewGuid(), artId, out _).Success);
        }

        [Fact]
        public void WindowOnAwaitingMeta_IsUnknownStream()
        {
            var stream = new ArticleTransferReceiveStream(11, Guid.NewGuid(), ArticleId.FromMessageId("<a@b>"u8));
            var result = stream.TryAcceptWindow(100);
            Assert.False(result.Success);
            Assert.Equal(VatpErrorCode.UnknownStream, result.Error);
        }
    }
}
