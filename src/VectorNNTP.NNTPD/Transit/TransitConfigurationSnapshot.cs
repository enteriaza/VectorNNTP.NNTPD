using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Transit;

/// <summary>
/// Immutable, fully validated Transit peer configuration snapshot.
/// </summary>
/// <remarks>
/// Readers observe either the previous complete snapshot or this complete snapshot.
/// DNS-resolved addresses live in <see cref="ITransitDnsAddressCache"/> so hostname
/// refreshes do not rebuild unrelated peer policy.
/// </remarks>
public sealed class TransitConfigurationSnapshot
{
    /// <summary>Empty snapshot (no peers).</summary>
    public static TransitConfigurationSnapshot Empty { get; } = new(new Dictionary<string, TransitPeerPolicy>(StringComparer.Ordinal));

    internal TransitConfigurationSnapshot(IReadOnlyDictionary<string, TransitPeerPolicy> peers)
    {
        Peers = peers;
    }

    /// <summary>Gets peers keyed by configured identifier (ordinal, case-sensitive).</summary>
    public IReadOnlyDictionary<string, TransitPeerPolicy> Peers { get; }

    /// <summary>Gets whether any peers are configured.</summary>
    public bool IsEmpty => Peers.Count == 0;

    /// <summary>Builds a snapshot from already-validated options.</summary>
    public static TransitConfigurationSnapshot Create(TransitPeersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var peers = new Dictionary<string, TransitPeerPolicy>(options.Count, StringComparer.Ordinal);
        foreach (var (identifier, peer) in options)
        {
            peers[identifier] = CreatePeer(identifier, peer);
        }

        return new TransitConfigurationSnapshot(peers);
    }

    internal static TransitPeerPolicy CreatePeer(string identifier, TransitPeerOptions options)
    {
        var literals = new List<IpPrefix>();
        var hostnames = new List<string>();
        foreach (var raw in options.AllowFrom ?? [])
        {
            if (!TransitAllowFromEntry.TryParse(raw, out var entry, out _))
            {
                continue;
            }

            switch (entry)
            {
                case TransitAllowFromEntry.Literal literal:
                    literals.Add(literal.Prefix);
                    break;
                case TransitAllowFromEntry.Hostname hostname:
                    if (!hostnames.Contains(hostname.DnsName, StringComparer.OrdinalIgnoreCase))
                    {
                        hostnames.Add(hostname.DnsName);
                    }

                    break;
            }
        }

        var connectTo = new List<TransitConnectEndpoint>();
        foreach (var raw in options.ConnectTo ?? [])
        {
            if (TransitConnectEndpoint.TryParse(raw, out var endpoint, out _) && endpoint is not null)
            {
                connectTo.Add(endpoint);
            }
        }

        if (!NewsfeedsPattern.TryParse(options.Patterns, out var patterns, out _) || patterns is null)
        {
            throw new InvalidOperationException("Patterns must be validated before snapshot creation.");
        }

        if (!TransitSslParser.TryParse(options.Ssl, out var ssl, out _))
        {
            throw new InvalidOperationException("Ssl must be validated before snapshot creation.");
        }

        var username = (options.Username ?? string.Empty).Trim();
        var password = options.Password ?? string.Empty;
        if (!TransitMessageTypesParser.TryParse(options.MessageTypes, out var messageTypes, out _))
        {
            throw new InvalidOperationException("MessageTypes must be validated before snapshot creation.");
        }

        var peerName = options.PeerName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(peerName))
        {
            throw new InvalidOperationException("PeerName must be validated before snapshot creation.");
        }

        return new TransitPeerPolicy(
            identifier,
            peerName,
            options.MaxIncomingConnections!.Value,
            options.MaxOutgoingConnections!.Value,
            literals,
            hostnames,
            connectTo,
            username,
            password,
            ssl,
            patterns,
            options.DeferOnDuplicate,
            options.PathToken ?? string.Empty,
            options.MaxSize,
            messageTypes);
    }
}
