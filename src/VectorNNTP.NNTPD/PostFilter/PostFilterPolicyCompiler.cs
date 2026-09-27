using System.Net;
using VectorNNTP.Common.Articles;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.PostFilter.Quota;

namespace VectorNNTP.NNTPD.PostFilter;

/// <summary>Compiles <see cref="PostFilterOptions"/> into an immutable snapshot.</summary>
internal static class PostFilterPolicyCompiler
{
    /// <summary>Builds a snapshot or throws when the options are not usable.</summary>
    public static PostFilterPolicySnapshot Compile(PostFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Quota);
        ArgumentNullException.ThrowIfNull(options.SpamAssassin);

        if (options.Quota.LongWindow <= TimeSpan.Zero || options.Quota.ShortWindow <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("PostFilter quota windows must be positive.");
        }

        var windows = new PostFilterQuotaWindows(
            (long)options.Quota.LongWindow.TotalMilliseconds,
            (long)options.Quota.ShortWindow.TotalMilliseconds);
        windows.Validate();

        var sa = options.SpamAssassin;
        if (sa.Enabled)
        {
            if (sa.OnFailure is null)
            {
                throw new InvalidOperationException(
                    "Nntpd:PostFilter:SpamAssassin:OnFailure is required when SpamAssassin is enabled.");
            }

            if (sa.Hosts.Length == 0 || sa.Hosts.Any(static host => string.IsNullOrWhiteSpace(host)))
            {
                throw new InvalidOperationException(
                    "Nntpd:PostFilter:SpamAssassin:Hosts is required when SpamAssassin is enabled.");
            }

            if (sa.Port is < 1 or > 65535)
            {
                throw new InvalidOperationException("Nntpd:PostFilter:SpamAssassin:Port must be 1–65535.");
            }

            if (sa.OperationTimeout < PostFilterSpamAssassinOptions.MinOperationTimeout
                || sa.OperationTimeout > PostFilterSpamAssassinOptions.MaxOperationTimeout)
            {
                throw new InvalidOperationException(
                    "Nntpd:PostFilter:SpamAssassin:OperationTimeout must be between 1 second and 2 minutes when SpamAssassin is enabled.");
            }

            if (sa.ConnectTimeout <= TimeSpan.Zero || sa.ConnectTimeout > sa.OperationTimeout)
            {
                throw new InvalidOperationException(
                    "Nntpd:PostFilter:SpamAssassin:ConnectTimeout must be positive and must not exceed OperationTimeout.");
            }

            if (string.IsNullOrWhiteSpace(sa.ProtocolVersion))
            {
                throw new InvalidOperationException(
                    "Nntpd:PostFilter:SpamAssassin:ProtocolVersion is required when SpamAssassin is enabled.");
            }

            if (sa.MaxConnections < PostFilterSpamAssassinOptions.MinMaxConnections
                || sa.MaxConnections > PostFilterSpamAssassinOptions.MaxMaxConnections)
            {
                throw new InvalidOperationException(
                    "Nntpd:PostFilter:SpamAssassin:MaxConnections must be between 1 and 32 when SpamAssassin is enabled.");
            }

            if (!Enum.IsDefined(sa.HostSelection))
            {
                throw new InvalidOperationException(
                    "Nntpd:PostFilter:SpamAssassin:HostSelection is not a defined strategy.");
            }
        }

        if (sa.MaxArticleSize < 0)
        {
            throw new InvalidOperationException(
                "Nntpd:PostFilter:SpamAssassin:MaxArticleSize must be zero or positive.");
        }

        var ceilings = new PostFilterQuotaCeilings(
            options.Quota.MaxMessagesLong,
            options.Quota.MaxBytesLong,
            options.Quota.MaxIdenticalLong,
            options.Quota.MaxMessagesShort,
            options.Quota.MaxBytesShort,
            options.Quota.MaxIdenticalShort);
        ceilings.Validate();

        return new PostFilterPolicySnapshot(
            options.Gate,
            ToSet(options.DeniedAccounts),
            ToNetworks(options.DeniedCidrs, "DeniedCidrs"),
            ToSet(options.AllowlistedAccounts),
            ToNetworks(options.AllowlistedCidrs, "AllowlistedCidrs"),
            ParseArtTypes(options.RejectArtTypes, "RejectArtTypes"),
            windows,
            ceilings,
            sa.Enabled,
            sa.OnFailure,
            sa.MaxArticleSize,
            ParseArtTypes(sa.ExcludeArtTypes, "SpamAssassin:ExcludeArtTypes"),
            sa.Enabled ? sa.Hosts.Select(static host => host.Trim()).ToArray() : [],
            sa.Port,
            string.IsNullOrWhiteSpace(sa.ProtocolVersion)
                ? PostFilterSpamAssassinOptions.DefaultProtocolVersion
                : sa.ProtocolVersion.Trim(),
            sa.MaxConnections,
            sa.HostSelection,
            sa.ConnectTimeout,
            sa.OperationTimeout,
            PostFilterQuotaDefaults.HoldMilliseconds(sa.Enabled, sa.OperationTimeout));
    }

    private static HashSet<string> ToSet(string[] values)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value.Trim());
            }
        }

        return set;
    }

    private static List<IPNetwork> ToNetworks(string[] values, string name)
    {
        var networks = new List<IPNetwork>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!IPNetwork.TryParse(value.Trim(), out var network))
            {
                throw new InvalidOperationException($"PostFilter {name} contains an invalid CIDR: '{value}'.");
            }

            networks.Add(network);
        }

        return networks;
    }

    private static ArticleType ParseArtTypes(string[] values, string name)
    {
        var mask = ArticleType.None;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!Enum.TryParse<ArticleType>(value.Trim(), ignoreCase: true, out var flag)
                || !Enum.IsDefined(flag)
                || flag == ArticleType.None)
            {
                throw new InvalidOperationException($"PostFilter {name} contains an unknown ArtType: '{value}'.");
            }

            mask |= flag;
        }

        return mask;
    }
}
