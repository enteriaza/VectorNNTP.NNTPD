using System.Net;
using Microsoft.Extensions.Configuration;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Tests.Fixtures;

internal static class BackFillerTestOptions
{
    internal const string SecretPassword = "super-secret-password-xyz";
    internal const string SecretToken = "super-secret-cloudflare-token-xyz";
    internal const string SecretPfx = "super-secret-pfx-password-xyz";

    internal static BackFillerOptions CreateValid()
    {
        return new BackFillerOptions
        {
            Name = "backfiller",
            ServerId = 1,
            DnsSuffix = "usenet.ninja",
            BindAddress = ["127.0.0.1"],
            BindPort = 1190,
            LogDirectory = "logs",
            CertificateDirectory = "certs",
            RabbitMQ =
            {
                Hosts = ["127.0.0.1"],
                Username = "nntparticles",
                Password = SecretPassword,
                EnableSsl = false,
            },
        };
    }

    internal static Dictionary<string, string?> CreateValidConfigurationPairs()
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["BackFiller:Name"] = "backfiller",
            ["BackFiller:ServerId"] = "1",
            ["BackFiller:DnsSuffix"] = "usenet.ninja",
            ["BackFiller:LogDirectory"] = "logs",
            ["RabbitMQ:Hosts:0"] = "127.0.0.1",
            ["RabbitMQ:Username"] = "nntparticles",
            ["RabbitMQ:Password"] = SecretPassword,
            ["RabbitMQ:EnableSsl"] = "false",
            ["BindAddress:0"] = "127.0.0.1",
            ["BindPort"] = "1190",
            ["BindPortTls"] = "1190",
            ["AcmeEmail"] = "security@usenet.ninja",
            ["AcmeCertificatePassword"] = SecretPfx,
            ["AcmeStateDir"] = "certs/",
            ["CloudFlareApiKey"] = SecretToken,
            ["CloudFlareZoneId"] = "0123456789abcdef0123456789abcdef",
            ["DnsSuffix"] = "usenet.ninja",
            ["BackFiller:Shutdown:GracePeriodSeconds"] = "45",
            ["ConnectionStrings:GrabberDB"] = "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz",
        };
    }

    internal static IConfiguration CreateValidConfiguration(IReadOnlyDictionary<string, string?>? overlays = null)
    {
        var pairs = CreateValidConfigurationPairs();
        if (overlays is not null)
        {
            foreach (var (key, value) in overlays)
            {
                pairs[key] = value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build();
    }

    internal static BackFillerConnectionStringsOptions CreateValidConnectionStrings()
    {
        return new BackFillerConnectionStringsOptions
        {
            GrabberDB = "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz",
        };
    }

    internal static AcmeCloudflareOptions CreateValidAcme(BackFillerOptions? options = null)
    {
        var identity = options ?? CreateValid();
        return new AcmeCloudflareOptions
        {
            BindAddress = identity.BindAddress is { Length: > 0 } ? identity.BindAddress : ["127.0.0.1"],
            BindPort = identity.BindPort ?? 1190,
            BindPortTls = identity.BindPort ?? 1190,
            Fqdn = identity.Fqdn,
            IncludeNewsHostnameInCertificate = false,
            AcmeEmail = "security@usenet.ninja",
            AcmeCertificatePassword = SecretPfx,
            AcmeStateDir = string.IsNullOrWhiteSpace(identity.CertificateDirectory) ? "certs/" : identity.CertificateDirectory,
            CloudFlareApiKey = SecretToken,
            CloudFlareZoneId = "0123456789abcdef0123456789abcdef",
            DnsSuffix = identity.DnsSuffix,
        };
    }

    internal static BackFillerOptionsValidator CreateValidator(
        ILocalIpAddressAssignee? assignee = null,
        IPhysicalMemoryProvider? memory = null)
    {
        _ = assignee;
        return new BackFillerOptionsValidator(
            memory ?? new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
    }
}

internal sealed class FakeLocalIpAddressAssignee(bool assignAll) : ILocalIpAddressAssignee
{
    public bool IsLocallyAssigned(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return assignAll;
    }

    public IReadOnlyList<IPAddress> GetAssignedUnicastAddresses() =>
        assignAll ? [IPAddress.Parse("198.18.0.10"), IPAddress.Parse("2001:db8::10")] : [];
}

internal sealed class FakePhysicalMemoryProvider(long totalBytes) : IPhysicalMemoryProvider
{
    public long GetTotalPhysicalMemoryBytes() => totalBytes;
}
