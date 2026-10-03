using System.Text;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;

namespace VectorNNTP.BackFiller.Tests.Fixtures
{
    /// <summary>Builds CanonicalV1 articles for retention / VATP tests.</summary>
    internal static class RetentionTestArticles
    {
        internal static PreparedCanonical Create(string messageId, string body = "body\r\n")
        {
            var parser = new NntpArticleParser("backfiller.test");
            var destuffed = BuildDestuffed(messageId, body);
            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            return new PreparedCanonical(
                messageId,
                Guid.NewGuid(),
                created.Record,
                created.SelectedDateHeaderName,
                created.Record.ArtData.ToArray());
        }

        internal static PreparedCanonical RetainPrepared(
            ArticleRetentionAuthority authority,
            string messageId,
            string body = "body\r\n")
        {
            var prepared = Create(messageId, body);
            var result = authority.RetainCanonical(
                prepared.MessageId,
                prepared.RequestId,
                prepared.Record,
                prepared.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, result.Kind);
            return prepared;
        }

        private static byte[] BuildDestuffed(string messageId, string body) =>
            Encoding.ASCII.GetBytes(
                "Path: peer.example\r\n"
                + "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: " + messageId + "\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: s\r\n"
                + "\r\n"
                + body);

        internal readonly record struct PreparedCanonical(
            string MessageId,
            Guid RequestId,
            ArticleRecord Record,
            NntpArticleHeaderName SelectedDateHeaderName,
            byte[] ArtData);
    }
}
