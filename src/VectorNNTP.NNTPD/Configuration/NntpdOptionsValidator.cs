using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Validates <see cref="NntpdOptions"/> at options bind / startup time.
/// </summary>
/// <remarks>
/// Does not bind sockets or call Cloudflare APIs. Never includes secret values in failure messages.
/// </remarks>
public sealed class NntpdOptionsValidator : IValidateOptions<NntpdOptions>
{
    private readonly ILocalIpAddressAssignee _localIpAddressAssignee;

    /// <summary>
    /// Initializes a new instance of the <see cref="NntpdOptionsValidator"/> class.
    /// </summary>
    /// <param name="localIpAddressAssignee">Probe used to verify explicit bind addresses.</param>
    public NntpdOptionsValidator(ILocalIpAddressAssignee localIpAddressAssignee)
    {
        ArgumentNullException.ThrowIfNull(localIpAddressAssignee);
        _localIpAddressAssignee = localIpAddressAssignee;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, NntpdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidateApplicationName(options, failures);
        ValidateTimeouts(options, failures);
        ValidateSystemd(options, failures);
        ValidateBindAddresses(options, failures);
        ValidateProxyHosts(options, failures);
        ValidatePorts(options, failures);
        ValidateCloudFlare(options, failures);
        ValidateDnsSuffixAndServerId(options, failures);
        ValidateAcme(options, failures);
        ValidateArticleIngestion(options, failures);
        ValidateTransit(options, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateTransit(NntpdOptions options, List<string> failures)
    {
        var transit = options.Transit ?? new TransitOptions();
        var entries = transit.AllowedPeers;
        if (entries is null)
        {
            failures.Add($"{nameof(NntpdOptions.Transit)}.{nameof(TransitOptions.AllowedPeers)} must not be null.");
            return;
        }

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                failures.Add(
                    $"{nameof(NntpdOptions.Transit)}.{nameof(TransitOptions.AllowedPeers)}[{i}] must not be empty.");
                continue;
            }

            var trimmed = entry.Trim();
            if (trimmed.Contains('/') || trimmed.Contains('*') || trimmed.Contains('+'))
            {
                failures.Add(
                    $"{nameof(NntpdOptions.Transit)}.{nameof(TransitOptions.AllowedPeers)}[{i}] must be a literal IPv4 or IPv6 address (CIDR and wildcards are not supported).");
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var address))
            {
                failures.Add(
                    $"{nameof(NntpdOptions.Transit)}.{nameof(TransitOptions.AllowedPeers)}[{i}] is not a valid IPv4 or IPv6 address.");
                continue;
            }

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                failures.Add(
                    $"{nameof(NntpdOptions.Transit)}.{nameof(TransitOptions.AllowedPeers)}[{i}] must not be an any-address wildcard; use an explicit peer address.");
            }
        }
    }

    private static void ValidateArticleIngestion(NntpdOptions options, List<string> failures)
    {
        var ingestion = options.ArticleIngestion ?? new ArticleIngestionOptions();
        if (string.IsNullOrWhiteSpace(ingestion.IncomingDirectory))
        {
            failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.IncomingDirectory)} must be a non-empty path.");
        }
        else if (ingestion.IncomingDirectory.Length > 512)
        {
            failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.IncomingDirectory)} must be 512 characters or fewer.");
        }

        if (ingestion.QueueCapacity is < 1 or > 100_000)
        {
            failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.QueueCapacity)} must be between 1 and 100000.");
        }

        if (ingestion.MaxArticleBytes is < 1 or > 100 * 1024 * 1024)
        {
            failures.Add($"{nameof(NntpdOptions.ArticleIngestion)}.{nameof(ArticleIngestionOptions.MaxArticleBytes)} must be between 1 and 104857600.");
        }
    }

    private static void ValidateApplicationName(NntpdOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            failures.Add($"{nameof(NntpdOptions.ApplicationName)} must be a non-empty string.");
        }
        else if (options.ApplicationName.Length > 128)
        {
            failures.Add($"{nameof(NntpdOptions.ApplicationName)} must be 128 characters or fewer.");
        }
    }

    private static void ValidateTimeouts(NntpdOptions options, List<string> failures)
    {
        if (options.GracefulShutdownTimeout < TimeSpan.FromSeconds(1))
        {
            failures.Add($"{nameof(NntpdOptions.GracefulShutdownTimeout)} must be at least 1 second.");
        }

        if (options.GracefulShutdownTimeout > TimeSpan.FromHours(1))
        {
            failures.Add($"{nameof(NntpdOptions.GracefulShutdownTimeout)} must not exceed 1 hour.");
        }

        if (options.StartupTimeout is { } startupTimeout)
        {
            if (startupTimeout < TimeSpan.FromSeconds(1))
            {
                failures.Add($"{nameof(NntpdOptions.StartupTimeout)} must be at least 1 second when specified.");
            }

            if (startupTimeout > TimeSpan.FromHours(1))
            {
                failures.Add($"{nameof(NntpdOptions.StartupTimeout)} must not exceed 1 hour.");
            }
        }

        if (options.CloudFlareOperationTimeout < TimeSpan.FromSeconds(1))
        {
            failures.Add($"{nameof(NntpdOptions.CloudFlareOperationTimeout)} must be at least 1 second.");
        }

        if (options.CloudFlareOperationTimeout > TimeSpan.FromHours(1))
        {
            failures.Add($"{nameof(NntpdOptions.CloudFlareOperationTimeout)} must not exceed 1 hour.");
        }
    }

    private static void ValidateSystemd(NntpdOptions options, List<string> failures)
    {
        if (options.Systemd is null)
        {
            failures.Add($"{nameof(NntpdOptions.Systemd)} must be provided.");
            return;
        }

        var fraction = options.Systemd.WatchdogIntervalFraction;
        if (double.IsNaN(fraction) || double.IsInfinity(fraction) || fraction is <= 0 or >= 1)
        {
            failures.Add(
                $"{nameof(NntpdOptions.Systemd)}.{nameof(SystemdOptions.WatchdogIntervalFraction)} must be in the open interval (0, 1).");
        }
        else if (fraction is < 0.05 or > 0.9)
        {
            failures.Add(
                $"{nameof(NntpdOptions.Systemd)}.{nameof(SystemdOptions.WatchdogIntervalFraction)} must be between 0.05 and 0.9 inclusive.");
        }
    }

    private void ValidateBindAddresses(NntpdOptions options, List<string> failures)
    {
        if (options.BindAddress is null || options.BindAddress.Length == 0)
        {
            failures.Add($"{nameof(NntpdOptions.BindAddress)} must contain at least one address or wildcard entry.");
            return;
        }

        for (var i = 0; i < options.BindAddress.Length; i++)
        {
            var entry = options.BindAddress[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                failures.Add($"{nameof(NntpdOptions.BindAddress)}[{i}] must not be empty.");
                continue;
            }

            var trimmed = entry.Trim();
            if (NntpdOptions.IsBindAddressWildcard(trimmed))
            {
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var address))
            {
                failures.Add($"{nameof(NntpdOptions.BindAddress)}[{i}] is not a valid IPv4 or IPv6 address.");
                continue;
            }

            if (!_localIpAddressAssignee.IsLocallyAssigned(address))
            {
                failures.Add(
                    $"{nameof(NntpdOptions.BindAddress)}[{i}] '{FormatAddressForMessage(address)}' is not assigned to any local network interface.");
            }
        }
    }

    private static void ValidateProxyHosts(NntpdOptions options, List<string> failures)
    {
        if (options.ProxyHosts is null)
        {
            return;
        }

        for (var i = 0; i < options.ProxyHosts.Length; i++)
        {
            var entry = options.ProxyHosts[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                failures.Add($"{nameof(NntpdOptions.ProxyHosts)}[{i}] must not be empty.");
                continue;
            }

            var trimmed = entry.Trim();
            if (trimmed.Contains('/') || trimmed.Contains('*'))
            {
                failures.Add(
                    $"{nameof(NntpdOptions.ProxyHosts)}[{i}] must be a literal IPv4 or IPv6 address (CIDR and wildcards are not supported).");
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var address))
            {
                failures.Add($"{nameof(NntpdOptions.ProxyHosts)}[{i}] is not a valid IPv4 or IPv6 address.");
                continue;
            }

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                failures.Add(
                    $"{nameof(NntpdOptions.ProxyHosts)}[{i}] must not be an any-address wildcard; use an explicit proxy peer address.");
            }
        }
    }

    private static void ValidatePorts(NntpdOptions options, List<string> failures)
    {
        if (options.BindPort is < 1 or > 65535)
        {
            failures.Add($"{nameof(NntpdOptions.BindPort)} must be an integer in the range 1–65535.");
        }

        if (options.BindPortTls is < 0 or > 65535)
        {
            failures.Add(
                $"{nameof(NntpdOptions.BindPortTls)} must be 0 (TLS disabled) or an integer in the range 1–65535.");
        }
    }

    private static void ValidateAcme(NntpdOptions options, List<string> failures)
    {
        // Directory URL and state directory are always validated when present so misconfiguration
        // is caught early; email and zone-coverage rules apply only when TLS is enabled.
        if (string.IsNullOrWhiteSpace(options.AcmeDirectoryUrl))
        {
            failures.Add($"{nameof(NntpdOptions.AcmeDirectoryUrl)} must be a non-empty HTTPS ACME directory URL.");
        }
        else if (!Uri.TryCreate(options.AcmeDirectoryUrl.Trim(), UriKind.Absolute, out var directoryUri)
                 || directoryUri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add(
                $"{nameof(NntpdOptions.AcmeDirectoryUrl)} must be an absolute HTTPS URL " +
                "(default is Let's Encrypt staging).");
        }

        if (string.IsNullOrWhiteSpace(options.AcmeStateDir))
        {
            failures.Add($"{nameof(NntpdOptions.AcmeStateDir)} must be a non-empty filesystem path.");
        }

        if (options.AcmeRenewalThresholdDays is < 1 or > 90)
        {
            failures.Add($"{nameof(NntpdOptions.AcmeRenewalThresholdDays)} must be an integer in the range 1–90.");
        }

        if (!options.IsTlsListenerEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.AcmeEmail) || !IsPlausibleEmail(options.AcmeEmail))
        {
            failures.Add(
                $"{nameof(NntpdOptions.AcmeEmail)} is required when {nameof(NntpdOptions.BindPortTls)} > 0 " +
                "and must be a valid contact email address.");
        }

        if (string.IsNullOrWhiteSpace(options.AcmeCertificatePassword))
        {
            failures.Add(
                $"{NntpdOptions.AcmeCertificatePasswordConfigurationKey} is required when {nameof(NntpdOptions.BindPortTls)} > 0 " +
                $"(use environment variable {NntpdOptions.AcmeCertificatePasswordEnvironmentVariable} or secrets; never commit the value).");
        }

        // DNS-01 identities (FQDN + news.usenet.ninja) must fall under DnsSuffix / Cloudflare zone.
        if (options.ServerId is >= 1 and <= 99 && !string.IsNullOrWhiteSpace(options.DnsSuffix))
        {
            try
            {
                var fqdn = NntpdOptions.FormatFqdn(options.ServerId.Value, options.DnsSuffix.Trim().TrimEnd('.'));
                var identities = Acme.CertificateIdentities.ForFqdn(fqdn);
                Acme.DnsZoneCoverage.RequireIdentitiesInDnsZone(identities, options.DnsSuffix);
            }
            catch (Acme.AcmeConfigurationException ex)
            {
                failures.Add(ex.Message);
            }
            catch (ArgumentException ex)
            {
                failures.Add(ex.Message);
            }
        }
    }

    private static bool IsPlausibleEmail(string email)
    {
        var trimmed = email.Trim();
        if (trimmed.Length is 0 or > 254)
        {
            return false;
        }

        var at = trimmed.IndexOf('@');
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
        {
            return false;
        }

        var domain = trimmed[(at + 1)..];
        return domain.Contains('.', StringComparison.Ordinal) && IsValidDnsSuffix(domain);
    }

    private static void ValidateCloudFlare(NntpdOptions options, List<string> failures)
    {
        // Cloudflare DNS integration settings are mandatory for this host configuration.
        // Failure messages never include secret values.
        if (string.IsNullOrWhiteSpace(options.CloudFlareApiKey))
        {
            failures.Add(
                $"{NntpdOptions.CloudFlareApiKeyConfigurationKey} must be configured (use environment variable {NntpdOptions.CloudFlareApiKeyEnvironmentVariable}).");
        }

        if (string.IsNullOrWhiteSpace(options.CloudFlareZoneId))
        {
            failures.Add(
                $"{NntpdOptions.CloudFlareZoneIdConfigurationKey} must be configured (use environment variable {NntpdOptions.CloudFlareZoneIdEnvironmentVariable} or {NntpdOptions.SectionName}:{NntpdOptions.CloudFlareZoneIdConfigurationKey}).");
        }
    }

    private static void ValidateDnsSuffixAndServerId(NntpdOptions options, List<string> failures)
    {
        if (options.ServerId is null)
        {
            failures.Add(
                $"{nameof(NntpdOptions.ServerId)} is required and must be an integer in the range 1–99 (no default; set {NntpdOptions.SectionName}:{nameof(NntpdOptions.ServerId)} or {NntpdOptions.ServerIdEnvironmentVariable}).");
        }
        else if (options.ServerId is < 1 or > 99)
        {
            failures.Add($"{nameof(NntpdOptions.ServerId)} must be an integer in the range 1–99.");
        }

        if (string.IsNullOrWhiteSpace(options.DnsSuffix))
        {
            failures.Add($"{nameof(NntpdOptions.DnsSuffix)} must be a non-empty DNS suffix.");
            return;
        }

        var suffix = options.DnsSuffix.Trim().TrimEnd('.');
        if (!IsValidDnsSuffix(suffix))
        {
            failures.Add($"{nameof(NntpdOptions.DnsSuffix)} is not a syntactically valid DNS name.");
            return;
        }

        // Ensure the generated FQDN stays within DNS length limits without allowing overrides.
        if (options.ServerId is >= 1 and <= 99)
        {
            var fqdn = NntpdOptions.FormatFqdn(options.ServerId.Value, suffix);
            if (fqdn.Length > 253)
            {
                failures.Add($"{nameof(NntpdOptions.DnsSuffix)} produces an FQDN longer than 253 characters.");
            }
        }
    }

    /// <summary>
    /// Validates DNS suffix / name syntax (labels, length, allowed characters).
    /// </summary>
    internal static bool IsValidDnsSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return false;
        }

        var value = suffix.Trim().TrimEnd('.');
        if (value.Length is 0 or > 253)
        {
            return false;
        }

        if (value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var labels = value.Split('.');
        if (labels.Length == 0)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
            {
                return false;
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var ch in label)
            {
                if (!IsDnsLabelChar(ch))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsDnsLabelChar(char ch) =>
        char.IsAsciiLetterOrDigit(ch) || ch == '-';

    private static string FormatAddressForMessage(IPAddress address)
    {
        // Keep messages free of secrets; addresses themselves are not secrets.
        return address.ToString();
    }

    /// <summary>
    /// Returns whether <paramref name="text"/> contains a configured secret (for tests / diagnostics hygiene).
    /// </summary>
    public static bool ContainsSecret(string text, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains(secret, StringComparison.Ordinal);
    }

    /// <summary>
    /// Joins validation failures for assertions without exposing caller-supplied secrets.
    /// </summary>
    public static string JoinFailures(ValidateOptionsResult result)
    {
        if (result.Failures is null)
        {
            return result.FailureMessage ?? string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var failure in result.Failures)
        {
            if (sb.Length > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"; {failure}");
            }
            else
            {
                sb.Append(failure);
            }
        }

        return sb.ToString();
    }
}
