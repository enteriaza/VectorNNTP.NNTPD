using System.Security.Cryptography;
using System.Text;
using VectorNNTP.BackFiller.Retention;

namespace VectorNNTP.BackFiller.Tests.Retention;

public sealed class ArticleIdentityTests
{
    [Fact]
    public void Known_message_id_vectors_are_lowercase_md5_of_exact_ascii_bytes()
    {
        Assert.Equal("30edc94157aa16fe644a45a1f1ffe160", ArticleIdentity.FromExactMessageId("<12345@example.invalid>").Md5Hex);
        Assert.Equal("de438dc83d64b1fa9206cf4da9eed5cc", ArticleIdentity.FromExactMessageId("<abc@example.invalid>").Md5Hex);
    }

    [Fact]
    public void Hash_uses_ascii_bytes_without_normalization()
    {
        const string messageId = "<AbC@Example.INVALID>";
        var expected = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(messageId))).ToLowerInvariant();
        var identity = ArticleIdentity.FromExactMessageId(messageId);
        Assert.Equal(messageId, identity.MessageId);
        Assert.Equal(expected, identity.Md5Hex);
        Assert.Equal(32, identity.Md5Hex.Length);
        Assert.Equal(identity.Md5Hex, identity.Md5Hex.ToLowerInvariant());
    }

    [Fact]
    public void Case_brackets_and_whitespace_are_not_normalized()
    {
        var canonical = ArticleIdentity.FromExactMessageId("<abc@example.invalid>");
        Assert.NotEqual(canonical.Md5Hex, ArticleIdentity.FromExactMessageId("<ABC@example.invalid>").Md5Hex);
        Assert.NotEqual(canonical.Md5Hex, ArticleIdentity.FromExactMessageId("abc@example.invalid").Md5Hex);
        Assert.NotEqual(canonical.Md5Hex, ArticleIdentity.FromExactMessageId(" <abc@example.invalid>").Md5Hex);
        Assert.NotEqual(canonical.Md5Hex, ArticleIdentity.FromExactMessageId("<abc@example.invalid> ").Md5Hex);
        Assert.Equal("983057a19b5437d0330045ac8c546d67", ArticleIdentity.FromExactMessageId("<ABC@example.invalid>").Md5Hex);
        Assert.Equal("a7ee85c34e58bc015f147f7c2bfbe85c", ArticleIdentity.FromExactMessageId("abc@example.invalid").Md5Hex);
    }
}
