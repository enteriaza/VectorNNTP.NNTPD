using System.Net;
using VectorNNTP.NNTPD.Dns.Wire;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Resolves challenge TXT visibility by querying authoritative nameservers for the configured DNS apex
/// (recursion disabled on TXT queries). Injectable for offline tests via <see cref="IAuthoritativeTxtResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>NS discovery (product contract):</b> discovers NS for the configured DNS apex (<c>DnsSuffix</c>) only.
/// Historical Vector.NNTP label-walk from the challenge name is intentionally not used so zone-apex
/// configuration remains authoritative.
/// </para>
/// <para>
/// <b>TXT visibility (product contract):</b> requires the expected token on every reachable authoritative
/// address that answers successfully (intersection semantics). Historical configurable quorum is not used.
/// </para>
/// <para>
/// Bootstrap/recursive DNS is used only to discover apex NS names and resolve NS A/AAAA. TXT queries
/// are sent directly to those addresses via the owned DNS wire client (UDP then TCP).
/// </para>
/// </remarks>
public sealed class AuthoritativeTxtResolver : IAuthoritativeTxtResolver
{
    private readonly string _zoneApex;
    private readonly ILogger<AuthoritativeTxtResolver> _logger;
    private readonly Func<CancellationToken, Task<IReadOnlyList<IPEndPoint>>>? _nameserverProvider;
    private readonly Func<IPAddress, string, CancellationToken, Task<IReadOnlyList<string>>>? _txtQuery;

    /// <summary>Initializes a new instance of the <see cref="AuthoritativeTxtResolver"/> class.</summary>
    public AuthoritativeTxtResolver(string zoneApex, ILogger<AuthoritativeTxtResolver> logger)
        : this(zoneApex, logger, nameserverProvider: null, txtQuery: null)
    {
    }

    /// <summary>Test constructor with injectable authoritative endpoints.</summary>
    internal AuthoritativeTxtResolver(
        string zoneApex,
        ILogger<AuthoritativeTxtResolver> logger,
        Func<CancellationToken, Task<IReadOnlyList<IPEndPoint>>>? nameserverProvider)
        : this(zoneApex, logger, nameserverProvider, txtQuery: null)
    {
    }

    /// <summary>Test constructor with injectable endpoints and TXT answers (no live DNS).</summary>
    internal AuthoritativeTxtResolver(
        string zoneApex,
        ILogger<AuthoritativeTxtResolver> logger,
        Func<CancellationToken, Task<IReadOnlyList<IPEndPoint>>>? nameserverProvider,
        Func<IPAddress, string, CancellationToken, Task<IReadOnlyList<string>>>? txtQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneApex);
        ArgumentNullException.ThrowIfNull(logger);
        _zoneApex = DnsZoneCoverage.NormalizeDnsHostname(zoneApex, "DNSSuffix");
        _logger = logger;
        _nameserverProvider = nameserverProvider;
        _txtQuery = txtQuery;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        var servers = _nameserverProvider is not null
            ? await _nameserverProvider(cancellationToken).ConfigureAwait(false)
            : await DiscoverAuthoritativeEndpointsAsync(cancellationToken).ConfigureAwait(false);

        if (servers.Count == 0)
        {
            throw new AcmeChallengeException("ns_discovery_failed", "no authoritative nameservers");
        }

        HashSet<string>? intersection = null;
        var successCount = 0;
        foreach (var server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyList<string> raw = _txtQuery is not null
                    ? await _txtQuery(server.Address, name.TrimEnd('.'), cancellationToken).ConfigureAwait(false)
                    : await AuthoritativeDnsWireClient
                        .QueryTxtAsync(server.Address, name.TrimEnd('.'), cancellationToken)
                        .ConfigureAwait(false);
                var values = raw
                    .Select(static t => t.Trim())
                    .Where(static t => t.Length > 0)
                    .ToHashSet(StringComparer.Ordinal);
                intersection = intersection is null
                    ? values
                    : intersection.Intersect(values, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                successCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AcmeLogMessages.AuthoritativeTxtLookupFailed(_logger, ex, name, server);
            }
        }

        if (successCount == 0 || intersection is null)
        {
            return [];
        }

        return intersection.ToArray();
    }

    private async Task<IReadOnlyList<IPEndPoint>> DiscoverAuthoritativeEndpointsAsync(
        CancellationToken cancellationToken)
    {
        // Zone-apex NS discovery (not label-walk): see type remarks.
        IReadOnlyList<IPAddress> addresses = await DnsWireRecursiveResolver
            .ResolveConfiguredZoneApexNameServerAddressesAsync(_zoneApex, _logger, cancellationToken)
            .ConfigureAwait(false);

        if (addresses.Count == 0)
        {
            throw new AcmeChallengeException("ns_discovery_failed", "empty NS set");
        }

        var endpoints = new List<IPEndPoint>(addresses.Count);
        foreach (IPAddress address in addresses)
        {
            endpoints.Add(new IPEndPoint(address, 53));
        }

        return endpoints;
    }
}
