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
        ValidatePorts(options, failures);
        ValidateCloudFlare(options, failures);
        ValidateDnsSuffixAndServerId(options, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
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
    /// Returns whether <paramref name="text"/> contains the API key (for tests / diagnostics hygiene).
    /// </summary>
    public static bool ContainsSecret(string text, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains(apiKey, StringComparison.Ordinal);
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
