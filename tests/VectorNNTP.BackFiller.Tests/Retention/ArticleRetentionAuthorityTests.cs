using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.BackFiller.Tests.Retention;

public sealed class ArticleRetentionAuthorityTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RetainCanonical_then_open_exposes_owned_artdata_and_uri()
    {
        var time = new ManualTimeProvider(Start);
        var authority = Create(time, maxBytes: 1024 * 1024);
        var prepared = RetentionTestArticles.Create("<a@b>");
        var retained = authority.RetainCanonical(
            "<a@b>",
            prepared.RequestId,
            prepared.Record,
            prepared.SelectedDateHeaderName);

        Assert.Equal(ArticleRetentionKind.Retained, retained.Kind);
        Assert.Equal("cache://backfiller.test:1190/" + retained.Identity!.Value.ArticleIdHex, retained.CacheUri);
        Assert.Equal(prepared.Record.ArtId.ToLowerHexString(), retained.Identity.Value.ArticleIdHex);
        Assert.Equal(prepared.Record.ArtSize, authority.RetainedPayloadBytes);
        Assert.Equal(1, authority.RetainedCount);

        using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, open.Kind);
        Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(prepared.ArtData));
        Assert.Equal(retained.CacheUri, open.Lease.CacheUri);
    }

    [Fact]
    public void Duplicate_message_id_is_first_wins_and_does_not_double_count()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var first = RetentionTestArticles.Create("<a@b>", "one\r\n");
        var second = RetentionTestArticles.Create("<a@b>", "two-bytes\r\n");
        Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
            "<a@b>", first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
        var duplicate = authority.RetainCanonical(
            "<a@b>", second.RequestId, second.Record, second.SelectedDateHeaderName);
        Assert.Equal(ArticleRetentionKind.AlreadyPresent, duplicate.Kind);
        Assert.Equal(first.Record.ArtSize, authority.RetainedPayloadBytes);
        Assert.Equal(1, authority.RetainedCount);

        using var open = authority.TryOpenTransfer(second.RequestId, first.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, open.Kind);
        Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(first.ArtData));
        Assert.False(open.Lease.Record.ArtData.Span.SequenceEqual(second.ArtData));
    }

    [Fact]
    public void Open_after_ttl_is_rejected_and_sweep_reclaims_bytes()
    {
        var time = new ManualTimeProvider(Start);
        var authority = Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
        var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
        time.Advance(TimeSpan.FromSeconds(10));
        using (var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId))
        {
            Assert.Equal(VatpOpenKind.Rejected, open.Kind);
        }

        Assert.Equal(0, authority.RetainedPayloadBytes);
        Assert.Equal(0, authority.SweepExpired());
        Assert.Equal(0, authority.RetainedCount);
    }

    [Fact]
    public void Sweep_does_not_remove_a_newer_generation_of_the_same_identity()
    {
        var time = new ManualTimeProvider(Start);
        var authority = Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(5));
        var old = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "old\r\n");
        time.Advance(TimeSpan.FromSeconds(5));
        using (var expired = authority.TryOpenTransfer(old.RequestId, old.Record.ArtId))
        {
            Assert.Equal(VatpOpenKind.Rejected, expired.Kind);
        }

        var newer = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "new-payload\r\n");
        Assert.Equal(0, authority.SweepExpired());
        using var open = authority.TryOpenTransfer(newer.RequestId, newer.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, open.Kind);
        Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(newer.ArtData));
    }

    [Fact]
    public void Article_exactly_at_capacity_is_retained_and_oversize_is_rejected()
    {
        var fit = RetentionTestArticles.Create("<fit@b>", "x\r\n");
        var authority = Create(new ManualTimeProvider(Start), maxBytes: fit.Record.ArtSize);
        Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
            "<fit@b>", fit.RequestId, fit.Record, fit.SelectedDateHeaderName).Kind);
        Assert.Equal(fit.Record.ArtSize, authority.RetainedPayloadBytes);

        var over = RetentionTestArticles.Create("<big@b>", "longer-body\r\n");
        Assert.True(over.Record.ArtSize > fit.Record.ArtSize);
        var rejected = authority.RetainCanonical(
            "<big@b>", over.RequestId, over.Record, over.SelectedDateHeaderName);
        Assert.Equal(ArticleRetentionKind.PayloadExceedsCapacity, rejected.Kind);
        Assert.True(rejected.IsCapacityRejected);
        Assert.Equal(fit.Record.ArtSize, authority.RetainedPayloadBytes);
    }

    [Fact]
    public void Insufficient_remaining_capacity_evicts_oldest_then_admits()
    {
        var one = RetentionTestArticles.Create("<one@b>", "11111\r\n");
        var two = RetentionTestArticles.Create("<two@b>", "22222\r\n");
        var three = RetentionTestArticles.Create("<three@b>", "333\r\n");
        var maxBytes = one.Record.ArtSize + two.Record.ArtSize;
        var authority = Create(new ManualTimeProvider(Start), maxBytes: maxBytes);
        Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
            "<one@b>", one.RequestId, one.Record, one.SelectedDateHeaderName).Kind);
        Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
            "<two@b>", two.RequestId, two.Record, two.SelectedDateHeaderName).Kind);
        var third = authority.RetainCanonical(
            "<three@b>", three.RequestId, three.Record, three.SelectedDateHeaderName);
        Assert.Equal(ArticleRetentionKind.Retained, third.Kind);
        using (var missing = authority.TryOpenTransfer(one.RequestId, one.Record.ArtId))
        {
            Assert.Equal(VatpOpenKind.Rejected, missing.Kind);
        }

        Assert.Equal(two.Record.ArtSize + three.Record.ArtSize, authority.RetainedPayloadBytes);
    }

    [Fact]
    public void Shutdown_rejects_insert_and_existing_open_still_works_until_dispose()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var keep = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
        authority.BeginShutdown();
        var later = RetentionTestArticles.Create("<b@c>");
        Assert.Equal(ArticleRetentionKind.ShuttingDown, authority.RetainCanonical(
            "<b@c>", later.RequestId, later.Record, later.SelectedDateHeaderName).Kind);
        using var open = authority.TryOpenTransfer(keep.RequestId, keep.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, open.Kind);
    }

    [Fact]
    public async Task Dispose_releases_entries_and_rejects_further_use()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        _ = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
        await authority.DisposeAsync();
        var next = RetentionTestArticles.Create("<b@c>");
        Assert.Throws<ObjectDisposedException>(() => authority.RetainCanonical(
            "<b@c>", next.RequestId, next.Record, next.SelectedDateHeaderName));
    }

    [Fact]
    public async Task Concurrent_insert_and_open_keep_exact_accounting()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 16 * 1024 * 1024);
        var prepared = Enumerable.Range(0, 32)
            .Select(i => RetentionTestArticles.Create($"<id{i}@b>", $"body-{i}\r\n"))
            .ToArray();
        var inserts = prepared.Select(async item =>
        {
            await Task.Yield();
            return authority.RetainCanonical(
                item.MessageId,
                item.RequestId,
                item.Record,
                item.SelectedDateHeaderName);
        });
        var results = await Task.WhenAll(inserts);
        Assert.Equal(32, results.Count(static r => r.Kind == ArticleRetentionKind.Retained));
        Assert.Equal(prepared.Sum(static p => p.Record.ArtSize), authority.RetainedPayloadBytes);

        var opens = prepared.Select(async item =>
        {
            await Task.Yield();
            using var open = authority.TryOpenTransfer(item.RequestId, item.Record.ArtId);
            return open.Kind;
        });
        var kinds = await Task.WhenAll(opens);
        Assert.All(kinds, static kind => Assert.Equal(VatpOpenKind.Opened, kind));
    }

    [Fact]
    public async Task Open_while_expiry_occurs_does_not_return_expired_bytes()
    {
        var time = new ManualTimeProvider(Start);
        var authority = Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(1));
        var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openTask = Task.Run(async () =>
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        });
        await started.Task;
        time.Advance(TimeSpan.FromSeconds(1));
        release.TrySetResult();
        using var result = await openTask;
        Assert.Equal(VatpOpenKind.Rejected, result.Kind);
        Assert.Equal(0, authority.RetainedPayloadBytes);
    }

    [Fact]
    public async Task Duplicate_identity_race_keeps_one_entry_and_exact_bytes()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var candidates = Enumerable.Range(0, 16)
            .Select(i => RetentionTestArticles.Create("<same@b>", $"body-{i}\r\n"))
            .ToArray();
        var tasks = candidates.Select(async item =>
        {
            await Task.Yield();
            return authority.RetainCanonical(
                "<same@b>",
                item.RequestId,
                item.Record,
                item.SelectedDateHeaderName);
        });
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(static r => r.Kind == ArticleRetentionKind.Retained));
        Assert.Equal(15, results.Count(static r => r.Kind == ArticleRetentionKind.AlreadyPresent));
        Assert.Equal(1, authority.RetainedCount);
        var winner = Assert.Single(results, static r => r.Kind == ArticleRetentionKind.Retained);
        Assert.Equal(winner.RetainedPayloadBytes, authority.RetainedPayloadBytes);
    }

    [Fact]
    public void Shutdown_during_open_still_returns_a_live_entry()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
        var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        authority.BeginShutdown();
        Assert.Equal(VatpOpenKind.Opened, open.Kind);
        Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(prepared.ArtData));
        open.Dispose();
        var later = RetentionTestArticles.Create("<c@d>");
        Assert.Equal(ArticleRetentionKind.ShuttingDown, authority.RetainCanonical(
            "<c@d>", later.RequestId, later.Record, later.SelectedDateHeaderName).Kind);
    }

    [Fact]
    public void Production_retention_code_does_not_block_synchronously()
    {
        var directory = FindSourceDirectory();
        foreach (var file in Directory.GetFiles(directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
        }
    }

    internal static ArticleRetentionAuthority Create(
        TimeProvider time,
        int maxBytes,
        TimeSpan? ttl = null,
        TimeSpan? sweep = null) =>
        new(
            new BackFillerArticleRetentionRuntimeOptions(
                maxBytes,
                ttl ?? TimeSpan.FromSeconds(60),
                sweep ?? TimeSpan.FromSeconds(1)),
            "backfiller.test",
            1190,
            time,
            NullLogger.Instance);

    private static string FindSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VectorNNTP.BackFiller", "Retention");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/VectorNNTP.BackFiller/Retention.");
    }
}
