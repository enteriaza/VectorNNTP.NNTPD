using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Configuration
{
    /// <summary>
    /// Copies BackFiller-owned bind, ACME, and DNS settings onto Common
    /// <see cref="AcmeCloudflareOptions"/>.
    /// </summary>
    /// <remarks>
    /// Common does not decide where application configuration lives. BackFiller
    /// binds those values from section <see cref="BackFillerOptions.SectionName"/>
    /// and this adapter feeds the reusable ACME/Cloudflare/networking types.
    /// Cloudflare API key, ACME PKCS#12, and ACME account-email secrets remain
    /// root-level <c>VECTOR__*</c> values and are overlaid here. Root-level
    /// <c>BindAddress</c>, <c>BindPort</c>, <c>BindPortTls</c>,
    /// <c>CloudFlareZoneId</c>, <c>DnsSuffix</c>, and ACME directory keys are ignored.
    /// Cleartext <c>BindPort</c> is not a BackFiller setting and is not copied.
    /// </remarks>
    internal static class BackFillerAcmeCloudflareOptionsAdapter
    {
        /// <summary>
        /// Applies nested BackFiller settings and root secrets onto
        /// <paramref name="destination"/>.
        /// </summary>
        /// <param name="destination">Common options instance to populate.</param>
        /// <param name="source">Validated BackFiller application options.</param>
        /// <param name="configuration">Full configuration root (secrets only).</param>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal static void Apply(
            AcmeCloudflareOptions destination,
            BackFillerOptions source,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(configuration);

            destination.BindAddress = source.BindAddress is { Length: > 0 } ? source.BindAddress : [];
            destination.BindPortTls = source.BindPortTls ?? 0;
            destination.AcmeDirectoryUrl = string.IsNullOrWhiteSpace(source.AcmeDirectoryUrl)
                ? AcmeCloudflareOptions.DefaultAcmeDirectoryUrl
                : source.AcmeDirectoryUrl.Trim();
            destination.AcmeRenewalThresholdDays = source.AcmeRenewalThresholdDays;
            destination.AcmeStateDir = ResolveAcmeStateDir(source);
            destination.DnsSuffix = source.DnsSuffix;
            destination.CloudFlareZoneId = source.CloudFlareZoneId;
            destination.Fqdn = source.Fqdn;
            destination.IncludeNewsHostnameInCertificate = false;

            OverlaySecretsFromRoot(destination, configuration);
        }

        /// <summary>
        /// Copies Cloudflare API key, ACME PKCS#12, and ACME account-email secrets
        /// from the configuration root. Does not copy bind, zone id, DNS suffix, or
        /// ACME directory settings.
        /// </summary>
        /// <param name="destination">Common options instance to update.</param>
        /// <param name="configuration">Full configuration root.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        /// <remarks>
        /// Missing secret keys are stored as empty strings. <see cref="AcmeCloudflareOptions.OverlayAcmeAccountEmail"/>
        /// applies the account email. A non-empty <see cref="AcmeCloudflareOptions.CloudFlareOperationTimeout"/>
        /// value that <see cref="TimeSpan.TryParse(string, out TimeSpan)"/> accepts replaces the destination timeout;
        /// a blank or unparseable value leaves it unchanged.
        /// </remarks>
        private static void OverlaySecretsFromRoot(AcmeCloudflareOptions destination, IConfiguration configuration)
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

        /// <summary>
        /// Chooses the ACME state directory copied onto Common options.
        /// </summary>
        /// <param name="source">BackFiller options. The caller has already rejected null.</param>
        /// <returns>
        /// Trimmed <see cref="BackFillerOptions.AcmeStateDir"/> when it is not white space;
        /// otherwise trimmed <see cref="BackFillerOptions.CertificateDirectory"/> when that is not white space;
        /// otherwise <see cref="AcmeCloudflareOptions.DefaultAcmeStateDir"/>.
        /// </returns>
        private static string ResolveAcmeStateDir(BackFillerOptions source)
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
}
