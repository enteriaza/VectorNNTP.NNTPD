using System.Net;
using VectorNNTP.Common.Articles;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.PostFilter;
using VectorNNTP.NNTPD.PostFilter.Quota;

namespace VectorNNTP.NNTPD.Tests.PostFilter;

/// <summary>
/// POST-boundary production-policy matrix for PostFilter v1.
/// Test-local snapshots only; does not change production <c>Gate=Disabled</c>.
/// </summary>
public sealed class PostFilterProductionPolicyMatrixTests
{
    [Fact]
    public async Task Scenario01_DisabledGate_KeepsExistingPostPath()
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin();
        var history = new PolicyRecordingHistoryDb();
        var queue = PostFilterPostHarness.NewQueue();
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(
                queue,
                PostFilterPostHarness.CreateFilter(PostFilterPolicySnapshot.Disabled, quota, sa),
                historyDb: history),
            "240 Article received OK");
        Assert.Equal(1, queue.Count);
        Assert.Empty(quota.Operations);
        Assert.Equal(0, sa.Calls);
        Assert.Equal(1, history.RememberCalls);
        await AssertQueuedCanonicalAsync(queue);
    }

    [Fact]
    public async Task Scenario02_ClosedGate_Returns441_WithoutReserveOrSpamAssassin()
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin();
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Closed,
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(queue, PostFilterPostHarness.CreateFilter(snapshot, quota, sa)),
            "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Empty(quota.Operations);
        Assert.Equal(0, sa.Calls);
    }

    [Fact]
    public async Task Scenario03_AccountAndCidrDeny_Returns441_WithoutReserve()
    {
        var accountQuota = new PolicyRecordingQuotaStore();
        var accountSa = new PolicyRecordingSpamAssassin();
        var accountQueue = PostFilterPostHarness.NewQueue();
        var accountSnapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedAccounts = ["poster"],
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        await using var accountDuplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            accountDuplex,
            accountDuplex.CreateSession(
                accountQueue,
                PostFilterPostHarness.CreateFilter(accountSnapshot, accountQuota, accountSa)),
            "441 Posting failed");
        Assert.Equal(0, accountQueue.Count);
        Assert.Empty(accountQuota.Operations);
        Assert.Equal(0, accountSa.Calls);

        var cidrQuota = new PolicyRecordingQuotaStore();
        var cidrSa = new PolicyRecordingSpamAssassin();
        var cidrQueue = PostFilterPostHarness.NewQueue();
        var cidrSnapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedCidrs = ["127.0.0.0/8"],
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        await using var cidrDuplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            cidrDuplex,
            cidrDuplex.CreateSession(
                cidrQueue,
                PostFilterPostHarness.CreateFilter(cidrSnapshot, cidrQuota, cidrSa)),
            "441 Posting failed");
        Assert.Equal(0, cidrQueue.Count);
        Assert.Empty(cidrQuota.Operations);
        Assert.Equal(0, cidrSa.Calls);
    }

    [Fact]
    public async Task Scenario04_ArtTypeDeny_Returns441_WithoutReserve()
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin();
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            RejectArtTypes = ["Default"],
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(queue, PostFilterPostHarness.CreateFilter(snapshot, quota, sa)),
            "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Empty(quota.Operations);
        Assert.Equal(0, sa.Calls);
    }

    [Fact]
    public async Task Scenario05_Allowlist_SkipsSpamAssassin_KeepsDenyAndQuota()
    {
        var denyQuota = new PolicyRecordingQuotaStore();
        var denySa = new PolicyRecordingSpamAssassin();
        var denyQueue = PostFilterPostHarness.NewQueue();
        var denySnapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedAccounts = ["poster"],
            AllowlistedAccounts = ["poster"],
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        await using var denyDuplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            denyDuplex,
            denyDuplex.CreateSession(
                denyQueue,
                PostFilterPostHarness.CreateFilter(denySnapshot, denyQuota, denySa)),
            "441 Posting failed");
        Assert.Equal(0, denyQueue.Count);
        Assert.Empty(denyQuota.Operations);
        Assert.Equal(0, denySa.Calls);

        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            AllowlistedAccounts = ["poster"],
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        var filter = PostFilterPostHarness.CreateFilter(snapshot, quota, sa);
        await using var first = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            first,
            first.CreateSession(PostFilterPostHarness.NewQueue(), filter),
            "240 Article received OK");
        await using var second = new PostFilterPostDuplex();
        var rejected = PostFilterPostHarness.NewQueue();
        await PostFilterPostHarness.PostAsync(
            second,
            second.CreateSession(rejected, filter),
            "441 Posting failed");
        Assert.Equal(0, rejected.Count);
        Assert.Equal(0, sa.Calls);
        Assert.Equal(new[] { "reserve", "commit", "reserve" }, quota.Operations);
    }

    [Fact]
    public async Task Scenario06_MessageCeiling_SecondPostReturns441_WithoutSpamAssassin()
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        var filter = PostFilterPostHarness.CreateFilter(snapshot, quota, sa);
        await using var first = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            first,
            first.CreateSession(PostFilterPostHarness.NewQueue(), filter),
            "240 Article received OK");
        Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
        var saCallsAfterAccept = sa.Calls;
        await using var second = new PostFilterPostDuplex();
        var queue = PostFilterPostHarness.NewQueue();
        await PostFilterPostHarness.PostAsync(
            second,
            second.CreateSession(queue, filter),
            "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Equal(saCallsAfterAccept, sa.Calls);
        Assert.Equal(new[] { "reserve", "commit", "reserve" }, quota.Operations);
        Assert.DoesNotContain("release", quota.Operations);
    }

    [Fact]
    public async Task Scenario07_ByteCeiling_UsesArticleRecordArtSize()
    {
        var probeQuota = new PolicyRecordingQuotaStore();
        var probeQueue = PostFilterPostHarness.NewQueue();
        var probeSnapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxBytesLong = 1_000_000 },
        });
        await using var probe = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            probe,
            probe.CreateSession(probeQueue, PostFilterPostHarness.CreateFilter(probeSnapshot, probeQuota)),
            "240 Article received OK");
        var inbound = await DequeueAsync(probeQueue);
        Assert.Equal(inbound.Record.ArtSize, probeQuota.LastReservedBytes);
        Assert.Equal(1, probeQuota.LastReservedMessages);

        var quota = new PolicyRecordingQuotaStore();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxBytesLong = inbound.Record.ArtSize },
        });
        var filter = PostFilterPostHarness.CreateFilter(snapshot, quota);
        await using var first = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            first,
            first.CreateSession(PostFilterPostHarness.NewQueue(), filter),
            "240 Article received OK");
        await using var second = new PostFilterPostDuplex();
        var rejected = PostFilterPostHarness.NewQueue();
        await PostFilterPostHarness.PostAsync(
            second,
            second.CreateSession(rejected, filter),
            "441 Posting failed");
        Assert.Equal(0, rejected.Count);
        Assert.Equal(inbound.Record.ArtSize, quota.LastReservedBytes);
    }

    [Fact]
    public async Task Scenario08_IdenticalCeiling_IsBodyHashNotArtHash()
    {
        var quota = new PolicyRecordingQuotaStore();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxIdenticalLong = 1 },
        });
        var filter = PostFilterPostHarness.CreateFilter(snapshot, quota);
        const string body = "shared-canonical-body\r\n";
        var firstQueue = PostFilterPostHarness.NewQueue();
        await using var first = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            first,
            first.CreateSession(firstQueue, filter),
            "240 Article received OK",
            article: PostFilterPostHarness.TextArticle(body));
        var firstInbound = await DequeueAsync(firstQueue);
        var bodyHex = PostFilterBodyHash.TryCompute(firstInbound.Record);
        Assert.False(string.IsNullOrWhiteSpace(bodyHex));
        Assert.Equal(bodyHex, quota.LastBodyHex);
        Assert.NotEqual(firstInbound.Record.ArtHash.ToString("x16"), quota.LastBodyHex);

        await using var sameBody = new PostFilterPostDuplex();
        var rejected = PostFilterPostHarness.NewQueue();
        await PostFilterPostHarness.PostAsync(
            sameBody,
            sameBody.CreateSession(rejected, filter),
            "441 Posting failed",
            article: PostFilterPostHarness.TextArticle(body));
        Assert.Equal(0, rejected.Count);

        await using var otherBody = new PostFilterPostDuplex();
        var accepted = PostFilterPostHarness.NewQueue();
        await PostFilterPostHarness.PostAsync(
            otherBody,
            otherBody.CreateSession(accepted, filter),
            "240 Article received OK",
            article: PostFilterPostHarness.TextArticle("different-body\r\n"));
        Assert.Equal(1, accepted.Count);
        var second = await DequeueAsync(accepted);
        Assert.NotEqual(firstInbound.Record.ArtHash, second.Record.ArtHash);
        Assert.NotEqual(bodyHex, PostFilterBodyHash.TryCompute(second.Record));
    }

    [Fact]
    public async Task Scenario09_AccountIsolation_SameBodyDoesNotShareQuota()
    {
        var quota = new PolicyRecordingQuotaStore();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
        });
        var filter = PostFilterPostHarness.CreateFilter(snapshot, quota);
        const string body = "isolated-body\r\n";
        await using var alice = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            alice,
            alice.CreateSession(PostFilterPostHarness.NewQueue(), filter),
            "240 Article received OK",
            username: "alice",
            article: PostFilterPostHarness.TextArticle(body));
        Assert.Equal("alice", quota.LastAccountName);
        await using var bob = new PostFilterPostDuplex();
        var bobQueue = PostFilterPostHarness.NewQueue();
        await PostFilterPostHarness.PostAsync(
            bob,
            bob.CreateSession(bobQueue, filter),
            "240 Article received OK",
            username: "bob",
            article: PostFilterPostHarness.TextArticle(body));
        Assert.Equal(1, bobQueue.Count);
        Assert.Equal("bob", quota.LastAccountName);
        Assert.Equal(new[] { "reserve", "commit", "reserve", "commit" }, quota.Operations);
    }

    [Fact]
    public async Task Scenario10_CrossNode_SameAccountSharesInMemoryQuota()
    {
        var shared = new InMemoryPostFilterQuotaStore();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
        });
        var nodeA = PostFilterPostHarness.CreateFilter(
            snapshot,
            shared,
            identity: new PostFilterReservationIdentity("n1", "incA"));
        var nodeB = PostFilterPostHarness.CreateFilter(
            snapshot,
            shared,
            identity: new PostFilterReservationIdentity("n2", "incB"));
        await using var first = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            first,
            first.CreateSession(PostFilterPostHarness.NewQueue(), nodeA),
            "240 Article received OK");
        await using var second = new PostFilterPostDuplex();
        var queue = PostFilterPostHarness.NewQueue();
        await PostFilterPostHarness.PostAsync(
            second,
            second.CreateSession(queue, nodeB),
            "441 Posting failed");
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Scenario11_EligibleHam_CallsSpamAssassin_CommitsAndReturns240()
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin();
        var history = new PolicyRecordingHistoryDb();
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(
                queue,
                PostFilterPostHarness.CreateFilter(snapshot, quota, sa),
                historyDb: history),
            "240 Article received OK");
        Assert.Equal(1, sa.Calls);
        Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
        Assert.Equal(1, history.RememberCalls);
        var inbound = await AssertQueuedCanonicalAsync(queue);
        Assert.Equal(sa.LastArtHash, inbound.Record.ArtHash);
        Assert.Equal(sa.LastArtId, inbound.Record.ArtId);
        Assert.True(sa.LastArtData!.AsSpan().SequenceEqual(inbound.Record.ArtData.Span));
        Assert.False(sa.LastScan!.AsSpan().SequenceEqual(inbound.Record.ArtData.Span));
    }

    [Fact]
    public async Task Scenario12_SpamResult_Returns441_ReleasesAndDoesNotRemember()
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin { Result = PostFilterSpamAssassinResult.Spam("spam") };
        var history = new PolicyRecordingHistoryDb();
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(
                queue,
                PostFilterPostHarness.CreateFilter(snapshot, quota, sa),
                historyDb: history),
            "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Equal(1, sa.Calls);
        Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
        Assert.Equal(0, history.RememberCalls);
        Assert.NotNull(sa.LastArtData);
        Assert.NotNull(sa.LastScan);
        Assert.False(sa.LastScan.AsSpan().SequenceEqual(sa.LastArtData));
        Assert.Equal(sa.LastArtSize, sa.LastArtData.Length);
    }

    [Fact]
    public async Task Scenario13_ArticleTooLargeForSpamAssassin_IsEligibilitySkip()
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin { Result = PostFilterSpamAssassinResult.Failed("must-not-run") };
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(maxArticleSize: 64),
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(queue, PostFilterPostHarness.CreateFilter(snapshot, quota, sa)),
            "240 Article received OK");
        Assert.Equal(0, sa.Calls);
        Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
        var inbound = await AssertQueuedCanonicalAsync(queue);
        Assert.True(inbound.Record.ArtSize >= 64);
    }

    [Fact]
    public async Task Scenario14_ExcludedArtType_SkipsSpamAssassin()
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin { Result = PostFilterSpamAssassinResult.Failed("must-not-run") };
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(excludeArtTypes: ["Default"]),
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(queue, PostFilterPostHarness.CreateFilter(snapshot, quota, sa)),
            "240 Article received OK");
        Assert.Equal(0, sa.Calls);
        Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
        var inbound = await AssertQueuedCanonicalAsync(queue);
        Assert.Equal(ArticleType.Default, inbound.Record.ArtType);
    }

    [Fact]
    public async Task Scenario15_AllowlistedAccount_BypassesSpamAssassinOnly()
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            AllowlistedCidrs = ["127.0.0.0/8"],
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 1 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        var filter = PostFilterPostHarness.CreateFilter(snapshot, quota, sa);
        await using var first = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            first,
            first.CreateSession(PostFilterPostHarness.NewQueue(), filter, clientAddress: IPAddress.Loopback),
            "240 Article received OK");
        Assert.Equal(0, sa.Calls);
        Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
        await using var second = new PostFilterPostDuplex();
        var queue = PostFilterPostHarness.NewQueue();
        await PostFilterPostHarness.PostAsync(
            second,
            second.CreateSession(queue, filter),
            "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, sa.Calls);
    }

    [Theory]
    [InlineData(PostFilterSpamOnFailure.Reject, "441 Posting failed", false)]
    [InlineData(PostFilterSpamOnFailure.Accept, "240 Article received OK", true)]
    public async Task Scenario16And17_SpamAssassinOnFailure(
        PostFilterSpamOnFailure onFailure,
        string expected,
        bool enqueued)
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin { Result = PostFilterSpamAssassinResult.Failed("down") };
        var history = new PolicyRecordingHistoryDb();
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(onFailure),
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(
                queue,
                PostFilterPostHarness.CreateFilter(snapshot, quota, sa),
                historyDb: history),
            expected);
        Assert.Equal(1, sa.Calls);
        Assert.Equal(enqueued ? 1 : 0, queue.Count);
        Assert.Equal(enqueued ? 1 : 0, history.RememberCalls);
        Assert.Equal(enqueued ? new[] { "reserve", "commit" } : new[] { "reserve", "release" }, quota.Operations);
    }

    [Fact]
    public async Task Scenario18_RedisUnavailableAtReserve_FailsClosedWithoutSpamAssassin()
    {
        var quota = new PolicyRecordingQuotaStore { Unavailable = true };
        var sa = new PolicyRecordingSpamAssassin();
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(queue, PostFilterPostHarness.CreateFilter(snapshot, quota, sa)),
            "441 Posting failed");
        Assert.Equal(0, queue.Count);
        Assert.Equal(new[] { "reserve" }, quota.Operations);
        Assert.DoesNotContain("commit", quota.Operations);
        Assert.Equal(0, sa.Calls);
    }

    [Fact]
    public async Task Scenario19_CommitUnavailableAfterAdmit_KeepsQueueAnd240_WithoutRelease()
    {
        var quota = new PolicyRecordingQuotaStore { CommitUnavailable = true };
        var metrics = new PostFilterMetrics();
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(
                queue,
                PostFilterPostHarness.CreateFilter(snapshot, quota),
                metrics),
            "240 Article received OK");
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, metrics.CommitUnavailable);
        Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
        Assert.DoesNotContain("release", quota.Operations);
    }

    [Fact]
    public async Task Scenario20_TryAdmitFailure_ReleasesReservation()
    {
        var quota = new PolicyRecordingQuotaStore();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(
                DisabledArticleIngestionQueue.Instance,
                PostFilterPostHarness.CreateFilter(snapshot, quota)),
            "441 Posting failed");
        Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
        Assert.Equal(CancellationToken.None, quota.LastReleaseToken);
        Assert.DoesNotContain("commit", quota.Operations);
    }

    [Fact]
    public async Task Scenario21_CancellationAfterReserve_ReleasesWithNone()
    {
        var quota = new PolicyRecordingQuotaStore();
        var history = new PolicyRecordingHistoryDb();
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
        });
        await using var duplex = new PostFilterPostDuplex();
        var session = duplex.CreateSession(
            queue,
            PostFilterPostHarness.CreateFilter(snapshot, quota),
            historyDb: history,
            cancelConnectionAfterAccept: true);
        session.ApplySuccessfulAuthentication("poster", PostFilterPostHarness.Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync(PostFilterPostHarness.TextArticle() + ".\r\n");
        await run.WaitAsync(PostFilterPostHarness.Safety);
        Assert.Equal(0, queue.Count);
        Assert.Equal(new[] { "reserve", "release" }, quota.Operations);
        Assert.Equal(CancellationToken.None, quota.LastReleaseToken);
        Assert.Equal(0, history.RememberCalls);
    }

    [Fact]
    public async Task Scenario22_CompletePath_ReservesScansAdmitsCommitsRemembers()
    {
        var quota = new PolicyRecordingQuotaStore();
        var sa = new PolicyRecordingSpamAssassin();
        var history = new PolicyRecordingHistoryDb();
        var queue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(
                queue,
                PostFilterPostHarness.CreateFilter(snapshot, quota, sa),
                historyDb: history),
            "240 Article received OK");
        Assert.Equal(1, sa.Calls);
        Assert.Equal(new[] { "reserve", "commit" }, quota.Operations);
        Assert.Equal(1, history.RememberCalls);
        var inbound = await AssertQueuedCanonicalAsync(queue);
        Assert.True(inbound.Record.ArtData.Equals(inbound.Payload));
        Assert.True(sa.LastArtData!.AsSpan().SequenceEqual(inbound.Record.ArtData.Span));
        Assert.False(ReferenceEquals(sa.LastScan, sa.LastArtData));
    }

    [Fact]
    public async Task Scenario23And24_ArticleRecordUnchanged_AndScanIsDisposable()
    {
        var hamSa = new PolicyRecordingSpamAssassin();
        var hamQuota = new PolicyRecordingQuotaStore();
        var hamQueue = PostFilterPostHarness.NewQueue();
        var snapshot = PostFilterPostHarness.Compile(new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            Quota = new PostFilterQuotaOptions { MaxMessagesLong = 10 },
            SpamAssassin = PostFilterPostHarness.EnabledSpamAssassin(),
        });
        await using var ham = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            ham,
            ham.CreateSession(hamQueue, PostFilterPostHarness.CreateFilter(snapshot, hamQuota, hamSa)),
            "240 Article received OK");
        var queued = await AssertQueuedCanonicalAsync(hamQueue);
        Assert.Equal(hamSa.LastArtHash, queued.Record.ArtHash);
        Assert.Equal(hamSa.LastArtId, queued.Record.ArtId);
        Assert.Equal(hamSa.LastArtType, queued.Record.ArtType);
        Assert.Equal(hamSa.LastArtSize, queued.Record.ArtSize);
        Assert.Equal(hamSa.LastFields.MessageId, queued.Record.Fields.MessageId);
        Assert.True(hamSa.LastArtData!.AsSpan().SequenceEqual(queued.Record.ArtData.Span));
        Assert.False(hamSa.LastScan!.AsSpan().SequenceEqual(queued.Record.ArtData.Span));
        Assert.Contains("Received:", System.Text.Encoding.ASCII.GetString(hamSa.LastScan!), StringComparison.Ordinal);

        var spamSa = new PolicyRecordingSpamAssassin { Result = PostFilterSpamAssassinResult.Spam("spam") };
        var spamQuota = new PolicyRecordingQuotaStore();
        await using var spam = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            spam,
            spam.CreateSession(
                PostFilterPostHarness.NewQueue(),
                PostFilterPostHarness.CreateFilter(snapshot, spamQuota, spamSa)),
            "441 Posting failed");
        Assert.Equal(1, spamSa.Calls);
        Assert.Equal(spamSa.LastArtSize, spamSa.LastArtData!.Length);
        Assert.False(spamSa.LastScan!.AsSpan().SequenceEqual(spamSa.LastArtData));
        Assert.NotEqual(0UL, spamSa.LastArtHash);
    }

    private static async Task<InboundArticle> AssertQueuedCanonicalAsync(ArticleIngestionQueue queue)
    {
        var inbound = await DequeueAsync(queue);
        Assert.Equal(ArticleParseStatus.CanonicalV1, inbound.Record.ParseStatus);
        Assert.True(inbound.Record.ArtData.Equals(inbound.Payload));
        Assert.True(inbound.Record.ArtSize > 0);
        return inbound;
    }

    private static async Task<InboundArticle> DequeueAsync(ArticleIngestionQueue queue)
    {
        using var cts = new CancellationTokenSource(PostFilterPostHarness.Safety);
        var inbound = await queue.DequeueAsync(cts.Token);
        Assert.NotNull(inbound);
        return inbound!;
    }
}
