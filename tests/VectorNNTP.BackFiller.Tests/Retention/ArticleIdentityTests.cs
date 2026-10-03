using System.Text;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.BackFiller.Tests.Retention
{
    public sealed class ArticleIdentityTests
    {
        [Fact]
        public void From_uses_existing_ArticleId_without_message_id_hash()
        {
            const string messageId = "<12345@example.invalid>";
            var artId = ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId));
            var identity = ArticleIdentity.From(messageId, artId);
            Assert.Equal(messageId, identity.MessageId);
            Assert.Equal(artId.ToLowerHexString(), identity.ArticleIdHex);
            Assert.Equal(ArticleId.HexLength, identity.ArticleIdHex.Length);
            Assert.Equal("dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14", identity.ArticleIdHex);
        }

        [Fact]
        public void Distinct_message_ids_produce_distinct_article_id_hex()
        {
            var a = ArticleIdentity.From(
                "<one@example.invalid>",
                ArticleId.FromMessageId("<one@example.invalid>"u8));
            var b = ArticleIdentity.From(
                "<two@example.invalid>",
                ArticleId.FromMessageId("<two@example.invalid>"u8));
            Assert.NotEqual(a.ArticleIdHex, b.ArticleIdHex);
        }
    }
}
