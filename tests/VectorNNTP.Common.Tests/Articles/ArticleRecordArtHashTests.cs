using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;

namespace VectorNNTP.Common.Tests.Articles
{
    /// <summary>
    /// ArtHash is one-shot XXH3-64 of final canonical ArtData.
    /// </summary>
    public sealed class ArticleRecordArtHashTests
    {
        private const string LocalFqdn = "backfiller01.usenet.ninja";

        /// <summary>XXH3-64 of ASCII <c>123456789</c> with default seed 0.</summary>
        public const ulong XxHash3KnownVector = 0x72DCB18B67A17DFFUL;

        [Fact]
        public void XxHash3_KnownVector_IsDeterministic()
        {
            Assert.Equal(XxHash3KnownVector, XxHash3.HashToUInt64("123456789"u8));
            Assert.Equal(XxHash3.HashToUInt64("123456789"u8), XxHash3.HashToUInt64("123456789"u8));
        }

        [Fact]
        public void TryCreate_ArtHash_MatchesIndependentXxHash3OfArtData()
        {
            var record = CreateAccepted(
                path: "peer.example",
                date: "Fri, 23 Aug 2024 07:30:10 +0000",
                messageId: "<independent@example.test>",
                body: "payload\r\n");
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        [Fact]
        public void TryCreate_ArtHash_IsStableForIdenticalCanonicalArtData()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<stable@example.test>", "body\r\n");
            var first = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            var second = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(first.IsAccepted);
            Assert.True(second.IsAccepted);
            Assert.True(first.Record.ArtData.Span.SequenceEqual(second.Record.ArtData.Span));
            Assert.Equal(first.Record.ArtHash, second.Record.ArtHash);
        }

        [Fact]
        public void TryCreate_ArtHash_ChangesWhenCanonicalContentChanges()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<change@example.test>", "body\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            var mutated = record.ArtData.ToArray();
            mutated[^2] ^= 0x01;
            Assert.NotEqual(record.ArtHash, XxHash3.HashToUInt64(mutated));
        }

        [Fact]
        public void TryCreate_ArtId_UnchangedWhenArticleBodyChanges()
        {
            var first = CreateAccepted("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<same-id@example.test>", "aaa\r\n");
            var second = CreateAccepted("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<same-id@example.test>", "bbb\r\n");
            Assert.Equal(first.ArtId, second.ArtId);
            Assert.NotEqual(first.ArtHash, second.ArtHash);
        }

