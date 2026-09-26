using System.Globalization;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Canonical BackFiller identity and FQDN construction.
/// </summary>
/// <remarks>
/// FQDN is generated as <c>{name}{serverId:00}.{dnsSuffix}</c> after name and suffix
/// canonicalization. It is not independently configurable.
/// Example: Name <c>backfiller</c>, ServerId <c>1</c> → <c>backfiller01.usenet.ninja</c>.
/// </remarks>
public static class BackFillerIdentity
{
    /// <summary>Minimum accepted <see cref="BackFillerOptions.ServerId"/>.</summary>
    public const int MinimumServerId = 0;

    /// <summary>Maximum accepted <see cref="BackFillerOptions.ServerId"/>.</summary>
    public const int MaximumServerId = 99;

    /// <summary>Maximum DNS FQDN length.</summary>
    public const int MaximumFqdnLength = 253;

    /// <summary>
    /// Formats <paramref name="serverId"/> as a two-digit zero-padded value.
    /// </summary>
    /// <param name="serverId">Server identifier in 0–99.</param>
    /// <returns>Two-digit identifier text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="serverId"/> is outside 0–99.</exception>
    public static string FormatServerId(int serverId)
    {
        return serverId is < MinimumServerId or > MaximumServerId
            ? throw new ArgumentOutOfRangeException(nameof(serverId), serverId, "ServerId must be between 0 and 99.")
            : serverId.ToString("D2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Canonicalizes a DNS suffix (trim, strip trailing dots, lowercase).
    /// </summary>
    /// <param name="dnsSuffix">Configured DNS suffix.</param>
    /// <returns>Canonical suffix.</returns>
    public static string CanonicalizeDnsSuffix(string dnsSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dnsSuffix);
        return dnsSuffix.Trim().TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>
    /// Canonicalizes the instance name used as the FQDN host-label prefix.
    /// </summary>
    /// <param name="name">Configured instance name.</param>
    /// <returns>Trimmed lowercase name.</returns>
    public static string CanonicalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Builds the canonical BackFiller FQDN from validated identity parts.
    /// </summary>
    /// <param name="name">Instance name.</param>
    /// <param name="serverId">Server identifier in 0–99.</param>
    /// <param name="dnsSuffix">DNS suffix.</param>
    /// <returns>Generated FQDN.</returns>
    public static string BuildFqdn(string name, int serverId, string dnsSuffix)
    {
        var canonicalName = CanonicalizeName(name);
        var canonicalSuffix = CanonicalizeDnsSuffix(dnsSuffix);
        return $"{canonicalName}{FormatServerId(serverId)}.{canonicalSuffix}";
    }

    /// <summary>
    /// Returns whether <paramref name="label"/> is a valid DNS label.
    /// </summary>
    /// <param name="label">Candidate label.</param>
    /// <returns><see langword="true"/> when the label is valid.</returns>
    public static bool IsValidDnsLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 63)
        {
            return false;
        }

        if (label[0] is '-' || label[^1] is '-')
        {
            return false;
        }

        foreach (var ch in label)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns whether <paramref name="suffix"/> is a syntactically valid DNS suffix.
    /// </summary>
    /// <param name="suffix">Canonical (already trimmed/lowercased) suffix.</param>
    /// <returns><see langword="true"/> when the suffix is valid.</returns>
    public static bool IsValidDnsSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix)
            || suffix.Contains(' ', StringComparison.Ordinal)
            || suffix.Contains("://", StringComparison.Ordinal)
            || suffix.Length > MaximumFqdnLength)
        {
            return false;
        }

        var labels = suffix.Split('.', StringSplitOptions.None);
        if (labels.Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (!IsValidDnsLabel(label))
            {
                return false;
            }
        }

        return true;
    }
}
