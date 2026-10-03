using Microsoft.Extensions.Configuration;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Copies StorageServer-owned bind, ACME, and DNS settings onto Common
/// <see cref="AcmeCloudflareOptions"/>.
/// </summary>
/// <remarks>
/// <see cref="AcmeCloudflareOptions.IncludeNewsHostnameInCertificate"/> is always
/// <see langword="false"/>. Cleartext <see cref="AcmeCloudflareOptions.BindPort"/>
/// is copied from <see cref="StorageServerOptions.BindPort"/> (default 0) and is
/// never listened on by StorageServer.
/// </remarks>
public static class StorageServerAcmeCloudflareOptionsAdapter
{
    /// <summary>
    /// Applies nested StorageServer settings and root secrets onto
    /// <paramref name="destination"/>.
    /// </summary>
    public static void Apply(
        AcmeCloudflareOptions destination,
        StorageServerOptions source,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);

        destination.BindAddress = source.BindAddress is { Length: > 0 } ? source.BindAddress : [];
        destination.BindPort = source.BindPort;
        destination.BindPortTls = source.BindPortTls ?? 0;
        destination.AcmeDirectoryUrl = string.IsNullOrWhiteSpace(source.AcmeDirectoryUrl)
            ? AcmeCloudflareOptions.DefaultAcmeDirectoryUrl
            : source.AcmeDirectoryUrl.Trim();
        destination.AcmeRenewalThresholdDays = source.AcmeRenewalThresholdDays;
        destination.AcmeStateDir = ResolveAcmeStateDir(source);
        destination.DnsSuffix = source.DnsSuffix;
        destination.CloudFlareZoneId = source.CloudFlareZoneId ?? string.Empty;
        destination.Fqdn = source.Fqdn;
        destination.IncludeNewsHostnameInCertificate = false;

        OverlaySecretsFromRoot(destination, configuration);
    }

    /// <summary>
    /// Copies Cloudflare API key, ACME PKCS#12, and ACME account-email secrets
    /// from the configuration root.
    /// </summary>
    public static void OverlaySecretsFromRoot(AcmeCloudflareOptions destination, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(configuration);

        destination.AcmeCertificatePassword =
            configuration[nameof(AcmeCloudflareOptions.AcmeCertificatePassword)] ?? string.Empty;
        destination.CloudFlareApiKey =
            configuration[nameof(AcmeCloudflareOptions.CloudFlareApiKey)] ?? string.Empty;
        AcmeCloudflareOptions.OverlayAcmeAccountEmail(destination, configuration);

        var timeout = configuration[nameof(AcmeCloudflareOptions.CloudFlareOperationTimeout)];
        if (!string.IsNullOrWhiteSpace(timeout) && TimeSpan.TryParse(timeout, out var parsed))
        {
            destination.CloudFlareOperationTimeout = parsed;
        }
    }

    private static string ResolveAcmeStateDir(StorageServerOptions source)
    {
        if (!string.IsNullOrWhiteSpace(source.AcmeStateDir))
        {
            return source.AcmeStateDir.Trim();
        }

        return string.IsNullOrWhiteSpace(source.CertificateDirectory)
            ? AcmeCloudflareOptions.DefaultAcmeStateDir
            : source.CertificateDirectory.Trim();
    }
}
