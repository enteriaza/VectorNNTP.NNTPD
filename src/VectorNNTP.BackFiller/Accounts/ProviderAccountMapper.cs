using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.Accounts;

/// <summary>
/// Maps <c>nntpbackfilleraccounts</c> rows onto Phase 4 <see cref="BackFillerProviderDefinition"/> values.
/// Unknown, duplicate, and invalid rows are rejected instead of published.
/// </summary>
internal static class ProviderAccountMapper
{
    /// <summary>
    /// Maps a query result. The first valid row for a canonical backbone wins.
    /// <c>maxconnections</c> becomes <see cref="BackFillerProviderDefinition.MaxSessions"/>.
    /// <c>keepalive</c> becomes <see cref="BackFillerProviderDefinition.KeepAliveSeconds"/>.
    /// <c>MinSessions</c> is unused leftover (table has no min column). <c>MaxSessions</c> is the eager desired count.
    /// </summary>
    /// <param name="rows">Parsed rows. Not mutated. Invalid rows are reported in the result rather than thrown.</param>
    /// <returns>Accepted definitions and one rejection record per skipped row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    internal static ProviderAccountMapResult Map(IReadOnlyList<ProviderAccountRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var providers = new List<BackFillerProviderDefinition>();
        var rejected = new List<RejectedProviderAccountRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (!TryMap(row, out var provider, out var reason))
            {
                rejected.Add(new RejectedProviderAccountRow(row.Backbone, reason));
                continue;
            }

            if (!seen.Add(provider.Backbone))
            {
                rejected.Add(new RejectedProviderAccountRow(provider.Backbone, "Duplicate backbone for this server id."));
                continue;
            }

            providers.Add(provider);
        }

        return new ProviderAccountMapResult(providers, rejected);
    }

    /// <summary>Resolves a backbone token to the canonical Phase 2/3 label.</summary>
    /// <param name="backbone">Raw backbone text. Leading and trailing white space is ignored.</param>
    /// <param name="canonical">
    /// Matching <see cref="BackFillerRabbitMqTopology.ProviderBackbones"/> label when this method returns
    /// <see langword="true"/>. Empty when <paramref name="backbone"/> is null or white space.
    /// The trimmed input when it matches no known backbone.
    /// </param>
    /// <returns><see langword="true"/> when the trimmed text matches a known backbone, ordinal-ignore-case.</returns>
    private static bool TryCanonicalizeBackbone(string? backbone, out string canonical)
    {
        if (string.IsNullOrWhiteSpace(backbone))
        {
            canonical = string.Empty;
            return false;
        }

        var trimmed = backbone.Trim();
        foreach (var candidate in BackFillerRabbitMqTopology.ProviderBackbones)
        {
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                canonical = candidate;
                return true;
            }
        }

        canonical = trimmed;
        return false;
    }

    /// <summary>Parses the persisted <c>usessl</c> enum.</summary>
    /// <param name="raw">Stored <c>usessl</c> text. Leading and trailing white space is ignored.</param>
    /// <param name="useSsl"><see langword="true"/> for <c>y</c> and <see langword="false"/> for <c>n</c> or any rejected value.</param>
    /// <returns><see langword="true"/> only for <c>y</c> or <c>n</c> after trimming. The comparison is case-sensitive.</returns>
    private static bool TryParseUseSsl(string? raw, out bool useSsl)
    {
        switch (raw?.Trim())
        {
            case "y":
                useSsl = true;
                return true;
            case "n":
                useSsl = false;
                return true;
            default:
                useSsl = false;
                return false;
        }
    }

    /// <summary>
    /// Validates one row and builds a definition with <see cref="BackFillerProviderDefinition.MinSessions"/> set to zero.
    /// </summary>
    /// <param name="row">Parsed row. Hostname and username are trimmed on success. Password is copied unchanged.</param>
    /// <param name="provider">The definition when this method returns <see langword="true"/>; otherwise the default null assignment.</param>
    /// <param name="reason">Empty on success. On failure, a stable rejection sentence and <paramref name="provider"/> is not published.</param>
    /// <returns><see langword="true"/> when every field below is accepted.</returns>
    /// <remarks>
    /// Rejects an unknown backbone, a missing hostname, username, or password, a port outside 1–65535,
    /// <c>maxconnections</c> below 1, and a <c>usessl</c> value other than <c>y</c> or <c>n</c>.
    /// <see cref="ProviderAccountRow.KeepAliveSeconds"/> is copied and is not range-checked again.
    /// </remarks>
    private static bool TryMap(ProviderAccountRow row, out BackFillerProviderDefinition provider, out string reason)
    {
        provider = null!;
        if (!TryCanonicalizeBackbone(row.Backbone, out var backbone))
        {
            reason = "Unknown or unsupported backbone.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(row.Hostname))
        {
            reason = "Hostname is required.";
            return false;
        }

        if (row.Port is < 1 or > 65535)
        {
            reason = "Port must be between 1 and 65535.";
            return false;
        }

        if (row.MaxConnections < 1)
        {
            reason = "MaxConnections must be at least 1.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(row.Username))
        {
            reason = "Username is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(row.Password))
        {
            reason = "Password is required.";
            return false;
        }

        if (!TryParseUseSsl(row.UseSslRaw, out var useTls))
        {
            reason = "UseSsl must be 'y' or 'n'.";
            return false;
        }

        provider = new BackFillerProviderDefinition(
            backbone,
            row.Hostname.Trim(),
            row.Port,
            useTls,
            row.Username.Trim(),
            row.Password,
            MinSessions: 0,
            MaxSessions: row.MaxConnections,
            KeepAliveSeconds: row.KeepAliveSeconds);
        reason = string.Empty;
        return true;
    }
}
