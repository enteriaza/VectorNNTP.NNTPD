using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.BackFiller.Tests.Retention;

public sealed class ArticleRetentionCanonicalTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RetainCanonical_TryOpenTransfer_happy_path()
    {
        var time = new ManualTimeProvider(Start);
        var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024);
        var prepared = CreateCanonical("<vatp-canonical@example.test>");
        var retained = authority.RetainCanonical(
            "<vatp-canonical@example.test>",
            prepared.RequestId,
            prepared.Record,
            prepared.SelectedDateHeaderName);
        Assert.Equal(ArticleRetentionKind.Retained, retained.Kind);

        var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, open.Kind);
        Assert.NotNull(open.Lease);
        Assert.Equal(prepared.Record.ArtSize, open.Lease!.Record.ArtSize);
        open.Dispose();
    }

    [Fact]
    public void Wrong_ArticleId_does_not_consume_RequestId()
    {
        var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var prepared = CreateCanonical("<wrong-id@example.test>");
        Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
            "<wrong-id@example.test>",
            prepared.RequestId,
            prepared.Record,
            prepared.SelectedDateHeaderName).Kind);

        var wrongId = ArticleId.FromMessageId("<other@example.test>"u8);
        using (var rejected = authority.TryOpenTransfer(prepared.RequestId, wrongId))
        {
            Assert.Equal(VatpOpenKind.Rejected, rejected.Kind);
        }

        using var opened = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, opened.Kind);
    }

    [Fact]
    public void Second_open_with_same_RequestId_fails()
    {
        var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var prepared = CreateCanonical("<second-open@example.test>");
        Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
            "<second-open@example.test>",
            prepared.RequestId,
            prepared.Record,
            prepared.SelectedDateHeaderName).Kind);

        using (var first = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId))
        {
            Assert.Equal(VatpOpenKind.Opened, first.Kind);
        }

        using var second = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        Assert.Equal(VatpOpenKind.Rejected, second.Kind);
    }

    [Fact]
    public void TryCancelPendingRequest_removes_open_eligibility()
    {
        var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var prepared = CreateCanonical("<cancel-pending@example.test>");
        Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
            "<cancel-pending@example.test>",
            prepared.RequestId,
            prepared.Record,
            prepared.SelectedDateHeaderName).Kind);

        Assert.True(authority.TryCancelPendingRequest(prepared.RequestId));
        using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        Assert.Equal(VatpOpenKind.Rejected, open.Kind);
        Assert.False(authority.TryCancelPendingRequest(prepared.RequestId));
    }

    [Fact]
    public void Ttl_expiry_rejects_open()
    {
        var time = new ManualTimeProvider(Start);
        var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(30));
        var prepared = CreateCanonical("<expired@example.test>");
        Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
            "<expired@example.test>",
            prepared.RequestId,
            prepared.Record,
            prepared.SelectedDateHeaderName).Kind);

        time.Advance(TimeSpan.FromSeconds(30));
        using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        Assert.Equal(VatpOpenKind.Rejected, open.Kind);
    }

    private static PreparedCanonical CreateCanonical(string messageId)
    {
        var parser = new NntpArticleParser("backfiller.test");
        var destuffed = BuildDestuffed(messageId, "body\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, destuffed);
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return new PreparedCanonical(Guid.NewGuid(), created.Record, created.SelectedDateHeaderName);
    }

    private static byte[] BuildDestuffed(string messageId, string body)
    {
        return System.Text.Encoding.ASCII.GetBytes(
            "Path: peer.example\r\n"
            + "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
            + "Message-ID: " + messageId + "\r\n"
            + "Newsgroups: alt.test\r\n"
            + "From: user@example.test\r\n"
            + "Subject: s\r\n"
            + "\r\n"
            + body);
    }

    private readonly record struct PreparedCanonical(
        Guid RequestId,
        ArticleRecord Record,
        NntpArticleHeaderName SelectedDateHeaderName);
}
