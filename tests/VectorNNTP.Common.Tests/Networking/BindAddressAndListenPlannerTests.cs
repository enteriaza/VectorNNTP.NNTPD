using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Tests.TestDoubles;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking;
using VectorNNTP.NNTPD.Networking.Listeners;

namespace VectorNNTP.Common.Tests.Networking;

public sealed class BindAddressAndListenPlannerTests
{
    [Fact]
    public void Resolve_ExplicitIpv4AndIpv6()
    {
        var v4 = IPAddress.Parse("198.18.0.66");
        var v6 = IPAddress.Parse("2001:db8::66");
        var resolved = CreateResolver(v4, v6).Resolve(Options([v4.ToString(), v6.ToString()]));

        Assert.Equal(2, resolved.All.Count);
        Assert.Contains(v4, resolved.IPv4);
        Assert.Contains(v6, resolved.IPv6);
    }

    [Fact]
    public void Resolve_WildcardExpandsEligibleAddressesAndDropsLoopback()
    {
        var publicV4 = IPAddress.Parse("203.0.113.10");
        var privateV4 = IPAddress.Parse("10.0.0.5");
        var v6 = IPAddress.Parse("2001:db8::5");
        var resolved = CreateResolver(
                publicV4,
                privateV4,
                v6,
                IPAddress.Loopback,
                IPAddress.Parse("169.254.10.10"))
            .Resolve(Options(["*"]));

        Assert.Equal(3, resolved.All.Count);
        Assert.DoesNotContain(IPAddress.Loopback, resolved.All);
    }

    [Fact]
    public void Resolve_MultipleExplicitAddressesDeduplicate()
    {
        var v4 = IPAddress.Parse("198.18.0.30");
        var resolved = CreateResolver(v4).Resolve(Options([v4.ToString(), v4.ToString(), "*"]));
        Assert.Single(resolved.All);
    }

    [Fact]
    public void Resolve_ExplicitLoopbackIsNotDnsEligible()
    {
        var resolved = CreateResolver(IPAddress.Loopback).Resolve(Options(["127.0.0.1"]));
        Assert.False(resolved.HasAny);
    }

    [Fact]
    public void Validator_RejectsNonLocalExplicitAddress()
    {
        var options = ValidOptions();
        options.BindAddress = ["198.51.100.10"];
        var result = new AcmeCloudflareOptionsValidator(new FakeLocalAssignee(assignAll: false))
            .Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("BindAddress", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validator_RejectsInvalidBindPort(int port)
    {
        var options = ValidOptions();
        options.BindPort = port;
        var result = new AcmeCloudflareOptionsValidator(new FakeLocalAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, static f => f.Contains("BindPort", StringComparison.Ordinal));
    }

    [Fact]
    public void Planner_Star_UsesDualStackIpv6Any()
    {
        var planned = ListenEndpointPlanner.Plan(["*"], 1190);
        Assert.Single(planned);
        Assert.Equal(IPAddress.IPv6Any, planned[0].Address);
        Assert.True(planned[0].DualMode);
        Assert.Equal(1190, planned[0].Port);
    }

    [Fact]
    public void Planner_ExplicitAddress_BindsOnlyThatAddress()
    {
        var planned = ListenEndpointPlanner.Plan(["127.0.0.1"], 1190);
        Assert.Single(planned);
        Assert.Equal(IPAddress.Loopback, planned[0].Address);
        Assert.False(planned[0].DualMode);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("::1", false)]
    public void Eligibility_MatchesNntpdRules(string text, bool expected)
    {
        Assert.Equal(expected, IpAddressEligibility.IsEligibleForDns(IPAddress.Parse(text)));
    }

    private static BindAddressResolver CreateResolver(params IPAddress[] local) =>
        new(new FakeLocalAssignee(local), NullLogger<BindAddressResolver>.Instance);

    private static AcmeCloudflareOptions Options(string[] bind) =>
        new()
        {
            BindAddress = bind,
            BindPort = 1190,
            BindPortTls = 1190,
        };

    private static AcmeCloudflareOptions ValidOptions() =>
        new()
        {
            BindAddress = ["*"],
            BindPort = 1190,
            BindPortTls = 1190,
            AcmeEmail = "ops@example.org",
            AcmeCertificatePassword = TestCertificateFactory.Password,
            CloudFlareApiKey = "unit-test-cloudflare-key-not-secret",
            CloudFlareZoneId = "5811a29d39a0732afb5f160c9b137c3d",
            Fqdn = "backfiller01.usenet.ninja",
            IncludeNewsHostnameInCertificate = false,
        };
}
