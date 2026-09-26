using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Tests.TestDoubles;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking;

namespace VectorNNTP.Common.Tests.Cloudflare;

public sealed class CloudflareAndDns01Tests
{
    private const string ZoneId = "zone-test";
    private const string Fqdn = "backfiller01.usenet.ninja";

    [Fact]
    public async Task Reconcile_CreatesAAndAaaa_WithManagedTtlAndDnsOnly()
    {
        var client = new InMemoryCloudflareDnsClient();
        var reconciler = CreateReconciler(client);

        await reconciler.ReconcileAsync(
            ZoneId,
            Fqdn,
            Desired("198.18.0.10", "2001:db8::10"),
            CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.Contains(
            snapshot,
            static r => r.Type == "A"
                        && r.Content == "198.18.0.10"
                        && r.Ttl == CloudflareManagedDnsPolicy.ManagedTtl
                        && r.Proxied == CloudflareManagedDnsPolicy.ManagedProxied);
        Assert.Contains(
            snapshot,
            static r => r.Type == "AAAA"
                        && r.Content == "2001:db8::10"
                        && r.Ttl == CloudflareManagedDnsPolicy.ManagedTtl
                        && r.Proxied == CloudflareManagedDnsPolicy.ManagedProxied);
    }

    [Fact]
    public async Task Reconcile_RemovesStaleAndLeavesUnrelatedNames()
    {
        var client = new InMemoryCloudflareDnsClient();
        client.Seed(
            Record("stale-a", "A", "198.18.0.99"),
            Record("other", "A", "198.18.0.50", name: "other.usenet.ninja"));
        var reconciler = CreateReconciler(client);

        await reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.DoesNotContain(snapshot, static r => r.Content == "198.18.0.99");
        Assert.Contains(snapshot, static r => r.Name == "other.usenet.ninja");
        Assert.Contains(snapshot, static r => r.Type == "A" && r.Content == "198.18.0.10");
    }

    [Fact]
    public async Task Reconcile_UnchangedSet_MakesNoMutations()
    {
        var client = new InMemoryCloudflareDnsClient();
        client.Seed(Record("a1", "A", "198.18.0.10"));
        var reconciler = CreateReconciler(client);

        await reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

        Assert.Equal(0, client.CreateCallCount);
        Assert.Equal(0, client.UpdateCallCount);
        Assert.Equal(0, client.DeleteCallCount);
    }

    [Fact]
    public async Task Cleanup_RemovesExactFqdnRecords()
    {
        var client = new InMemoryCloudflareDnsClient();
        client.Seed(
            Record("a1", "A", "198.18.0.10"),
            Record("txt", "TXT", "keep-other", name: "other.usenet.ninja"));
        var reconciler = CreateReconciler(client);

        await reconciler.RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.DoesNotContain(snapshot, static r => r.Name == Fqdn);
        Assert.Contains(snapshot, static r => r.Name == "other.usenet.ninja");
    }

    [Fact]
    public async Task Dns01_PlaceAndCleanup_RemovesTxt()
    {
        using var dir = new TempStateDir();
        var client = new InMemoryCloudflareDnsClient();
        var resolver = new ImmediateTxtResolver();
        var solver = new Dns01Solver(
            client,
            ZoneId,
            resolver,
            dir.Path,
            propagationTimeout: TimeSpan.FromSeconds(2),
            propagationInterval: TimeSpan.FromMilliseconds(10));

        var spec = new Dns01ChallengeSpec(Fqdn, "validation-token");
        resolver.Set(spec.RecordName, spec.Validation);
        await solver.PlaceAsync([spec], CancellationToken.None);
        Assert.Contains(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);
        await solver.WaitPropagatedAsync([spec], CancellationToken.None);
        await solver.CleanupAsync(CancellationToken.None);
        Assert.DoesNotContain(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);
    }

    [Fact]
    public async Task Dns01_CreateFailure_CleansUp()
    {
        using var dir = new TempStateDir();
        var client = new InMemoryCloudflareDnsClient
        {
            CreateException = new CloudflareDnsException("create failed"),
        };
        var solver = new Dns01Solver(client, ZoneId, new ImmediateTxtResolver(), dir.Path);

        await Assert.ThrowsAsync<AcmeChallengeException>(
            () => solver.PlaceAsync([new Dns01ChallengeSpec(Fqdn, "tok")], CancellationToken.None));
        Assert.DoesNotContain(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);
    }

    [Fact]
    public async Task Dns01_Cancellation_CleansUp()
    {
        using var dir = new TempStateDir();
        var client = new InMemoryCloudflareDnsClient
        {
            OnMutate = static async ct => await Task.Delay(Timeout.Infinite, ct),
        };
        var solver = new Dns01Solver(client, ZoneId, new ImmediateTxtResolver(), dir.Path);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => solver.PlaceAsync([new Dns01ChallengeSpec(Fqdn, "tok")], cts.Token));
        Assert.DoesNotContain(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);
    }

    private static CloudflareDnsReconciler CreateReconciler(ICloudflareDnsClient client)
    {
        var options = Options.Create(new AcmeCloudflareOptions
        {
            CloudFlareOperationTimeout = TimeSpan.FromSeconds(5),
        });
        var reconciler = new CloudflareDnsReconciler(
            client,
            options,
            NullLogger<CloudflareDnsReconciler>.Instance);
        reconciler.DelayAsync = static (_, _) => Task.CompletedTask;
        return reconciler;
    }

    private static ResolvedBindAddresses Desired(params string[] addresses) =>
        new(addresses.Select(IPAddress.Parse));

    private static CloudflareDnsRecord Record(string id, string type, string content, string? name = null) =>
        new()
        {
            Id = id,
            Type = type,
            Name = name ?? Fqdn,
            Content = content,
            Ttl = CloudflareManagedDnsPolicy.ManagedTtl,
            Proxied = CloudflareManagedDnsPolicy.ManagedProxied,
        };
}
