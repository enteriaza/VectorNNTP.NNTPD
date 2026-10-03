using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace VectorNNTP.Common.Configuration
{
    /// <summary>
    /// Shared bind, ACME, and Cloudflare settings owned by VectorNNTP.Common.
    /// </summary>
    /// <remarks>
    /// Shared ACME/Cloudflare secrets overlay from the configuration root.
    /// Bind addresses, ports, Cloudflare zone id, and DNS suffix are
    /// application-section configuration (NNTPD: <c>Nntpd</c>; BackFiller:
    /// <c>BackFiller</c>) and are not overlaid from the root. Environment
    /// variables for shared secrets use <see cref="VectorEnvironment.Prefix"/>
    /// only (<c>VECTOR__CLOUDFLAREAPIKEY</c>, <c>VECTOR__ACMECERTIFICATEPASSWORD</c>,
    /// <c>VECTOR__ACMEACCOUNT</c>).
    /// There is no application-specific prefix and no alias for those secrets.
    /// Never log a complete instance: it contains API keys and PFX passwords.
    /// </remarks>
    public class AcmeCloudflareOptions
    {
        /// <summary>
        /// Shared ACME/Cloudflare secrets bind from the configuration root. There is no
        /// application section name for those secrets.
        /// </summary>
        internal const string SectionName = "";

        /// <summary>Configuration key for the Cloudflare API key secret.</summary>
        internal const string CloudFlareApiKeyConfigurationKey = "CloudFlareApiKey";

        /// <summary>Configuration key for the ACME PKCS#12 password secret.</summary>
        internal const string AcmeCertificatePasswordConfigurationKey = "AcmeCertificatePassword";

        /// <summary>Configuration key for the shared ACME account contact email.</summary>
        internal const string AcmeAccountConfigurationKey = "ACMEACCOUNT";

        /// <summary>Configuration key for the Cloudflare zone id.</summary>
        internal const string CloudFlareZoneIdConfigurationKey = "CloudFlareZoneId";

        /// <summary>
        /// Environment variable that supplies <see cref="CloudFlareApiKey"/>
        /// (<c>VECTOR__CLOUDFLAREAPIKEY</c>).
        /// </summary>
        internal const string CloudFlareApiKeyEnvironmentVariable = "VECTOR__CLOUDFLAREAPIKEY";

        /// <summary>
        /// Environment variable that supplies <see cref="AcmeCertificatePassword"/>
        /// (<c>VECTOR__ACMECERTIFICATEPASSWORD</c>).
        /// </summary>
        internal const string AcmeCertificatePasswordEnvironmentVariable = "VECTOR__ACMECERTIFICATEPASSWORD";

        /// <summary>
        /// Environment variable that supplies <see cref="CloudFlareZoneId"/>
        /// (<c>VECTOR__CLOUDFLAREZONEID</c>).
        /// </summary>
        internal const string CloudFlareZoneIdEnvironmentVariable = "VECTOR__CLOUDFLAREZONEID";

        /// <summary>
        /// Environment variable that supplies <see cref="AcmeEmail"/>
        /// (<c>VECTOR__ACMEACCOUNT</c>). Shared by every application using Common ACME.
        /// </summary>
        internal const string AcmeAccountEnvironmentVariable = "VECTOR__ACMEACCOUNT";

        /// <summary>Default Let's Encrypt staging ACME directory URL.</summary>
        internal const string DefaultAcmeDirectoryUrl =
            "https://acme-staging-v02.api.letsencrypt.org/directory";

        /// <summary>Default relative ACME state directory.</summary>
        internal const string DefaultAcmeStateDir = "certs/";

        /// <summary>Default certificate renewal lead time in days.</summary>
        internal const int DefaultAcmeRenewalThresholdDays = 30;

        /// <summary>
        /// Gets or sets the maximum wall-clock duration for a single Cloudflare reconcile or clean-up operation.
        /// </summary>
        public TimeSpan CloudFlareOperationTimeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>Gets or sets the local listen addresses.</summary>
        public string[] BindAddress { get; set; } = [];

        /// <summary>
        /// Gets or sets the cleartext TCP port used by NNTPD.
        /// </summary>
        /// <remarks>
        /// NNTPD validates this property. TLS-only hosts do not supply or consume it.
        /// </remarks>
        public int BindPort { get; set; } = 119;

        /// <summary>
        /// Gets or sets the TLS TCP port.
        /// </summary>
        /// <remarks>
        /// NNTPD: <c>0</c> disables TLS/ACME. BackFiller: must be 1–65535; TLS is required.
        /// </remarks>
        [Range(0, 65535)]
        public int BindPortTls { get; set; }

        /// <summary>Gets whether TLS/ACME configuration is enabled.</summary>
        public bool IsTlsListenerEnabled => BindPortTls > 0;

        /// <summary>Gets or sets the ACME directory URL.</summary>
        public string AcmeDirectoryUrl { get; set; } = DefaultAcmeDirectoryUrl;

        /// <summary>
        /// Gets or sets the ACME account contact email.
        /// </summary>
        /// <remarks>
        /// Populated from shared <see cref="AcmeAccountEnvironmentVariable"/> /
        /// <see cref="AcmeAccountConfigurationKey"/>. Not application-section configuration.
        /// </remarks>
        public string AcmeEmail { get; set; } = string.Empty;

        /// <summary>Gets or sets the ACME account and certificate state directory.</summary>
        public string AcmeStateDir { get; set; } = DefaultAcmeStateDir;

        /// <summary>Gets or sets how many days before expiry a certificate is due for renewal.</summary>
        [Range(1, 90)]
        public int AcmeRenewalThresholdDays { get; set; } = DefaultAcmeRenewalThresholdDays;

        /// <summary>Gets or sets the PKCS#12 password. Secret. Never log.</summary>
        public string AcmeCertificatePassword { get; set; } = string.Empty;

        /// <summary>Gets or sets the Cloudflare API token/key. Secret. Never log.</summary>
        [Required(AllowEmptyStrings = false)]
        public string CloudFlareApiKey { get; set; } = string.Empty;

        /// <summary>Gets or sets the Cloudflare DNS zone identifier.</summary>
        [Required(AllowEmptyStrings = false)]
        public string CloudFlareZoneId { get; set; } = string.Empty;

        /// <summary>Gets or sets the DNS suffix expected to match the Cloudflare zone.</summary>
        public string DnsSuffix { get; set; } = "usenet.ninja";

        /// <summary>
        /// Gets or sets the FQDN published to Cloudflare and requested from ACME.
        /// NNTPD overrides this with the generated <c>nntpdNN</c> name.
        /// </summary>
        public virtual string Fqdn { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether ACME certificates also include <c>news.usenet.ninja</c>.
        /// NNTPD requires <see langword="true"/>. BackFiller must use <see langword="false"/>.
        /// </summary>
        public bool IncludeNewsHostnameInCertificate { get; set; } = true;

        /// <summary>
        /// Returns whether a bind-address entry is a wildcard (all interfaces / any-address).
        /// </summary>
        internal static bool IsBindAddressWildcard(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                return false;
            }

            var trimmed = entry.Trim();
            if (trimmed is "*" or "+")
            {
                return true;
            }

            if (!IPAddress.TryParse(trimmed, out var address))
            {
                return false;
            }

            return address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
        }

        /// <summary>
        /// Overwrites shared ACME/Cloudflare secrets and Cloudflare operation
        /// timeout from the configuration root so application sections cannot
        /// alias those keys. Bind addresses, ports, zone id, and DNS suffix are
        /// left unchanged.
        /// </summary>
        /// <param name="options">The options instance to update.</param>
        /// <param name="configuration">The full configuration root.</param>
        public static void OverlaySharedFromRoot(AcmeCloudflareOptions options, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(configuration);

            options.AcmeDirectoryUrl = configuration[nameof(AcmeDirectoryUrl)] ?? DefaultAcmeDirectoryUrl;
            OverlayAcmeAccountEmail(options, configuration);
            options.AcmeStateDir = configuration[nameof(AcmeStateDir)] ?? DefaultAcmeStateDir;
            options.AcmeRenewalThresholdDays = configuration.GetValue(
                nameof(AcmeRenewalThresholdDays),
                DefaultAcmeRenewalThresholdDays);
            options.AcmeCertificatePassword = configuration[nameof(AcmeCertificatePassword)] ?? string.Empty;
            options.CloudFlareApiKey = configuration[nameof(CloudFlareApiKey)] ?? string.Empty;

            var timeout = configuration[nameof(CloudFlareOperationTimeout)];
            if (!string.IsNullOrWhiteSpace(timeout) && TimeSpan.TryParse(timeout, out var parsed))
            {
                options.CloudFlareOperationTimeout = parsed;
            }
        }

        /// <summary>
        /// Copies the shared ACME account email from <see cref="AcmeAccountConfigurationKey"/>.
        /// Application-section <c>AcmeEmail</c> keys are ignored.
        /// </summary>
        /// <param name="options">The options instance to update.</param>
        /// <param name="configuration">The full configuration root.</param>
        internal static void OverlayAcmeAccountEmail(AcmeCloudflareOptions options, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(configuration);
            options.AcmeEmail = configuration[AcmeAccountConfigurationKey] ?? string.Empty;
        }
    }
}
