using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking.Proxy;

namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// Resolves connection-time transit/streaming peer privileges from effective client identity.
/// </summary>
public interface ITransitPeerAuthorization
{
    /// <summary>Gets whether any transit peers are configured.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Returns the initial <see cref="NntpAuthorization"/> for <paramref name="effectiveClientAddress"/>.
    /// </summary>
    /// <remarks>
    /// Allowed peers receive transit + streaming without authentication, reader, or posting.
    /// Other addresses receive <see cref="NntpAuthorization.Unauthenticated"/>.
    /// </remarks>
    NntpAuthorization Resolve(IPAddress effectiveClientAddress);
}

/// <summary>
/// Options-backed implementation of <see cref="ITransitPeerAuthorization"/>.
/// </summary>
public sealed class TransitPeerAuthorization : ITransitPeerAuthorization
{
    private readonly HashSet<IPAddress> _peers;

    /// <summary>Disabled shared instance (empty allow-list).</summary>
    public static TransitPeerAuthorization Disabled { get; } = FromAddresses(Array.Empty<IPAddress>());

    /// <summary>Creates an instance from <see cref="NntpdOptions.Transit"/> (DI).</summary>
    [ActivatorUtilitiesConstructor]
    public TransitPeerAuthorization(
        IOptions<NntpdOptions> options,
        ILogger<TransitPeerAuthorization> logger)
        : this(ParseAddresses(options.Value.Transit?.AllowedPeers))
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (_peers.Count > 0)
        {
            logger.LogWarning(
                "Trusted transit/streaming peers configured ({Count}): {Peers}. These addresses receive transit privileges without authentication.",
                _peers.Count,
                string.Join(", ", _peers));
        }
    }

    /// <summary>Creates an instance from already-parsed addresses (tests).</summary>
    public static TransitPeerAuthorization FromAddresses(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        return new TransitPeerAuthorization(addresses);
    }

    private TransitPeerAuthorization(IEnumerable<IPAddress> addresses)
    {
        _peers = new HashSet<IPAddress>(addresses.Select(TrustedProxyHosts.Canonicalize));
    }

    /// <inheritdoc />
    public bool IsEnabled => _peers.Count > 0;

    /// <inheritdoc />
    public NntpAuthorization Resolve(IPAddress effectiveClientAddress)
    {
        ArgumentNullException.ThrowIfNull(effectiveClientAddress);
        if (_peers.Count == 0)
        {
            return NntpAuthorization.Unauthenticated;
        }

        if (_peers.Contains(TrustedProxyHosts.Canonicalize(effectiveClientAddress)))
        {
            return NntpAuthorization.TrustedTransitPeer;
        }

        return NntpAuthorization.Unauthenticated;
    }

    private static IEnumerable<IPAddress> ParseAddresses(string[]? entries)
    {
        if (entries is null || entries.Length == 0)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (IPAddress.TryParse(entry.Trim(), out var address))
            {
                yield return address;
            }
        }
    }
}
