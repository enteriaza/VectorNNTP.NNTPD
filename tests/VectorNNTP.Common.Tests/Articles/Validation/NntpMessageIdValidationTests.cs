using System.Text;
using VectorNNTP.Common.Articles.Validation;

namespace VectorNNTP.Common.Tests.Articles.Validation
{
    public sealed class NntpMessageIdValidationTests
    {
        [Fact]
        public void MessageIdValidation_WhenRepresentativeValidForms_ReturnsTrue()
        {
            Assert.True(NntpMessageIdValidation.IsValidMessageId("<abc@example.com>"));
            Assert.True(NntpMessageIdValidation.IsValidMessageId("<part.one+tag@news-server.example.net>"));
            Assert.True(NntpMessageIdValidation.IsValidMessageId("<name@[127.0.0.1]>"));
        }

        [Fact]
        public void MessageIdValidation_WhenRepresentativeInvalidForms_ReturnsFalse()
        {
            Assert.False(NntpMessageIdValidation.IsValidMessageId("abc@example.com"));
            Assert.False(NntpMessageIdValidation.IsValidMessageId("<double..dot@example.com>"));
            Assert.False(NntpMessageIdValidation.IsValidMessageId("<\"quoted\"@example.com>"));
            Assert.False(NntpMessageIdValidation.IsValidMessageId("<toolong" + new string('a', 400) + "@example.com>"));
        }

        [Fact]
        public void Schema_vectors_match_the_old_runtime_validator()
        {
            string[] valid =
            [
                "<12345@example.invalid>",
                "<abc.def+tag@example.invalid>",
                "<a@b>",
                "<abc@[127.0.0.1]>",
                "<abc@[IPv6:2001:db8::1]>",
                "<abc@[x@y]>",
                BuildMessageIdWithTotalLength(250),
            ];

            string[] invalid =
            [
                "not-message-id",
                string.Empty,
                "   ",
                "<double..dot@example.com>",
                "<\"quoted\"@example.com>",
                "<nodomain@>",
                "<@example.com>",
                "<abc@example..com>",
                "<abc@exa mple.com>",
                "<abc@@example.com>",
                "<abc@[127.0.0.1>",
                "<abc@[]>",
                "abc@example.com",
                "<abc@example.com",
                "abc@example.com>",
                BuildMessageIdWithTotalLength(251),
            ];

            foreach (var messageId in valid)
            {
                Assert.True(NntpMessageIdValidation.IsValidMessageId(messageId), messageId);
                Assert.True(NntpMessageIdValidation.IsValidMessageId(Encoding.ASCII.GetBytes(messageId)), messageId);
            }

            foreach (var messageId in invalid)
            {
                Assert.False(NntpMessageIdValidation.IsValidMessageId(messageId), messageId);
                Assert.False(NntpMessageIdValidation.IsValidMessageId(Encoding.ASCII.GetBytes(messageId)), messageId);
            }
        }

        [Fact]
        public void Byte_and_char_overloads_agree_on_ascii_tokens()
        {
            const string token = "<part.one+tag@news-server.example.net>";
            Assert.Equal(
                NntpMessageIdValidation.IsValidMessageId(token.AsSpan()),
                NntpMessageIdValidation.IsValidMessageId(Encoding.ASCII.GetBytes(token)));
        }

        [Fact]
        public void Non_ascii_bytes_are_rejected_without_decoding()
        {
            var bytes = "<abc@exámple.com>"u8.ToArray();
            Assert.False(NntpMessageIdValidation.IsValidMessageId(bytes));
        }

        [Fact]
        public void Strip_spaces_trims_only_when_requested()
        {
            const string padded = "  <abc@example.com>  ";
            Assert.False(NntpMessageIdValidation.IsValidMessageId(padded));
            Assert.True(NntpMessageIdValidation.IsValidMessageId(padded, stripSpaces: true));
            Assert.True(NntpMessageIdValidation.IsValidMessageId(Encoding.ASCII.GetBytes(padded), stripSpaces: true));
        }

        private static string BuildMessageIdWithTotalLength(int totalLength)
        {
            const string domainPart = "@example.invalid>";
            var localLength = totalLength - 1 - domainPart.Length;
            return $"<{new string('a', localLength)}{domainPart}";
        }
    }
}
