using System.Text;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.Common.Tests.Articles.Parsing;

public sealed class ArticlePathCanonicalizerTests
{
    private const string Tracker = "news.usenet.ninja";
    private static ReadOnlySpan<byte> LocalIdentity => "bf01.usenet.ninja"u8;

    [Fact]
    public void TryAnalyze_WhenMissing_ClassifiesMissing()
    {
        Assert.True(ArticlePathCanonicalizer.TryAnalyze(
            [],
            LocalIdentity,
            pathPresent: false,
            out var kind,
            out var containsTracker,
            out var failure));
        Assert.Equal(ArticlePathKind.Missing, kind);
        Assert.False(containsTracker);
        Assert.Equal(NntpArticleParseFailureCode.None, failure);
    }

    [Fact]
    public void TryAnalyze_WhenOnlySeparators_ClassifiesEmpty()
    {
        Assert.True(ArticlePathCanonicalizer.TryAnalyze(
            "!!!  "u8,
            LocalIdentity,
            pathPresent: true,
            out var kind,
            out var containsTracker,
            out _));
        Assert.Equal(ArticlePathKind.Empty, kind);
        Assert.False(containsTracker);
    }

    [Fact]
    public void TryWriteCanonicalPath_WhenNeedsPrependAndTrackerAbsent_WritesTrackerThenLocalIdentity()
    {
        Assert.True(ArticlePathCanonicalizer.TryAnalyze(
            "foo!bar!"u8,
            LocalIdentity,
            pathPresent: true,
            out var kind,
            out var containsTracker,
            out _));
        Assert.Equal(ArticlePathKind.NeedsLocalIdentityPrepend, kind);
        Assert.False(containsTracker);

        Span<byte> destination = stackalloc byte[80];
        Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
            "foo!bar!"u8,
            LocalIdentity,
            kind,
            containsTracker,
            ArticlePathMode.Traverse,
            destination,
            out var written));
        Assert.True(destination[..written].SequenceEqual("news.usenet.ninja!bf01.usenet.ninja!foo!bar"u8));
        Assert.Equal(1, CountToken(destination[..written], "news.usenet.ninja"u8));
    }

    [Fact]
    public void TryWriteCanonicalPath_WhenTrackerAlreadyFirst_DoesNotDuplicateTracker()
    {
        WriteAndAssert(
            "news.usenet.ninja!foo!",
            "bf01.usenet.ninja",
            "bf01.usenet.ninja!news.usenet.ninja!foo");
    }

    [Fact]
    public void TryWriteCanonicalPath_WhenTrackerOccursLater_DoesNotDuplicateTracker()
    {
        WriteAndAssert(
            "foo!news.usenet.ninja!bar!",
            "bf01.usenet.ninja",
            "bf01.usenet.ninja!foo!news.usenet.ninja!bar");
    }

    [Fact]
    public void TryAnalyze_WhenTrackerIdentityDiffersOnlyByCase_DetectsTracker()
    {
        Assert.True(ArticlePathCanonicalizer.TryAnalyze(
            "NEWS.USENET.NINJA!foo"u8,
            LocalIdentity,
            pathPresent: true,
            out var kind,
            out var containsTracker,
            out _));
        Assert.Equal(ArticlePathKind.NeedsLocalIdentityPrepend, kind);
        Assert.True(containsTracker);

        Span<byte> destination = stackalloc byte[80];
        Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
            "NEWS.USENET.NINJA!foo"u8,
            LocalIdentity,
            kind,
            containsTracker,
            ArticlePathMode.Traverse,
            destination,
            out var written));
        Assert.True(destination[..written].SequenceEqual("bf01.usenet.ninja!NEWS.USENET.NINJA!foo"u8));
        Assert.Equal(1, CountToken(destination[..written], "NEWS.USENET.NINJA"u8));
    }

    [Fact]
    public void TryAnalyze_WhenTrackerIsOnlyASubstringOfAToken_DoesNotCountAsTracker()
    {
        Assert.True(ArticlePathCanonicalizer.TryAnalyze(
            "news.usenet.ninja.extra!foo"u8,
            LocalIdentity,
            pathPresent: true,
            out var kind,
            out var containsTracker,
            out _));
        Assert.Equal(ArticlePathKind.NeedsLocalIdentityPrepend, kind);
        Assert.False(containsTracker);

        Span<byte> destination = stackalloc byte[96];
        Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
            "news.usenet.ninja.extra!foo"u8,
            LocalIdentity,
            kind,
            containsTracker,
            ArticlePathMode.Traverse,
            destination,
            out var written));
        Assert.True(destination[..written].SequenceEqual("news.usenet.ninja!bf01.usenet.ninja!news.usenet.ninja.extra!foo"u8));
        Assert.Equal(1, CountToken(destination[..written], "news.usenet.ninja"u8));
    }

    [Fact]
    public void TryWriteCanonicalPath_WhenLocalIdentityIsLeftmost_PrependsAgain()
    {
        WriteAndAssert(
            "bf01.usenet.ninja!news.example.org",
            "bf01.usenet.ninja",
            "news.usenet.ninja!bf01.usenet.ninja!bf01.usenet.ninja!news.example.org");
    }

    [Fact]
    public void TryWriteCanonicalPath_FirstBackFillerTraversal_InsertsTrackerThenApplication()
    {
        WriteAndAssert(
            "news.example.org!feed2",
            "backfiller01.usenet.ninja",
            "news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2");
    }

    [Fact]
    public void TryWriteCanonicalPath_SubsequentStorageTraversal_PrependsApplicationBeforeExistingTracker()
    {
        WriteAndAssert(
            "news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2",
            "storage01.usenet.ninja",
            "storage01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2");
    }

    [Fact]
    public void TryWriteCanonicalPath_SubsequentNntpdTraversal_PrependsApplicationAndKeepsSingleTracker()
    {
        WriteAndAssert(
            "storage01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2",
            "nntpd01.usenet.ninja",
            "nntpd01.usenet.ninja!storage01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2");
        Assert.Equal(1, CountToken(
            "nntpd01.usenet.ninja!storage01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2"u8,
            "news.usenet.ninja"u8));
    }

    [Fact]
    public void TryWriteCanonicalPath_WhenMissing_WritesTrackerAndLocalIdentity()
    {
        Assert.True(ArticlePathCanonicalizer.TryAnalyze(
            [],
            LocalIdentity,
            pathPresent: false,
            out var kind,
            out var containsTracker,
            out _));

        Span<byte> destination = stackalloc byte[64];
        Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
            [],
            LocalIdentity,
            kind,
            containsTracker,
            ArticlePathMode.Traverse,
            destination,
            out var written));
        Assert.True(destination[..written].SequenceEqual("news.usenet.ninja!bf01.usenet.ninja"u8));
    }

    [Fact]
    public void TryWriteCanonicalPath_WhenLocalIdentityOccursLater_PrependsAgain()
    {
        WriteAndAssert(
            "cache02.usenet.ninja!nntpd01.usenet.ninja!news.usenet.ninja",
            "nntpd01.usenet.ninja",
            "nntpd01.usenet.ninja!cache02.usenet.ninja!nntpd01.usenet.ninja!news.usenet.ninja");
    }

    [Fact]
    public void TryWriteCanonicalPath_Normalize_WhenTrackerIsPresent_DoesNotPrependApplicationHop()
    {
        WriteAndAssert(
            "cache01.usenet.ninja!news.usenet.ninja!nntpd01.usenet.ninja",
            "news.usenet.ninja",
            "cache01.usenet.ninja!news.usenet.ninja!nntpd01.usenet.ninja",
            ArticlePathMode.Normalize);
    }

    [Fact]
    public void TryWriteCanonicalPath_WhenLeftmostMatchesIgnoringCase_PrependsAgain()
    {
        WriteAndAssert(
            "NNTPD01.USENET.NINJA!cache02.usenet.ninja",
            "nntpd01.usenet.ninja",
            "news.usenet.ninja!nntpd01.usenet.ninja!NNTPD01.USENET.NINJA!cache02.usenet.ninja");
    }

    [Fact]
    public void TryWriteCanonicalPath_BackFillerThenNntpdThenCaches_KeepsNewestFirst()
    {
        var path = "peer.example";
        path = Rewrite(path, "backfiller01.usenet.ninja");
        Assert.Equal("news.usenet.ninja!backfiller01.usenet.ninja!peer.example", path);
        path = Rewrite(path, "nntpd01.usenet.ninja");
        path = Rewrite(path, "cache02.usenet.ninja");
        path = Rewrite(path, "nntpd01.usenet.ninja");
        path = Rewrite(path, "cache01.usenet.ninja");
        Assert.Equal(
            "cache01.usenet.ninja!nntpd01.usenet.ninja!cache02.usenet.ninja!nntpd01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja!peer.example",
            path);
        Assert.Equal(
            "cache01.usenet.ninja!" + path,
            Rewrite(path, "cache01.usenet.ninja"));
        Assert.Equal(path, Rewrite(path, "cache01.usenet.ninja", ArticlePathMode.Normalize));
        Assert.Equal(1, CountToken(Encoding.ASCII.GetBytes(path), "news.usenet.ninja"u8));
    }

    [Fact]
    public void TryWriteCanonicalPath_Normalize_InsertsTrackerOnceAndKeepsApplicationHops()
    {
        WriteAndAssert(
            "nntpd01.usenet.ninja!nntpd01.usenet.ninja!peer.example",
            "cache01.usenet.ninja",
            "news.usenet.ninja!nntpd01.usenet.ninja!nntpd01.usenet.ninja!peer.example",
            ArticlePathMode.Normalize);
    }

    [Fact]
    public void TryWriteCanonicalPath_Normalize_CollapsesRepeatedTrackerAndIsIdempotent()
    {
        const string raw = "news.usenet.ninja!nntpd01.usenet.ninja!NEWS.USENET.NINJA!peer.example";
        const string once = "news.usenet.ninja!nntpd01.usenet.ninja!peer.example";
        WriteAndAssert(raw, "cache01.usenet.ninja", once, ArticlePathMode.Normalize);
        WriteAndAssert(once, "cache01.usenet.ninja", once, ArticlePathMode.Normalize);
    }

    [Fact]
    public void TryWriteCanonicalPath_Traverse_DoesNotDeduplicateApplicationFqdn()
    {
        WriteAndAssert(
            "nntpd01.usenet.ninja!cache01.usenet.ninja!nntpd01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja",
            "nntpd01.usenet.ninja",
            "nntpd01.usenet.ninja!nntpd01.usenet.ninja!cache01.usenet.ninja!nntpd01.usenet.ninja!news.usenet.ninja!backfiller01.usenet.ninja");
    }

    [Fact]
    public void TryAnalyze_WhenControlBytePresent_Rejects()
    {
        Assert.False(ArticlePathCanonicalizer.TryAnalyze(
            "feed\x7f!other"u8,
            LocalIdentity,
            pathPresent: true,
            out _,
            out _,
            out var failure));
        Assert.Equal(NntpArticleParseFailureCode.InvalidPath, failure);
    }

    private static string Rewrite(string rawPath, string localIdentity, ArticlePathMode mode = ArticlePathMode.Traverse)
    {
        var raw = Encoding.ASCII.GetBytes(rawPath);
        var local = Encoding.ASCII.GetBytes(localIdentity);
        Assert.True(ArticlePathCanonicalizer.TryAnalyze(
            raw,
            local,
            pathPresent: true,
            out var kind,
            out var containsTracker,
            out _));
        Span<byte> destination = stackalloc byte[512];
        Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
            raw,
            local,
            kind,
            containsTracker,
            mode,
            destination,
            out var written));
        return Encoding.ASCII.GetString(destination[..written]);
    }

    private static void WriteAndAssert(
        string rawPath,
        string localIdentity,
        string expected,
        ArticlePathMode mode = ArticlePathMode.Traverse)
    {
        var raw = Encoding.ASCII.GetBytes(rawPath);
        var local = Encoding.ASCII.GetBytes(localIdentity);
        Assert.True(ArticlePathCanonicalizer.TryAnalyze(
            raw,
            local,
            pathPresent: true,
            out var kind,
            out var containsTracker,
            out _));

        Span<byte> destination = stackalloc byte[512];
        Assert.True(ArticlePathCanonicalizer.TryWriteCanonicalPath(
            raw,
            local,
            kind,
            containsTracker,
            mode,
            destination,
            out var written));
        Assert.Equal(expected, Encoding.ASCII.GetString(destination[..written]));
        Assert.Equal(1, CountToken(destination[..written], Encoding.ASCII.GetBytes(Tracker)));
    }

    private static int CountToken(ReadOnlySpan<byte> path, ReadOnlySpan<byte> token)
    {
        var remaining = path;
        var count = 0;
        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOf((byte)'!');
            ReadOnlySpan<byte> component;
            if (separator < 0)
            {
                component = remaining;
                remaining = default;
            }
            else
            {
                component = remaining[..separator];
                remaining = remaining[(separator + 1)..];
            }

            if (component.Length == token.Length && component.SequenceEqual(token))
            {
                count++;
            }
        }

        return count;
    }
}
