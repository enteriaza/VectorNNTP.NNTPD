using Microsoft.Extensions.Configuration;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Applies root-level <c>VECTOR__</c> secrets without replacing NNTPD-owned
/// bind addresses, ports, or ACME state directory. ACME directory, renewal
/// threshold, Cloudflare zone id, and DNS suffix are published from
/// <c>nntpsharedconfig</c>. The ACME account email is shared Common configuration
/// (<see cref="AcmeCloudflareOptions.AcmeAccountEnvironmentVariable"/>).
/// </summary>
internal static class NntpdAcmeCloudflareOptionsOverlay
{
    /// <summary>
    /// Overlays shared root secrets, then restores
    /// <see cref="AcmeCloudflareOptions.AcmeStateDir"/> bound from <c>Nntpd</c>.
    /// Bind addresses and ports remain the values bound from <c>Nntpd</c>.
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

        var stateDir = options.AcmeStateDir;

        AcmeCloudflareOptions.OverlaySharedFromRoot(options, configuration);

        options.AcmeStateDir = stateDir;
    }
}
