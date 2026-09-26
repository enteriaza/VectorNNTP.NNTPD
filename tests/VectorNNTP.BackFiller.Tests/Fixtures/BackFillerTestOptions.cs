using System.Net;
using Microsoft.Extensions.Configuration;
using VectorNNTP.BackFiller.Configuration;

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
            LetsEncrypt =
            {
                CloudFlareApiToken = SecretToken,
                CloudFlareZoneId = "0123456789abcdef0123456789abcdef",
                PfxExportPassword = SecretPfx,
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
            ["BackFiller:BindAddress:0"] = "127.0.0.1",
            ["BackFiller:BindPort"] = "1190",
            ["BackFiller:LogDirectory"] = "logs",
            ["BackFiller:CertificateDirectory"] = "certs",
            ["BackFiller:RabbitMQ:Hosts:0"] = "127.0.0.1",
            ["BackFiller:RabbitMQ:Username"] = "nntparticles",
            ["BackFiller:RabbitMQ:Password"] = SecretPassword,
            ["BackFiller:RabbitMQ:EnableSsl"] = "false",
            ["BackFiller:LetsEncrypt:CloudFlareApiToken"] = SecretToken,
            ["BackFiller:LetsEncrypt:CloudFlareZoneId"] = "0123456789abcdef0123456789abcdef",
            ["BackFiller:LetsEncrypt:PfxExportPassword"] = SecretPfx,
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

    internal static BackFillerOptionsValidator CreateValidator(
        ILocalIpAddressAssignee? assignee = null,
        IPhysicalMemoryProvider? memory = null)
    {
        return new BackFillerOptionsValidator(
            assignee ?? new FakeLocalIpAddressAssignee(assignAll: true),
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
}

internal sealed class FakePhysicalMemoryProvider(long totalBytes) : IPhysicalMemoryProvider
{
    public long GetTotalPhysicalMemoryBytes() => totalBytes;
}
