using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.PostFilter;

namespace VectorNNTP.NNTPD.Tests.PostFilter;

public sealed class PostFilterPolicyServiceTests
{
    [Fact]
    public async Task StartAsync_PublishesCompiledSnapshot()
    {
        var options = new MutableOptions(new NntpdOptions());
        await using var service = new PostFilterPolicyService(
            options,
            NullLogger<PostFilterPolicyService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.HasSnapshot);
        Assert.Equal(PostFilterGateState.Disabled, service.Current.Gate);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_InvalidPolicy_ThrowsAndDoesNotPublish()
    {
        var options = new MutableOptions(new NntpdOptions
        {
            PostFilter = new PostFilterOptions
            {
                SpamAssassin = new PostFilterSpamAssassinOptions
                {
                    Enabled = true,
                    Hosts = ["127.0.0.1"],
                },
            },
        });
        await using var service = new PostFilterPolicyService(
            options,
            NullLogger<PostFilterPolicyService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.HasSnapshot);
    }

    [Fact]
    public async Task RefreshFailure_RetainsLastKnownGood()
    {
        var options = new MutableOptions(new NntpdOptions
        {
            PostFilter = new PostFilterOptions { Gate = PostFilterGateState.Closed },
        });
        var time = new FakeTimeProvider();
        await using var service = new PostFilterPolicyService(
            options,
            NullLogger<PostFilterPolicyService>.Instance,
            time,
            TimeSpan.FromMinutes(5));
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(PostFilterGateState.Closed, service.Current.Gate);

        options.CurrentValue = new NntpdOptions
        {
            PostFilter = new PostFilterOptions { Gate = PostFilterGateState.Active },
        };
        time.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => service.Current.Gate == PostFilterGateState.Active);

        options.CurrentValue = new NntpdOptions
        {
            PostFilter = new PostFilterOptions
            {
                SpamAssassin = new PostFilterSpamAssassinOptions
                {
                    Enabled = true,
                    Hosts = ["127.0.0.1"],
                },
            },
        };
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Yield();
        await Task.Yield();
        Assert.Equal(PostFilterGateState.Active, service.Current.Gate);
        await service.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed class MutableOptions : IOptionsMonitor<NntpdOptions>
    {
        public MutableOptions(NntpdOptions value) => CurrentValue = value;

        public NntpdOptions CurrentValue { get; set; }

        public NntpdOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NntpdOptions, string?> listener) => null;
    }
}
