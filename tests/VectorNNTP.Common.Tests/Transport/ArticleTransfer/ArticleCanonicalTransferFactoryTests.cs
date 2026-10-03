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

        private static bool IsSameBuffer(ReadOnlyMemory<byte> memory, byte[] array) =>
            System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment)
            && ReferenceEquals(segment.Array, array)
            && segment.Offset == 0
            && segment.Count == array.Length;
    }
}
