using System.Globalization;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Builds a canonical application FQDN from a fixed prefix, validated server id, and DNS suffix.
/// </summary>
/// <remarks>
/// Format is <c>{prefix}{serverId:00}.{dnsSuffix}</c> in invariant lowercase.
/// The prefix is application identity supplied by the caller; this type does not
/// know about configuration sections or operator-configurable names.
/// </remarks>
public static class ApplicationFqdn
{
    /// <summary>Maximum DNS FQDN length.</summary>
    public const int MaximumLength = 253;

    /// <summary>
    /// Formats the host label <c>{prefix}{serverId:00}</c>.
    /// </summary>
    /// <param name="prefix">Fixed application prefix (for example <c>nntpd</c>).</param>
    /// <param name="serverId">Validated server id in the shared ServerId range.</param>
    /// <returns>Lowercase host label with a two-digit id.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="prefix"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="serverId"/> is outside 1–255.</exception>
    public static string FormatHostLabel(string prefix, int serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!ServerIdRules.IsInRange(serverId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(serverId),
                serverId,
                $"ServerId must be between {ServerIdRules.MinimumInclusive} and {ServerIdRules.MaximumInclusive}.");
        }

        return string.Concat(
            prefix.Trim().ToLowerInvariant(),
            serverId.ToString("D2", CultureInfo.InvariantCulture));
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
    /// Builds <c>{prefix}{serverId:00}.{dnsSuffix}</c>.
    /// </summary>
    /// <param name="prefix">Fixed application prefix.</param>
    /// <param name="serverId">Validated server id in the shared ServerId range.</param>
    /// <param name="dnsSuffix">DNS suffix.</param>
    /// <returns>Canonical lowercase FQDN.</returns>
    public static string Build(string prefix, int serverId, string dnsSuffix)
    {
        return $"{FormatHostLabel(prefix, serverId)}.{CanonicalizeDnsSuffix(dnsSuffix)}";
    }
}
