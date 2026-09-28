using System.Text;
using VectorNNTP.NNTPD.ArticleIngestion;

namespace VectorNNTP.NNTPD.Tests.ArticleIngestion;

/// <summary>Exact INN <c>news</c> line format from innd(8) LOGGING / <c>ARTlog</c>.</summary>
public sealed class NewsLogLineFormatterTests
{
    [Fact]
    public void Write_Accepted_NamedPeer_UsesInboundFeed_Size_AndOutboundPlaceholder()
    {
        var timestamp = new DateTimeOffset(2026, 9, 27, 0, 4, 4, 293, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Accepted,
            "<bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid>"u8.ToArray(),
            "BlueWorldHosting"u8.ToArray(),
            timestamp: timestamp,
            size: 1584);
        var line = Format(in evt, timestamp);
        Assert.Equal(
            "Sep 27 00:04:04.293 + BlueWorldHosting <bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid> 1584 ?\n",
            line);
        Assert.DoesNotContain(" + ? ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Junk_NamedPeer_KeepsOutboundPlaceholder_ThenReason()
    {
        var timestamp = new DateTimeOffset(2026, 9, 27, 0, 4, 4, 293, TimeSpan.Zero);
        var id = "<bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid>"u8.ToArray();
        var withoutReason = new NewsLogEvent(
            NewsLogDisposition.Junk,
            id,
            "BlueWorldHosting"u8.ToArray(),
            timestamp: timestamp,
            size: 1584);
        Assert.Equal(
            "Sep 27 00:04:04.293 j BlueWorldHosting <bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid> 1584 ?\n",
            Format(in withoutReason, timestamp));

        var withReason = new NewsLogEvent(
            NewsLogDisposition.Junk,
            id,
            "BlueWorldHosting"u8.ToArray(),
            timestamp: timestamp,
            reason: "newsgroup not carried: unknown.un.carried"u8.ToArray(),
            size: 1584);
        var line = Format(in withReason, timestamp);
        Assert.Equal(
            "Sep 27 00:04:04.293 j BlueWorldHosting <bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid> 1584 ? newsgroup not carried: unknown.un.carried\n",
            line);
        var outbound = line.IndexOf(" 1584 ?", StringComparison.Ordinal);
        var msgid = line.IndexOf("<bdp-backfill-", StringComparison.Ordinal);
        Assert.True(outbound > msgid);
    }

    [Fact]
    public void Write_Rejected_NamedPeer_PreservesPeerSizePlaceholder_ThenReason()
    {
        var timestamp = new DateTimeOffset(2026, 9, 27, 0, 4, 4, 293, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Rejected,
            "<bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid>"u8.ToArray(),
            "BlueWorldHosting"u8.ToArray(),
            timestamp: timestamp,
            responseCode: 439,
            reason: "yEncoding invalid"u8.ToArray(),
            size: 1584);
        var line = Format(in evt, timestamp);
        Assert.Equal(
            "Sep 27 00:04:04.293 - BlueWorldHosting <bdp-backfill-53301945c0f74ac3afc1edda885d2294@livingmemorybear.invalid> 1584 ? yEncoding invalid\n",
            line);
        Assert.DoesNotContain("439", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_IsSelfContained_DoesNotRequireSessionOrConnection()
    {
        var timestamp = new DateTimeOffset(2026, 9, 27, 0, 4, 4, 293, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Accepted,
            "<id@example.com>"u8.ToArray(),
            "BlueWorldHosting"u8.ToArray(),
            timestamp: timestamp,
            size: 1584);
        Assert.Equal(
            "Sep 27 00:04:04.293 + BlueWorldHosting <id@example.com> 1584 ?\n",
            Format(in evt, timestamp));
    }

    [Fact]
    public void Write_Accepted_MatchesInnFieldOrder_FeedThenMessageId()
    {
        var timestamp = new DateTimeOffset(2024, 8, 25, 13, 37, 41, 839, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Accepted,
            "<cancel.4066@foo.com>"u8.ToArray(),
            "?"u8.ToArray(),
            timestamp: timestamp);
        var line = Format(in evt, timestamp);
        Assert.Equal("Aug 25 13:37:41.839 + ? <cancel.4066@foo.com> 0 ?\n", line);
    }

    [Fact]
    public void Write_Junk_WithoutReason_DoesNotInventOne()
    {
        var timestamp = new DateTimeOffset(2024, 1, 5, 0, 0, 0, 0, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Junk,
            "<AbC@Example.COM>"u8.ToArray(),
            timestamp: timestamp);
        var line = Format(in evt, timestamp);
        Assert.Equal("Jan  5 00:00:00.000 j ? <AbC@Example.COM> 0 ?\n", line);
        Assert.DoesNotContain("newsgroup not carried", line, StringComparison.Ordinal);
        Assert.DoesNotContain("peer-only", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" SITE", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Junk_SerializesSuppliedReason_InInnFieldOrder()
    {
        var timestamp = new DateTimeOffset(2024, 1, 5, 0, 0, 0, 0, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Junk,
            "<AbC@Example.COM>"u8.ToArray(),
            timestamp: timestamp,
            reason: "newsgroup not carried: alt.example.foo"u8.ToArray());
        var line = Format(in evt, timestamp);
        Assert.Equal("Jan  5 00:00:00.000 j ? <AbC@Example.COM> 0 ? newsgroup not carried: alt.example.foo\n", line);
        Assert.DoesNotContain("437", line, StringComparison.Ordinal);
        Assert.DoesNotContain("439", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Junk_PeerOnly_SerializesSuppliedReason()
    {
        var timestamp = new DateTimeOffset(2024, 1, 5, 0, 0, 0, 0, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Junk,
            "<peer@example.com>"u8.ToArray(),
            timestamp: timestamp,
            reason: "peer-only: alt.example.foo"u8.ToArray());
        Assert.Equal(
            "Jan  5 00:00:00.000 j ? <peer@example.com> 0 ? peer-only: alt.example.foo\n",
            Format(in evt, timestamp));
    }

    [Fact]
    public void Write_Junk_DoesNotDeriveGroupNames()
    {
        var timestamp = new DateTimeOffset(2024, 1, 5, 0, 0, 0, 0, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Junk,
            "<id@example.com>"u8.ToArray(),
            timestamp: timestamp,
            reason: "newsgroup not carried: supplied.group"u8.ToArray());
        var line = Format(in evt, timestamp);
        Assert.Equal("Jan  5 00:00:00.000 j ? <id@example.com> 0 ? newsgroup not carried: supplied.group\n", line);
        Assert.DoesNotContain("Newsgroups", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Rejected_SerializesSuppliedUncarriedGroupReason()
    {
        var timestamp = new DateTimeOffset(2024, 1, 5, 0, 0, 0, 0, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Rejected,
            "<id@example.com>"u8.ToArray(),
            timestamp: timestamp,
            responseCode: 437,
            reason: "newsgroup not carried: alt.example.foo"u8.ToArray());
        var line = Format(in evt, timestamp);
        Assert.Equal("Jan  5 00:00:00.000 - ? <id@example.com> 0 ? newsgroup not carried: alt.example.foo\n", line);
        Assert.DoesNotContain("437", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Moderated_UsesM_NotPlusOrUnknown()
    {
        var timestamp = new DateTimeOffset(2024, 8, 25, 13, 37, 41, 839, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Moderated,
            "<mod@example.com>"u8.ToArray(),
            timestamp: timestamp);
        var line = Format(in evt, timestamp);
        Assert.Equal("Aug 25 13:37:41.839 m ? <mod@example.com> 0 ?\n", line);
        Assert.DoesNotContain(" + ", line, StringComparison.Ordinal);
        Assert.Equal((byte)'m', (byte)NewsLogDisposition.Moderated);
        Assert.NotEqual((byte)'?', (byte)NewsLogDisposition.Moderated);
    }

    [Fact]
    public void Write_RejectedYEncoding_OmitsResponseCode_AndUsesInnFieldOrder()
    {
        var timestamp = new DateTimeOffset(2024, 8, 25, 13, 37, 54, 638, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Rejected,
            "<23k82@bar.net>"u8.ToArray(),
            timestamp: timestamp,
            responseCode: 439,
            reason: "yEncoding invalid"u8.ToArray());
        var line = Format(in evt, timestamp);
        Assert.Equal("Aug 25 13:37:54.638 - ? <23k82@bar.net> 0 ? yEncoding invalid\n", line);
        Assert.DoesNotContain("439", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" SITE", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Rejected_DoesNotRenderResponseCode()
    {
        var timestamp = new DateTimeOffset(2024, 8, 25, 13, 37, 54, 638, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Rejected,
            "<id@example.com>"u8.ToArray(),
            timestamp: timestamp,
            responseCode: 437,
            reason: "article too large"u8.ToArray());
        var line = Format(in evt, timestamp);
        Assert.Equal("Aug 25 13:37:54.638 - ? <id@example.com> 0 ? article too large\n", line);
        Assert.DoesNotContain("437", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_FutureSites_AreAppendedWithoutInventingThem()
    {
        var timestamp = new DateTimeOffset(2024, 8, 25, 13, 37, 41, 839, TimeSpan.Zero);
        var evt = new NewsLogEvent(
            NewsLogDisposition.Accepted,
            "<id@example.com>"u8.ToArray(),
            "news.server.fr"u8.ToArray(),
            "a.peer other.server.org"u8.ToArray(),
            timestamp);
        var line = Format(in evt, timestamp);
        Assert.Equal(
            "Aug 25 13:37:41.839 + news.server.fr <id@example.com> 0 a.peer other.server.org\n",
            line);
    }

    private static string Format(in NewsLogEvent evt, DateTimeOffset timestamp)
    {
        Span<byte> buffer = stackalloc byte[NewsLogLineFormatter.RequiredLength(in evt) + 16];
        var written = NewsLogLineFormatter.Write(buffer, in evt, timestamp);
        return Encoding.ASCII.GetString(buffer[..written]);
    }
}
