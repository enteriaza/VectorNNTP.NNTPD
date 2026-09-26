using System.Net;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Acme;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Validates shared bind, ACME, and Cloudflare settings.
/// </summary>
/// <remarks>
/// Does not bind sockets or call Cloudflare. Failure messages never include secret values.
/// </remarks>
public sealed class AcmeCloudflareOptionsValidator : IValidateOptions<AcmeCloudflareOptions>
{
    private readonly ILocalIpAddressAssignee _localIpAddressAssignee;

    /// <summary>Initializes a new validator.</summary>
    public AcmeCloudflareOptionsValidator(ILocalIpAddressAssignee localIpAddressAssignee)
    {
        ArgumentNullException.ThrowIfNull(localIpAddressAssignee);
        _localIpAddressAssignee = localIpAddressAssignee;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AcmeCloudflareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        CollectFailures(options, _localIpAddressAssignee, failures, validateCertificateZoneCoverage: true);
        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Collects shared validation failures without constructing an options result.
    /// </summary>
    public static void CollectFailures(
        AcmeCloudflareOptions options,
        ILocalIpAddressAssignee localIpAddressAssignee,
        List<string> failures,
        bool validateCertificateZoneCoverage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(localIpAddressAssignee);
        ArgumentNullException.ThrowIfNull(failures);

        ValidateCloudFlareTimeout(options, failures);
        ValidateBindAddresses(options, localIpAddressAssignee, failures);
        ValidatePorts(options, failures);
        ValidateCloudFlare(options, failures);
        ValidateDnsSuffix(options, failures);
        ValidateAcme(options, failures, validateCertificateZoneCoverage);
    }

    /// <summary>Normalizes empty bind lists to a single wildcard and trims entries.</summary>
    public static void NormalizeBindAddresses(AcmeCloudflareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BindAddress is null || options.BindAddress.Length == 0)
        {
            options.BindAddress = ["*"];
            return;
        }

        for (var i = 0; i < options.BindAddress.Length; i++)
        {
            options.BindAddress[i] = options.BindAddress[i]?.Trim() ?? string.Empty;
        }
    }

    /// <summary>Resolves a relative ACME state directory against <paramref name="contentRootPath"/>.</summary>
    public static string ResolveAcmeStateDir(string acmeStateDir, string? contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acmeStateDir);
        var trimmed = acmeStateDir.Trim();
        var root = string.IsNullOrWhiteSpace(contentRootPath)
            ? AppContext.BaseDirectory
            : contentRootPath;
        return Path.GetFullPath(trimmed, Path.GetFullPath(root))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void ValidateCloudFlareTimeout(AcmeCloudflareOptions options, List<string> failures)
    {
        if (options.CloudFlareOperationTimeout < TimeSpan.FromSeconds(1))
        {
            failures.Add($"{nameof(AcmeCloudflareOptions.CloudFlareOperationTimeout)} must be at least 1 second.");
        }

        if (options.CloudFlareOperationTimeout > TimeSpan.FromHours(1))
        {
            failures.Add($"{nameof(AcmeCloudflareOptions.CloudFlareOperationTimeout)} must not exceed 1 hour.");
        }
    }

