using System.Net;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Acme;

namespace VectorNNTP.Common.Configuration
{
    /// <summary>
    /// Validates shared bind, ACME, and Cloudflare settings.
    /// </summary>
    /// <remarks>
    /// Does not bind sockets or call Cloudflare. Failure messages never include secret values.
    /// </remarks>
    internal sealed class AcmeCloudflareOptionsValidator : IValidateOptions<AcmeCloudflareOptions>
    {
        /// <summary>NIC assignment check used for explicit bind addresses. Wildcards are not checked here.</summary>
        private readonly ILocalIpAddressAssignee _localIpAddressAssignee;

        /// <summary>Initializes a new validator.</summary>
        /// <param name="localIpAddressAssignee">Reports whether an explicit bind address is assigned to a local interface.</param>
        public AcmeCloudflareOptionsValidator(ILocalIpAddressAssignee localIpAddressAssignee)
        {
            ArgumentNullException.ThrowIfNull(localIpAddressAssignee);
            _localIpAddressAssignee = localIpAddressAssignee;
        }

        /// <summary>
        /// Validates bind addresses, ports, Cloudflare credentials, DNS suffix, ACME settings, and certificate zone coverage.
        /// </summary>
        /// <param name="name">Named-options name. This validator does not branch on it.</param>
        /// <param name="options">Options instance to validate.</param>
        /// <returns>Success, or a failure list that does not include secret values.</returns>
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
        /// <param name="options">Options instance to validate.</param>
        /// <param name="localIpAddressAssignee">NIC assignment check for explicit bind addresses.</param>
        /// <param name="failures">Destination list. Existing entries are kept.</param>
        /// <param name="validateCertificateZoneCoverage">
        /// When <see langword="true"/> and TLS is enabled, certificate identities must fall inside <see cref="AcmeCloudflareOptions.DnsSuffix"/>.
        /// </param>
        private static void CollectFailures(
            AcmeCloudflareOptions options,
            ILocalIpAddressAssignee localIpAddressAssignee,
            List<string> failures,
            bool validateCertificateZoneCoverage)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(localIpAddressAssignee);
            ArgumentNullException.ThrowIfNull(failures);

            ValidateCloudFlareTimeout(options, failures);
            CollectBindAddressFailures(options, localIpAddressAssignee, failures);
            ValidatePorts(options, failures);
            ValidateCloudFlare(options, failures);
            ValidateDnsSuffix(options, failures);
            ValidateAcme(options, failures, validateCertificateZoneCoverage);
        }

        /// <summary>Normalizes empty bind lists to a single wildcard and trims entries.</summary>
        /// <param name="options">Options whose <see cref="AcmeCloudflareOptions.BindAddress"/> array is replaced or trimmed in place.</param>
        internal static void NormalizeBindAddresses(AcmeCloudflareOptions options)
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

        /// <summary>
        /// Resolves an ACME state directory through
        /// <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/>.
        /// </summary>
        /// <param name="acmeStateDir">Configured ACME state directory (relative or absolute).</param>
        /// <param name="applicationBaseDirectory">
        /// Application binary directory. Production callers must pass
        /// <see cref="AppContext.BaseDirectory"/>.
        /// </param>
        internal static string ResolveAcmeStateDir(string acmeStateDir, string? applicationBaseDirectory) =>
            ApplicationLocalPath.ResolveApplicationLocalPath(acmeStateDir, applicationBaseDirectory);

        /// <summary>
        /// Requires <see cref="AcmeCloudflareOptions.CloudFlareOperationTimeout"/> to be from 1 second through 1 hour, inclusive.
        /// </summary>
        /// <param name="options">Options instance to validate.</param>
        /// <param name="failures">Receives a message when the timeout is outside that range.</param>
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

        /// <summary>
        /// Validates bind-address tokens: wildcards, IPv4/IPv6 literals, and NIC assignment.
        /// </summary>
        /// <param name="options">Options whose bind list is checked. An empty list is a failure.</param>
        /// <param name="localIpAddressAssignee">Assignment check for non-wildcard entries.</param>
        /// <param name="failures">Receives one message per invalid entry. Wildcard entries are accepted without a NIC check.</param>
        internal static void CollectBindAddressFailures(
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

        /// <summary>
        /// Requires <see cref="AcmeCloudflareOptions.BindPortTls"/> to be 0 (TLS disabled) or an integer from 1 through 65535.
        /// </summary>
        /// <param name="options">Options instance to validate.</param>
        /// <param name="failures">Receives a message when the port is outside that set.</param>
        private static void ValidatePorts(AcmeCloudflareOptions options, List<string> failures)
        {
            if (options.BindPortTls is < 0 or > 65535)
            {
                failures.Add(
                    $"{nameof(AcmeCloudflareOptions.BindPortTls)} must be 0 (TLS disabled) or an integer in the range 1–65535.");
            }
        }

        /// <summary>
        /// Requires an absolute HTTPS ACME directory URL, a non-empty state directory, and a renewal threshold from 1 through 90 days.
        /// When TLS is enabled, also requires a plausible account email and a certificate password.
        /// </summary>
        /// <param name="options">Options instance to validate.</param>
        /// <param name="failures">Receives one message per failed ACME rule.</param>
        /// <param name="validateCertificateZoneCoverage">
        /// When <see langword="true"/>, TLS is enabled, and both FQDN and DNS suffix are present, certificate identities must lie in the DNS zone.
        /// </param>
        /// <remarks>
        /// Email and certificate password are not required when <see cref="AcmeCloudflareOptions.BindPortTls"/> is 0.
        /// Zone-coverage failures are copied from the thrown configuration or argument message.
        /// </remarks>
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
                    $"and must be a valid contact email address (use environment variable {AcmeCloudflareOptions.AcmeAccountEnvironmentVariable}).");
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

        /// <summary>
        /// Requires a non-whitespace Cloudflare API key and zone id. Failure text names the configuration keys and environment variables, not the secret.
        /// </summary>
        /// <param name="options">Options instance to validate.</param>
        /// <param name="failures">Receives one message per missing Cloudflare setting.</param>
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

        /// <summary>
        /// Requires <see cref="AcmeCloudflareOptions.DnsSuffix"/> to be non-whitespace and a syntactically valid DNS name after trimming a trailing dot.
        /// </summary>
        /// <param name="options">Options instance to validate.</param>
        /// <param name="failures">Receives a message when the suffix is missing or invalid. A missing suffix skips the syntax check.</param>
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

        /// <summary>
        /// Returns whether <paramref name="email"/> has one <c>@</c>, a non-empty local part, length 1–254, and a dotted domain that passes <see cref="NntpdDnsName.IsValidSuffix"/>.
        /// </summary>
        /// <param name="email">Contact address. Leading and trailing whitespace is ignored.</param>
        /// <returns><see langword="false"/> for blank, too long, or structurally invalid addresses. Delivery is not checked.</returns>
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
    internal static class NntpdDnsName
    {
        /// <summary>Validates DNS suffix / name syntax (labels, length, allowed characters).</summary>
        /// <param name="suffix">Candidate name. A trailing dot is removed before the checks. Whitespace is invalid.</param>
        /// <returns>
        /// <see langword="true"/> when the name is 1–253 characters, has no empty labels, and each label is 1–63 ASCII letters, digits, or hyphens not starting or ending with a hyphen.
        /// </returns>
        internal static bool IsValidSuffix(string suffix)
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
}
