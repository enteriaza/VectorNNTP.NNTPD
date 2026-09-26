using Microsoft.Extensions.Configuration;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Applies root-level shared bind, DNS, and <c>VECTOR__</c> secrets without replacing
/// NNTPD-owned ACME directory, renewal threshold, or state directory.
/// The ACME account email is shared Common configuration
/// (<see cref="AcmeCloudflareOptions.AcmeAccountEnvironmentVariable"/>).
/// </summary>
internal static class NntpdAcmeCloudflareOptionsOverlay
{
    /// <summary>
    /// Overlays shared root secrets and bind/DNS settings, then restores
    /// <see cref="NntpdOptions"/> values bound from <c>Nntpd</c> for
    /// <see cref="AcmeCloudflareOptions.AcmeDirectoryUrl"/>,
    /// <see cref="AcmeCloudflareOptions.AcmeRenewalThresholdDays"/>, and
    /// <see cref="AcmeCloudflareOptions.AcmeStateDir"/>.
    /// The ACME account email always comes from
    /// <see cref="AcmeCloudflareOptions.AcmeAccountConfigurationKey"/>.
    /// </summary>
    /// <param name="options">The NNTPD options already bound from the <c>Nntpd</c> section.</param>
    /// <param name="configuration">The full configuration root, including <c>VECTOR__</c> secrets.</param>
    public static void OverlaySharedFromRootPreservingApplicationAcme(
        NntpdOptions options,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        var directoryUrl = options.AcmeDirectoryUrl;
        var stateDir = options.AcmeStateDir;
        var renewalDays = options.AcmeRenewalThresholdDays;

        AcmeCloudflareOptions.OverlaySharedFromRoot(options, configuration);

        options.AcmeDirectoryUrl = directoryUrl;
        options.AcmeStateDir = stateDir;
        options.AcmeRenewalThresholdDays = renewalDays;
    }
}
