using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.OverviewDb;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.Common.Tests.Articles.OverviewDb
{
    /// <summary>OverviewDB protobuf payload contracts produced from CanonicalV1 ArticleRecord.</summary>
    public sealed class OverviewArticleV1CodecTests
    {
        private const string LocalFqdn = "nntpd01.usenet.ninja";

        [Fact]
        public void Encode_ContainsArticleIdAndClearTextMessageId_AsDistinctFields()
        {
            var record = CreateRecord(
                Destuffed(
                    "<id@example.test>",
                    body: UniqueBody,
                    newsgroups: "alt.test,rec.test",
                    subject: "overview-subject",
                    from: "poster@example.test",
                    references: "<prev@example.test>"));

            var payload = OverviewArticleV1Codec.Encode(record);
            var decoded = OverviewArticleV1Codec.Decode(payload);

            Span<byte> artId = stackalloc byte[ArticleId.Length];
            record.ArtId.CopyTo(artId);
            Assert.Equal(OverviewArticleV1.CurrentSchemaVersion, decoded.SchemaVersion);
            Assert.Equal(artId.ToArray(), decoded.ArticleId);
            Assert.Equal("<id@example.test>", decoded.MessageId);
            Assert.NotEqual(Encoding.UTF8.GetBytes(decoded.MessageId), decoded.ArticleId);
            Assert.Equal(32, decoded.ArticleId.Length);
            Assert.StartsWith("<", decoded.MessageId, StringComparison.Ordinal);
            Assert.EndsWith(">", decoded.MessageId, StringComparison.Ordinal);
        }

        [Fact]
        public void Encode_ContainsCanonicalOverviewFieldsAndAllNewsgroups()
        {
            var record = CreateRecord(
                Destuffed(
                    "<multi@example.test>",
                    body: UniqueBody,
                    newsgroups: "alt.test, rec.test ,comp.test",
                    subject: "overview-subject",
                    from: "poster@example.test",
                    references: "<prev@example.test>"));

            var payload = OverviewArticleV1Codec.Encode(record);
            var decoded = OverviewArticleV1Codec.Decode(payload);

            Assert.Equal("overview-subject", decoded.Subject);
            Assert.Equal("poster@example.test", decoded.From);
            Assert.Equal(Encoding.ASCII.GetString(record.Date), decoded.Date);
            Assert.Equal("<prev@example.test>", decoded.References);
            Assert.Equal((uint)record.ArtSize, decoded.Bytes);
            Assert.Equal((uint)record.ArtLines, decoded.Lines);
            Assert.Equal(["alt.test", "rec.test", "comp.test"], decoded.Newsgroups);
            Assert.Equal(record.ArtSize, (int)decoded.Bytes);
            Assert.Equal(record.ArtLines, (int)decoded.Lines);
        }

        [Fact]
        public void Encode_DoesNotContainArticleBody_AndRemainsCompact()
        {
            var record = CreateRecord(
                Destuffed(
                    "<body@example.test>",
                    body: UniqueBody,
                    newsgroups: "alt.test"));

            var payload = OverviewArticleV1Codec.Encode(record);

            Assert.False(OverviewArticleV1Codec.ContainsCompleteArticle(payload, record.ArtData.Span));
            Assert.DoesNotContain(Encoding.ASCII.GetBytes("UNIQUE-OVERVIEW-BODY-TOKEN"), payload);
            Assert.Equal(341, record.ArtSize);
            Assert.Equal(140, payload.Length);
        }

        [Fact]
        public void Encode_PreservesNewsgroupOrder_FromCanonicalHeader()
        {
            var record = CreateRecord(
                Destuffed(
                    "<order@example.test>",
                    newsgroups: "group.z,group.a,group.m"));

            var decoded = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(record));
            Assert.Equal(["group.z", "group.a", "group.m"], decoded.Newsgroups);
        }

        [Fact]
        public void Encode_DateIsCanonicalHeaderValue_NotCanonicalUtcDateTime()
        {
            var destuffed =
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0200\r\n" +
                "Message-ID: <date-semantics@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: date-semantics\r\n" +
                "\r\n" +
                "body\r\n";
            var record = CreateRecord(destuffed);
            var payload = OverviewArticleV1Codec.Encode(record);
            var decoded = OverviewArticleV1Codec.Decode(payload);

            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), record.CanonicalUtc);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", Encoding.ASCII.GetString(record.Date));
            Assert.Equal(Encoding.ASCII.GetString(record.Date), decoded.Date);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", decoded.Date);
            Assert.NotEqual(record.CanonicalUtc.ToString("O"), decoded.Date);
            Assert.DoesNotContain("07:30:10", decoded.Date, StringComparison.Ordinal);
            Assert.DoesNotContain("+0200", decoded.Date, StringComparison.Ordinal);
        }

        [Fact]
        public void Encode_Newsgroups_AreExactTokensInSourceOrder()
        {
            var record = CreateRecord(
                Destuffed(
                    "<groups@example.test>",
                    newsgroups: "group.one, group.two, group.three"));

            var decoded = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(record));
            Assert.Equal(["group.one", "group.two", "group.three"], decoded.Newsgroups);
            Assert.Equal("group.one, group.two, group.three", Encoding.ASCII.GetString(record.Newsgroups));
        }

        [Fact]
        public void Encode_MissingReferences_IsEmptyRatherThanInvented()
        {
            var record = CreateRecord(
                Destuffed(
                    "<noref@example.test>",
                    newsgroups: "alt.test"));

            Assert.False(record.Fields.References.IsPresent);
            Assert.True(record.References.IsEmpty);
            var decoded = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(record));
            Assert.Equal(string.Empty, decoded.References);
            Assert.Equal("ingress-test", decoded.Subject);
            Assert.Equal("user@example.test", decoded.From);
        }

        [Fact]
        public void EncodeDecode_RoundTrip_IsEquivalent()
        {
            var record = CreateRecord(
                Destuffed(
                    "<roundtrip@example.test>",
                    body: UniqueBody,
                    newsgroups: "alt.test, rec.test",
                    subject: "overview-subject",
                    from: "poster@example.test",
                    references: "<prev@example.test>"));

            var first = OverviewArticleV1Codec.Encode(record);
            var decoded = OverviewArticleV1Codec.Decode(first);
            var second = OverviewArticleV1Codec.Encode(record);

            Assert.Equal(first, second);
            Assert.Equal(OverviewArticleV1.CurrentSchemaVersion, decoded.SchemaVersion);
            Assert.Equal("<roundtrip@example.test>", decoded.MessageId);
            Assert.Equal(["alt.test", "rec.test"], decoded.Newsgroups);
            Assert.Equal("overview-subject", decoded.Subject);
            Assert.Equal("poster@example.test", decoded.From);
            Assert.Equal(Encoding.ASCII.GetString(record.Date), decoded.Date);
            Assert.Equal("<prev@example.test>", decoded.References);
            Assert.Equal((uint)record.ArtSize, decoded.Bytes);
            Assert.Equal((uint)record.ArtLines, decoded.Lines);

            Span<byte> artId = stackalloc byte[ArticleId.Length];
            record.ArtId.CopyTo(artId);
            Assert.Equal(artId.ToArray(), decoded.ArticleId);
            Assert.Equal(decoded.ArticleId, OverviewArticleV1Codec.Decode(second).ArticleId);
        }

        [Fact]
        public void FoldedNewsgroups_AreRejectedByParser_BeforeOverviewEncode()
        {
            var destuffed =
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <folded-ng@example.test>\r\n" +
                "Newsgroups: group.one, group.two,\r\n" +
                " group.three\r\n" +
                "From: user@example.test\r\n" +
                "Subject: folded-newsgroups\r\n" +
                "\r\n" +
                "body\r\n";
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(LocalFqdn),
                Encoding.ASCII.GetBytes(destuffed));

            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.InvalidNewsgroups, created.ParseFailure);
        }

        private const string UniqueBody = "UNIQUE-OVERVIEW-BODY-TOKEN\r\n" +
            "line2-padding-padding-padding-padding-padding-padding\r\n" +
            "line3-padding-padding-padding-padding-padding-padding\r\n";

        private static string Destuffed(
            string messageId,
            string body = "body\r\n",
            string newsgroups = "alt.test",
            string subject = "ingress-test",
            string from = "user@example.test",
            string? references = null)
        {
            var referencesHeader = string.IsNullOrEmpty(references)
                ? string.Empty
                : "References: " + references + "\r\n";
            return
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: " + messageId + "\r\n" +
                "Newsgroups: " + newsgroups + "\r\n" +
                "From: " + from + "\r\n" +
                "Subject: " + subject + "\r\n" +
                referencesHeader +
                "\r\n" +
                body;
        }

        private static ArticleRecord CreateRecord(string destuffed)
        {
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(LocalFqdn),
                Encoding.ASCII.GetBytes(destuffed));
            Assert.True(created.IsAccepted);
            return created.Record;
        }
    }
}
