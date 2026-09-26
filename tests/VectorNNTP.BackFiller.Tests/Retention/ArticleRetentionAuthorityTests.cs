using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Retention;

public sealed class ArticleRetentionAuthorityTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retain_then_lookup_exposes_owned_payload_and_uri()
    {
        var time = new ManualTimeProvider(Start);
        var authority = Create(time, maxBytes: 1024);
        var payload = "From: a@b\r\n\r\nbody"u8.ToArray();

        var retained = authority.Retain("<a@b>", payload);
        using var lookup = authority.TryGetByMessageId("<a@b>");
        using var byMd5 = authority.TryGetByMd5(retained.Identity!.Value.Md5Hex);

        Assert.Equal(ArticleRetentionKind.Retained, retained.Kind);
        Assert.Equal("cache://backfiller.test:1190/" + retained.Identity.Value.Md5Hex, retained.CacheUri);
        Assert.Equal(payload.Length, authority.RetainedPayloadBytes);
        Assert.Equal(1, authority.RetainedCount);
        Assert.Equal(ArticleLookupKind.Found, lookup.Kind);
        Assert.Equal(payload, lookup.Lease!.Payload.ToArray());
        Assert.Equal(retained.CacheUri, lookup.Lease.CacheUri);
        Assert.Equal(ArticleLookupKind.Found, byMd5.Kind);
    }

    [Fact]
    public void Ownership_transfers_and_survives_retrieved_article_and_session_disposal()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024);
        var retrieved = new RetrievedArticle("From: a@b\r\n\r\nbody"u8.ToArray());
        Assert.True(retrieved.TryDetach(out var payload));
        retrieved.Dispose();
        var retained = authority.Retain("<a@b>", payload);
        Assert.True(retained.IsAvailable);
        Assert.False(retrieved.TryDetach(out _));

        using var lookup = authority.TryGetByMessageId("<a@b>");
        Assert.Equal("From: a@b\r\n\r\nbody"u8.ToArray(), lookup.Lease!.Payload.ToArray());
        lookup.Lease.Dispose();
        lookup.Lease.Dispose();
        using var again = authority.TryGetByMessageId("<a@b>");
        Assert.Equal(ArticleLookupKind.Found, again.Kind);
    }

    [Fact]
    public void Duplicate_message_id_is_first_wins_and_does_not_double_count()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024);
        var first = "one"u8.ToArray();
        var second = "two-bytes"u8.ToArray();
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<a@b>", first).Kind);
        var duplicate = authority.Retain("<a@b>", second);
        Assert.Equal(ArticleRetentionKind.AlreadyPresent, duplicate.Kind);
        Assert.Equal(first.Length, authority.RetainedPayloadBytes);
        using var lookup = authority.TryGetByMessageId("<a@b>");
        Assert.Equal(first, lookup.Lease!.Payload.ToArray());
    }

    [Fact]
    public void Lookup_after_ttl_is_expired_and_sweep_reclaims_bytes()
    {
        var time = new ManualTimeProvider(Start);
        var authority = Create(time, maxBytes: 1024, ttl: TimeSpan.FromSeconds(10));
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<a@b>", "payload"u8.ToArray()).Kind);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ArticleLookupKind.Expired, authority.TryGetByMessageId("<a@b>").Kind);
        Assert.Equal(0, authority.RetainedPayloadBytes);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, authority.SweepExpired());
        Assert.Equal(ArticleLookupKind.Missing, authority.TryGetByMessageId("<a@b>").Kind);
    }

    [Fact]
    public void Sweep_does_not_remove_a_newer_generation_of_the_same_identity()
    {
        var time = new ManualTimeProvider(Start);
        var authority = Create(time, maxBytes: 1024, ttl: TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<a@b>", "old"u8.ToArray()).Kind);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleLookupKind.Expired, authority.TryGetByMessageId("<a@b>").Kind);
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<a@b>", "new-payload"u8.ToArray()).Kind);
        Assert.Equal(0, authority.SweepExpired());
        using var lookup = authority.TryGetByMessageId("<a@b>");
        Assert.Equal("new-payload"u8.ToArray(), lookup.Lease!.Payload.ToArray());
    }

    [Fact]
    public void Article_exactly_at_capacity_is_retained_and_oversize_is_rejected()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 8);
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<fit@b>", "12345678"u8.ToArray()).Kind);
        Assert.Equal(8, authority.RetainedPayloadBytes);
        var over = authority.Retain("<big@b>", "123456789"u8.ToArray());
        Assert.Equal(ArticleRetentionKind.PayloadExceedsCapacity, over.Kind);
        Assert.True(over.IsCapacityRejected);
        Assert.Equal(8, authority.RetainedPayloadBytes);
    }

    [Fact]
    public void Insufficient_remaining_capacity_evicts_oldest_then_admits()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 10);
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<one@b>", "12345"u8.ToArray()).Kind);
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<two@b>", "67890"u8.ToArray()).Kind);
        var third = authority.Retain("<three@b>", "abcd"u8.ToArray());
        Assert.Equal(ArticleRetentionKind.Retained, third.Kind);
        Assert.Equal(ArticleLookupKind.Missing, authority.TryGetByMessageId("<one@b>").Kind);
        Assert.Equal(9, authority.RetainedPayloadBytes);
    }

    [Fact]
    public void Empty_payload_is_invalid_and_does_not_change_accounting()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 32);
        Assert.Equal(ArticleRetentionKind.InvalidPayload, authority.Retain("<a@b>", []).Kind);
        Assert.Equal(0, authority.RetainedPayloadBytes);
    }

    [Fact]
    public void Shutdown_rejects_insert_and_lookup_of_existing_still_works_until_dispose()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 32);
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<a@b>", "keep"u8.ToArray()).Kind);
        authority.BeginShutdown();
        Assert.Equal(ArticleRetentionKind.ShuttingDown, authority.Retain("<b@c>", "later"u8.ToArray()).Kind);
        Assert.Equal(ArticleLookupKind.Found, authority.TryGetByMessageId("<a@b>").Kind);
    }

    [Fact]
    public async Task Dispose_releases_entries_and_rejects_further_use()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 32);
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<a@b>", "keep"u8.ToArray()).Kind);
        await authority.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => authority.Retain("<b@c>", "x"u8.ToArray()));
    }

    [Fact]
    public async Task Concurrent_insert_and_lookup_keep_exact_accounting()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var inserts = Enumerable.Range(0, 32).Select(async i =>
        {
            await Task.Yield();
            return authority.Retain($"<id{i}@b>", new byte[10]);
        });
        var results = await Task.WhenAll(inserts);
        Assert.Equal(32, results.Count(static r => r.Kind == ArticleRetentionKind.Retained));
        Assert.Equal(320, authority.RetainedPayloadBytes);

        var lookups = Enumerable.Range(0, 32).Select(async i =>
        {
            await Task.Yield();
            using var result = authority.TryGetByMessageId($"<id{i}@b>");
            return result.Kind;
        });
        var kinds = await Task.WhenAll(lookups);
        Assert.All(kinds, static kind => Assert.Equal(ArticleLookupKind.Found, kind));
    }

    [Fact]
    public async Task Lookup_while_expiry_occurs_does_not_return_expired_bytes()
    {
        var time = new ManualTimeProvider(Start);
        var authority = Create(time, maxBytes: 1024, ttl: TimeSpan.FromSeconds(1));
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<a@b>", "payload"u8.ToArray()).Kind);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = Task.Run(async () =>
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return authority.TryGetByMessageId("<a@b>");
        });
        await started.Task;
        time.Advance(TimeSpan.FromSeconds(1));
        release.TrySetResult();
        using var result = await lookup;
        Assert.NotEqual(ArticleLookupKind.Found, result.Kind);
        Assert.Equal(0, authority.RetainedPayloadBytes);
    }

    [Fact]
    public void Lease_cannot_dispose_authority_storage_for_another_caller()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 32);
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<a@b>", "shared"u8.ToArray()).Kind);
        var first = authority.TryGetByMessageId("<a@b>");
        var second = authority.TryGetByMessageId("<a@b>");
        first.Lease!.Dispose();
        Assert.Equal("shared"u8.ToArray(), second.Lease!.Payload.ToArray());
        second.Lease.Dispose();
        using var still = authority.TryGetByMessageId("<a@b>");
        Assert.Equal(ArticleLookupKind.Found, still.Kind);
    }

    [Fact]
    public async Task Duplicate_identity_race_keeps_one_entry_and_exact_bytes()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024);
        var tasks = Enumerable.Range(0, 16).Select(async i =>
        {
            await Task.Yield();
            return authority.Retain("<same@b>", new byte[] { (byte)i });
        });
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(static r => r.Kind == ArticleRetentionKind.Retained));
        Assert.Equal(15, results.Count(static r => r.Kind == ArticleRetentionKind.AlreadyPresent));
        Assert.Equal(1, authority.RetainedPayloadBytes);
        Assert.Equal(1, authority.RetainedCount);
    }

    [Fact]
    public void Shutdown_during_lookup_still_returns_a_live_entry()
    {
        var authority = Create(new ManualTimeProvider(Start), maxBytes: 32);
        Assert.Equal(ArticleRetentionKind.Retained, authority.Retain("<a@b>", "keep"u8.ToArray()).Kind);
        var lookup = authority.TryGetByMessageId("<a@b>");
        authority.BeginShutdown();
        Assert.Equal(ArticleLookupKind.Found, lookup.Kind);
        Assert.Equal("keep"u8.ToArray(), lookup.Lease!.Payload.ToArray());
        lookup.Dispose();
        Assert.Equal(ArticleRetentionKind.ShuttingDown, authority.Retain("<c@d>", "nope"u8.ToArray()).Kind);
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
