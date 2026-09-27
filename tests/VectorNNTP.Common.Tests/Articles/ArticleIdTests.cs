using System.Buffers.Binary;
using Blake3;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.Common.Tests.Articles;

public sealed class ArticleIdTests
{
    private static readonly byte[] ExampleId = "<id@example>"u8.ToArray();

    [Fact]
    public void FromMessageId_IsDeterministicAndIncludesAngleBrackets()
    {
        var first = ArticleId.FromMessageId(ExampleId);
        var second = ArticleId.FromMessageId("<id@example>"u8);
        Assert.Equal(first, second);

        Span<byte> bytes = stackalloc byte[ArticleId.Length];
        first.CopyTo(bytes);
        var expected = Hasher.Hash(ExampleId);
        Assert.True(bytes.SequenceEqual(expected.AsSpan()));
    }

    [Fact]
    public void FromMessageId_HashesValueBytes_NotFieldName()
    {
        var valueOnly = ArticleId.FromMessageId(ExampleId);
        var includingFieldName = ArticleId.FromMessageId("Message-ID: <id@example>"u8);
        Assert.NotEqual(valueOnly, includingFieldName);
        Assert.Equal(ArticleId.FromMessageId("<id@example>"u8), valueOnly);
    }

    [Fact]
    public void FromMessageId_DoesNotTrimOrCaseFold()
    {
        var exact = ArticleId.FromMessageId("<Id@Example>"u8);
        var folded = ArticleId.FromMessageId("<id@example>"u8);
        var padded = ArticleId.FromMessageId(" <id@example> "u8);
        Assert.NotEqual(exact, folded);
        Assert.NotEqual(folded, padded);
    }

    [Fact]
    public void FromMessageId_KnownDigest_MatchesBlake3OfExactBytes()
    {
        var id = ArticleId.FromMessageId(ExampleId);
        Span<byte> actual = stackalloc byte[ArticleId.Length];
        id.CopyTo(actual);
        var expected = Hasher.Hash(ExampleId);
        Assert.True(actual.SequenceEqual(expected.AsSpan()));
        Assert.Equal(
            "CFC44E7F244E2AAB6D1E7FF93F9FB93C5DDE9DAFAA16F6DB2FBBAE712881D250",
            Convert.ToHexString(actual));
        Assert.Equal(ArticleId.Length, actual.Length);
    }

    [Fact]
    public void ValueType_DoesNotAllocateAfterWarmup()
    {
        _ = ArticleId.FromMessageId(ExampleId);
        _ = ArticleId.FromMessageId(ExampleId);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var id = ArticleId.FromMessageId(ExampleId);
        Span<byte> destination = stackalloc byte[ArticleId.Length];
        id.CopyTo(destination);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(id, ArticleId.FromSpan(destination));
    }

    [Fact]
    public void FromSpan_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => ArticleId.FromSpan([1, 2, 3]));
    }

    [Fact]
    public void LittleEndianWords_RoundTrip()
    {
        var id = ArticleId.FromMessageId(ExampleId);
        Span<byte> bytes = stackalloc byte[ArticleId.Length];
        id.CopyTo(bytes);
        Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        Assert.Equal(id, ArticleId.FromSpan(bytes));
    }
}
