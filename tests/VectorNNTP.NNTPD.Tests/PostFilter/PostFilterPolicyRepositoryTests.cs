using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.NntpDb;
using VectorNNTP.NNTPD.PostFilter;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.PostFilter;

public sealed class PostFilterPolicyRepositoryTests
{
    [Fact]
    public async Task LoadAsync_MapsFactoryRecord()
    {
        var options = new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedAccounts = ["poster"],
            Quota = new PostFilterQuotaOptions { MaxMessagesShort = 100 },
        };
        var factory = new FakeNntpDbConnectionFactory
        {
            PostFilterPolicy = InMemoryPostFilterPolicyRepository.Create(options, revision: 3),
        };
        var nntpDb = CreateStartedDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var repository = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);

        var record = await repository.LoadAsync();
        Assert.Equal(3, record.Revision);
        Assert.Equal(PostFilterGateState.Active, record.Options.Gate);
        Assert.Equal(["poster"], record.Options.DeniedAccounts);
        Assert.Equal(100, record.Options.Quota.MaxMessagesShort);
        Assert.Equal(1, factory.Connections.Sum(static connection => connection.QueryPostFilterPolicyCount));
        await nntpDb.DisposeAsync();
    }

    [Fact]
    public async Task LoadAsync_MissingRow_ThrowsWithoutInventingPolicy()
    {
        var factory = new FakeNntpDbConnectionFactory { PostFilterPolicy = null };
        var nntpDb = CreateStartedDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var repository = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.LoadAsync().AsTask());
        Assert.Contains("nntppostfilterpolicy", ex.Message, StringComparison.Ordinal);
        await nntpDb.DisposeAsync();
    }

    [Fact]
    public async Task LoadAsync_Unavailable_Propagates()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            QueryPostFilterPolicyException = new NntpDbUnavailableException("down"),
        };
        var nntpDb = CreateStartedDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var repository = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);

        await Assert.ThrowsAsync<NntpDbUnavailableException>(() => repository.LoadAsync().AsTask());
        await nntpDb.DisposeAsync();
    }

    [Fact]
    public void Queries_TargetSingletonTables()
    {
        Assert.Contains("FROM nntppostfilterpolicy", NntpPostFilterQueries.SelectPolicy, StringComparison.Ordinal);
        Assert.Contains("policy_id = 1", NntpPostFilterQueries.SelectPolicy, StringComparison.Ordinal);
        Assert.Contains("FROM nntppostfilteraccounts", NntpPostFilterQueries.SelectAccounts, StringComparison.Ordinal);
        Assert.Contains("FROM nntppostfiltercidrs", NntpPostFilterQueries.SelectCidrs, StringComparison.Ordinal);
        Assert.Contains("FROM nntppostfilterarttypes", NntpPostFilterQueries.SelectArtTypes, StringComparison.Ordinal);
        Assert.Contains("FROM nntppostfiltersahosts", NntpPostFilterQueries.SelectHosts, StringComparison.Ordinal);
    }

    private static NntpDbService CreateStartedDb(FakeNntpDbConnectionFactory factory) =>
        new(
            factory,
            Options.Create(new NntpDbOptions
            {
                ConnectionString = TestHostFactory.TestNntpDbConnectionString,
                StartupTimeout = TimeSpan.FromSeconds(15),
            }),
            NullLogger<NntpDbService>.Instance);
}
