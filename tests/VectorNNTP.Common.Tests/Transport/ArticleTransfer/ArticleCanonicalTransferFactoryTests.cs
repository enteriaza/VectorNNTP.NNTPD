using System.IO.Hashing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.Common.Tests.Transport.ArticleTransfer
{
    public sealed class ArticleCanonicalTransferFactoryTests
    {
        [Fact]
        public void TryCreateFromCanonicalTransfer_ValidRoundTrip_MatchesFactoryRecord()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var expected = record.ArtId;

            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            var transferred = created.Record;

            Assert.Equal(ArticleParseStatus.CanonicalV1, transferred.ParseStatus);
            Assert.Equal(record.ArtId, transferred.ArtId);
            Assert.Equal(record.ArtHash, transferred.ArtHash);
            Assert.Equal(record.ArtType, transferred.ArtType);
            Assert.Equal(record.ArtLines, transferred.ArtLines);
            Assert.Equal(record.CanonicalUtc, transferred.CanonicalUtc);
            Assert.Equal(record.Fields, transferred.Fields);
            Assert.True(transferred.ArtData.Span.SequenceEqual(artData));
            Assert.True(IsSameBuffer(transferred.ArtData, artData));
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_DoesNotCopyArtData()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.True(created.IsAccepted);
            Assert.True(IsSameBuffer(created.Record.ArtData, artData));
            artData[0] ^= 0xFF;
            Assert.Equal(artData[0], created.Record.ArtData.Span[0]);
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_ArtSizeMismatch_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash,
                record.ArtLines,
                record.ArtSize + 1,
                selected,
                record.Fields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferArtSizeMismatch, created.MaterializeFailure);
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_ArtHashMismatch_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash ^ 1UL,
                record.ArtLines,
                record.ArtSize,
                selected,
                record.Fields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferArtHashMismatch, created.MaterializeFailure);
            Assert.Equal(ArticleParseStatus.None, created.Record.ParseStatus);
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_ArticleIdMismatch_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var wrong = ArticleId.FromMessageId("<other@example.test>"u8);
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in wrong);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferArticleIdMismatch, created.MaterializeFailure);
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_FieldRangeOutsideBuffer_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var badFields = new ArticleFieldTable(
                new ArticleByteRange(artData.Length - 1, 5),
                record.Fields.Newsgroups,
                record.Fields.Subject,
                record.Fields.From,
                record.Fields.Date,
                record.Fields.References,
                record.Fields.Path);
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash,
                record.ArtLines,
                record.ArtSize,
                selected,
                badFields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferInvalidFieldRange, created.MaterializeFailure);
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_LocateMismatch_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            // Shift Subject range by one while keeping bounds valid.
            var subject = record.Fields.Subject;
            Assert.True(subject.IsPresent && subject.Length > 1);
            var shifted = new ArticleByteRange(subject.Offset + 1, subject.Length - 1);
            var badFields = new ArticleFieldTable(
                record.Fields.MessageId,
                record.Fields.Newsgroups,
                shifted,
                record.Fields.From,
                record.Fields.Date,
                record.Fields.References,
                record.Fields.Path);
            var meta = new ArticleCanonicalTransferMeta(
                XxHash3.HashToUInt64(artData),
                record.ArtLines,
                record.ArtSize,
                selected,
                badFields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferFieldTableMismatch, created.MaterializeFailure);
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_InvalidSelectedDateHeader_Rejected()
        {
            var (record, _, artData) = VatpTestArticles.CreateCanonical();
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash,
                record.ArtLines,
                record.ArtSize,
                NntpArticleHeaderName.Unknown,
                record.Fields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferInvalidSelectedDateHeader, created.MaterializeFailure);
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_InvalidArtLines_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash,
                -1,
                record.ArtSize,
                selected,
                record.Fields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferInvalidArtLines, created.MaterializeFailure);
        }

        [Theory]
        [InlineData("<part.one+tag@news-server.example.net>")]
        [InlineData("<foo@[1.2.3.4]>")]
        [InlineData("<foo@[IPv6:2001:db8::1]>")]
        public void TryCreateFromCanonicalTransfer_ValidMessageId_Accepted(string messageId)
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical(messageId);
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var expected = record.ArtId;

            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);

            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            Assert.Equal(expected, created.Record.ArtId);
        }

        [Theory]
        [InlineData("<fooexample.test>")]
        [InlineData("<double..dot@example.test>")]
        [InlineData("<.leading@example.test>")]
        [InlineData("<trailing.@example.test>")]
        [InlineData("<foo@.example.test>")]
        [InlineData("<foo@example.>")]
        [InlineData("<foo bar@example.test>")]
        [InlineData("<foo@[1.2.3.4>")]
        [InlineData("<foo@[[1.2.3.4]>")]
        [InlineData("<foo@bar>baz>")]
        public void TryCreateFromCanonicalTransfer_MalformedMessageId_RejectedWhenArticleIdMatches(string messageId)
        {
            var created = CreateConsistentTransfer(System.Text.Encoding.ASCII.GetBytes(messageId));

            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferMissingMessageId, created.MaterializeFailure);
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_MessageIdLongerThan250_RejectedWhenArticleIdMatches()
        {
            var messageId = new byte[251];
            messageId[0] = (byte)'<';
            messageId.AsSpan(1, 247).Fill((byte)'a');
            messageId[248] = (byte)'@';
            messageId[249] = (byte)'b';
            messageId[250] = (byte)'>';

            var created = CreateConsistentTransfer(messageId);

            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferMissingMessageId, created.MaterializeFailure);
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_InvalidMessageId_DoesNotAllocate()
        {
            var artData = BuildArticle("<foo@bar>baz>"u8);
            var fields = ArticleFieldTable.Locate(artData, NntpArticleHeaderName.Date);
            var messageId = fields.MessageId.Slice(artData).ToArray();
            var expected = ArticleId.FromMessageId(messageId);
            var meta = new ArticleCanonicalTransferMeta(
                XxHash3.HashToUInt64(artData),
                1,
                artData.Length,
                NntpArticleHeaderName.Date,
                fields);

            var warmup = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferMissingMessageId, warmup.MaterializeFailure);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(NntpArticleCanonicalFailureCode.TransferMissingMessageId, created.MaterializeFailure);
            Assert.Equal(0, allocated);
        }

        [Fact]
        public void TryCreateFromCanonicalTransfer_MutatedArtData_HashFails()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            artData[^1] ^= 0x01;
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferArtHashMismatch, created.MaterializeFailure);
        }

        private static ArticleRecordCreateResult CreateConsistentTransfer(ReadOnlySpan<byte> messageId)
        {
            var artData = BuildArticle(messageId);
            var fields = ArticleFieldTable.Locate(artData, NntpArticleHeaderName.Date);
            var value = fields.MessageId.Slice(artData);
            var expected = ArticleId.FromMessageId(value);
            var meta = new ArticleCanonicalTransferMeta(
                XxHash3.HashToUInt64(artData),
                1,
                artData.Length,
                NntpArticleHeaderName.Date,
                fields);
            return ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
        }

        private static byte[] BuildArticle(ReadOnlySpan<byte> messageId)
        {
            var prefix = "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "u8;
            var suffix = "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: vatp\r\n\r\nline1\r\n"u8;
            var artData = new byte[prefix.Length + messageId.Length + suffix.Length];
            prefix.CopyTo(artData);
            messageId.CopyTo(artData.AsSpan(prefix.Length));
            suffix.CopyTo(artData.AsSpan(prefix.Length + messageId.Length));
            return artData;
        }

        private static bool IsSameBuffer(ReadOnlyMemory<byte> memory, byte[] array) =>
            System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment)
            && ReferenceEquals(segment.Array, array)
            && segment.Offset == 0
            && segment.Count == array.Length;
    }
}
