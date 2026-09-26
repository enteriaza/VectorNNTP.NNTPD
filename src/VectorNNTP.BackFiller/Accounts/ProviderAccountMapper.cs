using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.Accounts;

/// <summary>
/// Maps <c>nntpbackfilleraccounts</c> rows onto Phase 4 <see cref="BackFillerProviderDefinition"/> values.
/// Unknown, duplicate, and invalid rows are rejected instead of published.
/// </summary>
public static class ProviderAccountMapper
{
    /// <summary>
    /// Maps a query result. First valid row for a canonical backbone wins.
    /// <c>maxconnections</c> becomes <see cref="BackFillerProviderDefinition.MaxSessions"/>.
    /// <c>MinSessions</c> is 0 (lazy) because the table has no min-session column.
    /// </summary>
    public static ProviderAccountMapResult Map(IReadOnlyList<ProviderAccountRow> rows)
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
    public static bool TryCanonicalizeBackbone(string? backbone, out string canonical)
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
    public static bool TryParseUseSsl(string? raw, out bool useSsl)
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

        if (row.Password is null)
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
            MaxSessions: row.MaxConnections);
        reason = string.Empty;
        return true;
    }
}