    private static void ValidateBindAddresses(
        AcmeCloudflareOptions options,
        ILocalIpAddressAssignee localIpAddressAssignee,
        List<string> failures)
    {
        if (options.BindAddress is null || options.BindAddress.Length == 0)
        {
            failures.Add($"{nameof(AcmeCloudflareOptions.BindAddress)} must contain at least one address or wildcard entry.");
            return;
        }

        for (var i = 0; i < options.BindAddress.Length; i++)
        {
            var entry = options.BindAddress[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                failures.Add($"{nameof(AcmeCloudflareOptions.BindAddress)}[{i}] must not be empty.");
                continue;
            }

            var trimmed = entry.Trim();
            if (AcmeCloudflareOptions.IsBindAddressWildcard(trimmed))
            {
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var address))
            {
                failures.Add($"{nameof(AcmeCloudflareOptions.BindAddress)}[{i}] is not a valid IPv4 or IPv6 address.");
                continue;
            }

            if (!localIpAddressAssignee.IsLocallyAssigned(address))
            {
                failures.Add(
                    $"{nameof(AcmeCloudflareOptions.BindAddress)}[{i}] '{address}' is not assigned to any local network interface.");
            }
        }
    }

    private static void ValidatePorts(AcmeCloudflareOptions options, List<string> failures)
    {
        if (options.BindPort is < 1 or > 65535)
        {
            failures.Add($"{nameof(AcmeCloudflareOptions.BindPort)} must be an integer in the range 1–65535.");
        }

        if (options.BindPortTls is < 0 or > 65535)
        {
            failures.Add(
                $"{nameof(AcmeCloudflareOptions.BindPortTls)} must be 0 (TLS disabled) or an integer in the range 1–65535.");
        }
    }

    private static void ValidateAcme(
        AcmeCloudflareOptions options,
        List<string> failures,
        bool validateCertificateZoneCoverage)
    {
        if (string.IsNullOrWhiteSpace(options.AcmeDirectoryUrl))
        {
            failures.Add($"{nameof(AcmeCloudflareOptions.AcmeDirectoryUrl)} must be a non-empty HTTPS ACME directory URL.");
        }
        else if (!Uri.TryCreate(options.AcmeDirectoryUrl.Trim(), UriKind.Absolute, out var directoryUri)
                 || directoryUri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add(
                $"{nameof(AcmeCloudflareOptions.AcmeDirectoryUrl)} must be an absolute HTTPS URL " +
                "(default is Let's Encrypt staging).");
        }

        if (string.IsNullOrWhiteSpace(options.AcmeStateDir))
        {
            failures.Add($"{nameof(AcmeCloudflareOptions.AcmeStateDir)} must be a non-empty filesystem path.");
        }

        if (options.AcmeRenewalThresholdDays is < 1 or > 90)
        {
            failures.Add($"{nameof(AcmeCloudflareOptions.AcmeRenewalThresholdDays)} must be an integer in the range 1–90.");
        }

        if (!options.IsTlsListenerEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.AcmeEmail) || !IsPlausibleEmail(options.AcmeEmail))
        {
            failures.Add(
                $"{nameof(AcmeCloudflareOptions.AcmeEmail)} is required when {nameof(AcmeCloudflareOptions.BindPortTls)} > 0 " +
                "and must be a valid contact email address.");
        }

        if (string.IsNullOrWhiteSpace(options.AcmeCertificatePassword))
        {
            failures.Add(
                $"{AcmeCloudflareOptions.AcmeCertificatePasswordConfigurationKey} is required when {nameof(AcmeCloudflareOptions.BindPortTls)} > 0 " +
                $"(use environment variable {AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable} or secrets; never commit the value).");
        }

        if (!validateCertificateZoneCoverage || string.IsNullOrWhiteSpace(options.Fqdn) || string.IsNullOrWhiteSpace(options.DnsSuffix))
        {
            return;
        }

        try
        {
            var identities = CertificateIdentities.ForFqdn(options.Fqdn, options.IncludeNewsHostnameInCertificate);
            DnsZoneCoverage.RequireIdentitiesInDnsZone(identities, options.DnsSuffix);
        }
        catch (AcmeConfigurationException ex)
        {
            failures.Add(ex.Message);
        }
        catch (ArgumentException ex)
        {
            failures.Add(ex.Message);
        }
    }

    private static void ValidateCloudFlare(AcmeCloudflareOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.CloudFlareApiKey))
        {
            failures.Add(
                $"{AcmeCloudflareOptions.CloudFlareApiKeyConfigurationKey} must be configured (use environment variable {AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable}).");
        }

        if (string.IsNullOrWhiteSpace(options.CloudFlareZoneId))
        {
            failures.Add(
                $"{AcmeCloudflareOptions.CloudFlareZoneIdConfigurationKey} must be configured (use environment variable {AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable} or root key {AcmeCloudflareOptions.CloudFlareZoneIdConfigurationKey}).");
        }
    }

    private static void ValidateDnsSuffix(AcmeCloudflareOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.DnsSuffix))
        {
            failures.Add($"{nameof(AcmeCloudflareOptions.DnsSuffix)} must be a non-empty DNS suffix.");
            return;
        }

        var suffix = options.DnsSuffix.Trim().TrimEnd('.');
        if (!NntpdDnsName.IsValidSuffix(suffix))
        {
            failures.Add($"{nameof(AcmeCloudflareOptions.DnsSuffix)} is not a syntactically valid DNS name.");
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
        return domain.Contains('.', StringComparison.Ordinal) && NntpdDnsName.IsValidSuffix(domain);
    }
}

/// <summary>Shared DNS name syntax used by ACME/Cloudflare configuration validation.</summary>
public static class NntpdDnsName
{
    /// <summary>Validates DNS suffix / name syntax (labels, length, allowed characters).</summary>
    public static bool IsValidSuffix(string suffix)
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
                if (!char.IsAsciiLetterOrDigit(ch) && ch != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }
}