        [Fact]
        public void TryCreate_ArtId_UnchangedByDateAndPathCanonicalization()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 09:30:10 +0200", "<canon-id@example.test>", "body\r\n");
            var parse = new NntpArticleParser(LocalFqdn).Parse(destuffed);
            Assert.True(parse.IsAccepted);
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            Assert.Equal(ArticleId.FromMessageId("<canon-id@example.test>"u8), created.Record.ArtId);
            Assert.False(created.Record.Date.SequenceEqual("Fri, 23 Aug 2024 09:30:10 +0200"u8));
            Assert.False(created.Record.Path.SequenceEqual("peer.example"u8));
        }

        [Fact]
        public void TryCreate_DateRewrite_ProducesExpectedArtDataAndArtHash()
        {
            const string date = "Fri, 23 Aug 2024 09:30:10 +0200";
            var destuffed = BuildDestuffed("peer.example", date, "<date-rewrite@example.test>", "body\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            AssertRecordInvariants(record, destuffed, expectedLines: 1);
            Assert.False(record.Date.SequenceEqual(Encoding.ASCII.GetBytes(date)));
            Assert.True(record.Date.SequenceEqual("Fri, 23 Aug 2024 07:30:10 +0000"u8));
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        [Fact]
        public void TryCreate_PathRewrite_ProducesExpectedArtDataAndArtHash()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<path-rewrite@example.test>", "body\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            AssertRecordInvariants(record, destuffed, expectedLines: 1);
            Assert.True(record.Path.SequenceEqual("news.usenet.ninja!backfiller01.usenet.ninja!peer.example"u8));
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        [Fact]
        public void TryCreate_PathInsertion_ProducesExpectedArtDataAndArtHash()
        {
            var destuffed = BuildDestuffedWithoutPath("Fri, 23 Aug 2024 07:30:10 +0000", "<path-insert@example.test>", "body\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            AssertRecordInvariants(record, destuffed, expectedLines: 1);
            Assert.True(record.Fields.Path.IsPresent);
            Assert.True(record.Path.StartsWith("news.usenet.ninja!backfiller01.usenet.ninja"u8));
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        [Theory]
        [InlineData("crlf", "a\r\nb\r\n")]
        [InlineData("lf", "a\nb\n")]
        [InlineData("cr", "a\rb\r")]
        [InlineData("mixed", "a\r\nb\nc\r")]
        public void TryCreate_LineTerminators_ArtHashMatchesArtData(string name, string body)
        {
            _ = name;
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", $"<{name}@example.test>", body);
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted, created.ParseFailure + "/" + created.MaterializeFailure);
            var record = created.Record;
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
            Assert.Equal(ArticleParseStatus.CanonicalV1, record.ParseStatus);
            Assert.Equal(record.ArtData.Length, record.ArtSize);
        }

        [Fact]
        public void TryCreate_SmallArticle_Works()
        {
            var destuffed = BuildDestuffed("p", "Fri, 23 Aug 2024 07:30:10 +0000", "<small@example.test>", "x\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            AssertRecordInvariants(record, destuffed, expectedLines: 1);
            Assert.True(record.ArtSize < 512);
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        [Fact]
        public void TryCreate_EmptyInput_DoesNotProduceRecord()
        {
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), ReadOnlyMemory<byte>.Empty);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.EmptyArticle, created.ParseFailure);
            Assert.Equal(ArticleParseStatus.None, created.Record.ParseStatus);
            Assert.Equal(0UL, created.Record.ArtHash);
        }

        [Fact]
        public void TryCreate_DoesNotAllocateSecondArticleSizedBuffer()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<onebuf@example.test>", "body\r\n");
            var parser = new NntpArticleParser(LocalFqdn);
            _ = ArticleRecordFactory.TryCreate(parser, destuffed);
            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted);
            var artSize = created.Record.ArtSize;

            var before = GC.GetAllocatedBytesForCurrentThread();
            var again = ArticleRecordFactory.TryCreate(parser, destuffed);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(again.IsAccepted);
            Assert.Equal(XxHash3.HashToUInt64(again.Record.ArtData.Span), again.Record.ArtHash);
            Assert.True(allocated >= artSize);
            Assert.True(allocated < artSize * 2, $"allocated {allocated} for ArtSize {artSize}");
        }

        [Fact]
        public void Factory_ArtData_AliasesMaterializerBuffer_AndArtHashIsOneShot()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 09:30:10 +0200", "<alias@example.test>", "z\r\n");
            var parser = new NntpArticleParser(LocalFqdn);
            var parse = parser.Parse(destuffed);
            Assert.True(parse.IsAccepted);
            var materialize = NntpArticleCanonicalMaterializer.Materialize(in parse, ArticlePathMode.Traverse);
            Assert.True(materialize.IsAccepted);
            Assert.NotNull(materialize.ArticleBytes);

            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            Assert.True(record.ArtData.Span.SequenceEqual(materialize.ArticleBytes));
            Assert.Equal(XxHash3.HashToUInt64(materialize.ArticleBytes), record.ArtHash);
            Assert.Equal(materialize.ArticleBytes.Length, record.ArtSize);
            Assert.Equal(parse.BodyLineCount, record.ArtLines);
            Assert.Equal(ArticleTypeClassifier.Classify(parse.HeaderBytes.Span, parse.BodyBytes.Span), record.ArtType);
        }

        private static ArticleRecord CreateAccepted(string path, string date, string messageId, string body)
        {
            var destuffed = BuildDestuffed(path, date, messageId, body);
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            return created.Record;
        }

        private static void AssertRecordInvariants(ArticleRecord record, ReadOnlyMemory<byte> destuffed, int expectedLines)
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var parse = parser.Parse(destuffed);
            Assert.True(parse.IsAccepted);
            var materialize = NntpArticleCanonicalMaterializer.Materialize(in parse, ArticlePathMode.Traverse);
            Assert.True(materialize.IsAccepted);
            Assert.NotNull(materialize.ArticleBytes);

            Assert.Equal(ArticleParseStatus.CanonicalV1, record.ParseStatus);
            Assert.Equal(record.ArtData.Length, record.ArtSize);
            Assert.Equal(materialize.ArticleBytes.Length, record.ArtSize);
            Assert.True(record.ArtData.Span.SequenceEqual(materialize.ArticleBytes));
            Assert.Equal(parse.BodyLineCount, record.ArtLines);
            Assert.Equal(expectedLines, record.ArtLines);
            Assert.Equal(ArticleId.FromMessageId(record.MessageId), record.ArtId);
            Assert.Equal(ArticleTypeClassifier.Classify(parse.HeaderBytes.Span, parse.BodyBytes.Span), record.ArtType);
            Assert.True(record.Fields.MessageId.IsPresent);
            Assert.True(record.Fields.Newsgroups.IsPresent);
            Assert.True(record.Fields.Date.IsPresent);
            Assert.True(record.Fields.MessageId.Offset + record.Fields.MessageId.Length <= record.ArtSize);
            Assert.True(record.ArtData.Span.Slice(record.Fields.MessageId.Offset, record.Fields.MessageId.Length)
                .SequenceEqual(record.MessageId));
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        private static byte[] BuildDestuffed(string path, string date, string messageId, string body)
        {
            return Encoding.ASCII.GetBytes(
                "Path: " + path + "\r\n" +
                "Date: " + date + "\r\n" +
                "Message-ID: " + messageId + "\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: hash\r\n" +
                "\r\n" +
                body);
        }

        private static byte[] BuildDestuffedWithoutPath(string date, string messageId, string body)
        {
            return Encoding.ASCII.GetBytes(
                "Date: " + date + "\r\n" +
                "Message-ID: " + messageId + "\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: hash\r\n" +
                "\r\n" +
                body);
        }
    }
}
