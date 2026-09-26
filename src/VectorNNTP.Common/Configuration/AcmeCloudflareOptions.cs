using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Shared bind, ACME, and Cloudflare settings owned by VectorNNTP.Common.
/// </summary>
/// <remarks>
/// Bound from the configuration root (application-neutral). NNTPD and BackFiller
/// consume the same contract. Environment variables use
/// <see cref="VectorEnvironment.Prefix"/> only
/// (<c>VECTOR__CLOUDFLAREAPIKEY</c>, <c>VECTOR__ACMECERTIFICATEPASSWORD</c>,
/// <c>VECTOR__CLOUDFLAREZONEID</c>, <c>VECTOR__BINDADDRESS</c>,
/// <c>VECTOR__BINDPORT</c>, <c>VECTOR__BINDPORTTLS</c>). There is no
/// application-specific prefix and no alias.
/// Never log a complete instance: it contains API keys and PFX passwords.
/// </remarks>
public class AcmeCloudflareOptions
{
    /// <summary>
    /// Shared settings bind from the configuration root. There is no application section name.
    /// </summary>
    public const string SectionName = "";

    /// <summary>Configuration key for the Cloudflare API key secret.</summary>
    public const string CloudFlareApiKeyConfigurationKey = "CloudFlareApiKey";

    /// <summary>Configuration key for the ACME PKCS#12 password secret.</summary>
    public const string AcmeCertificatePasswordConfigurationKey = "AcmeCertificatePassword";

    /// <summary>Configuration key for the Cloudflare zone id.</summary>
    public const string CloudFlareZoneIdConfigurationKey = "CloudFlareZoneId";

    /// <summary>
    /// Environment variable that supplies <see cref="CloudFlareApiKey"/>
    /// (<c>VECTOR__CLOUDFLAREAPIKEY</c>).
    /// </summary>
    public const string CloudFlareApiKeyEnvironmentVariable = "VECTOR__CLOUDFLAREAPIKEY";

    /// <summary>
    /// Environment variable that supplies <see cref="AcmeCertificatePassword"/>
    /// (<c>VECTOR__ACMECERTIFICATEPASSWORD</c>).
    /// </summary>
    public const string AcmeCertificatePasswordEnvironmentVariable = "VECTOR__ACMECERTIFICATEPASSWORD";

    /// <summary>
    /// Environment variable that supplies <see cref="CloudFlareZoneId"/>
    /// (<c>VECTOR__CLOUDFLAREZONEID</c>).
    /// </summary>
    public const string CloudFlareZoneIdEnvironmentVariable = "VECTOR__CLOUDFLAREZONEID";

    /// <summary>
    /// Environment variable that supplies <see cref="BindAddress"/>
    /// (<c>VECTOR__BINDADDRESS</c>).
    /// </summary>
    public const string BindAddressEnvironmentVariable = "VECTOR__BINDADDRESS";

    /// <summary>
    /// Environment variable that supplies <see cref="BindPort"/>
    /// (<c>VECTOR__BINDPORT</c>).
    /// </summary>
    public const string BindPortEnvironmentVariable = "VECTOR__BINDPORT";

    /// <summary>
    /// Environment variable that supplies <see cref="BindPortTls"/>
    /// (<c>VECTOR__BINDPORTTLS</c>).
    /// </summary>
    public const string BindPortTlsEnvironmentVariable = "VECTOR__BINDPORTTLS";

    /// <summary>Default Let's Encrypt staging ACME directory URL.</summary>
    public const string DefaultAcmeDirectoryUrl =
        "https://acme-staging-v02.api.letsencrypt.org/directory";

    /// <summary>Default relative ACME state directory.</summary>
    public const string DefaultAcmeStateDir = "certs/";

    /// <summary>Default certificate renewal lead time in days.</summary>
    public const int DefaultAcmeRenewalThresholdDays = 30;

    /// <summary>
    /// Gets or sets the maximum wall-clock duration for a single Cloudflare reconcile or clean-up operation.
    /// </summary>
    public TimeSpan CloudFlareOperationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets the local listen addresses.</summary>
    public string[] BindAddress { get; set; } = [];

    /// <summary>Gets or sets the cleartext TCP port used by NNTPD. BackFiller does not listen on this port.</summary>
    [Range(1, 65535)]
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

    /// <summary>Gets or sets the ACME account contact email.</summary>
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
    public static bool IsBindAddressWildcard(string entry)
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
    /// Overwrites shared properties from the configuration root so application
    /// sections cannot alias these keys.
    /// </summary>
    /// <param name="options">The options instance to update.</param>
    /// <param name="configuration">The full configuration root.</param>
    public static void OverlaySharedFromRoot(AcmeCloudflareOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        options.BindAddress = configuration.GetSection(nameof(BindAddress)).Get<string[]>() ?? [];
        options.BindPort = configuration.GetValue(nameof(BindPort), 119);
        options.BindPortTls = configuration.GetValue(nameof(BindPortTls), 0);
        options.AcmeDirectoryUrl = configuration[nameof(AcmeDirectoryUrl)] ?? DefaultAcmeDirectoryUrl;
        options.AcmeEmail = configuration[nameof(AcmeEmail)] ?? string.Empty;
        options.AcmeStateDir = configuration[nameof(AcmeStateDir)] ?? DefaultAcmeStateDir;
        options.AcmeRenewalThresholdDays = configuration.GetValue(
            nameof(AcmeRenewalThresholdDays),
            DefaultAcmeRenewalThresholdDays);
        options.AcmeCertificatePassword = configuration[nameof(AcmeCertificatePassword)] ?? string.Empty;
        options.CloudFlareApiKey = configuration[nameof(CloudFlareApiKey)] ?? string.Empty;
        options.CloudFlareZoneId = configuration[nameof(CloudFlareZoneId)] ?? string.Empty;
        options.DnsSuffix = configuration[nameof(DnsSuffix)] ?? "usenet.ninja";

        var timeout = configuration[nameof(CloudFlareOperationTimeout)];
        if (!string.IsNullOrWhiteSpace(timeout) && TimeSpan.TryParse(timeout, out var parsed))
        {
            options.CloudFlareOperationTimeout = parsed;
        }
    }
}
