using System.Net;
using DnsClient;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Resolves challenge TXT visibility by querying authoritative nameservers for the DNS apex
/// (recursion disabled). Injectable for offline tests via <see cref="IAuthoritativeTxtResolver"/>.
/// </summary>
/// <remarks>
/// Production discovers NS for the configured DNS apex, resolves NS A/AAAA, then requires the
/// expected token on every reachable authoritative address (intersection semantics).
/// </remarks>
public sealed class AuthoritativeTxtResolver : IAuthoritativeTxtResolver
{
    private readonly string _zoneApex;
    private readonly ILogger<AuthoritativeTxtResolver> _logger;
    private readonly Func<CancellationToken, Task<IReadOnlyList<IPEndPoint>>>? _nameserverProvider;

    /// <summary>Initializes a new instance of the <see cref="AuthoritativeTxtResolver"/> class.</summary>
    public AuthoritativeTxtResolver(string zoneApex, ILogger<AuthoritativeTxtResolver> logger)
        : this(zoneApex, logger, nameserverProvider: null)
    {
    }

    /// <summary>Test constructor with injectable authoritative endpoints.</summary>
    internal AuthoritativeTxtResolver(
        string zoneApex,
        ILogger<AuthoritativeTxtResolver> logger,
        Func<CancellationToken, Task<IReadOnlyList<IPEndPoint>>>? nameserverProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneApex);
        ArgumentNullException.ThrowIfNull(logger);
        _zoneApex = DnsZoneCoverage.NormalizeDnsHostname(zoneApex, "DNSSuffix");
        _logger = logger;
        _nameserverProvider = nameserverProvider;
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
                var options = new LookupClientOptions(server)
                {
                    Recursion = false,
                    UseCache = false,
                    Retries = 1,
                    Timeout = TimeSpan.FromSeconds(5),
                };
                var client = new LookupClient(options);
                var response = await client
                    .QueryAsync(name.TrimEnd('.'), QueryType.TXT, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (response.HasError)
                {
                    continue;
                }

                var values = response.Answers.TxtRecords()
                    .SelectMany(static r => r.Text)
                    .Select(static t => t.Trim())
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
        var discovery = new LookupClient();
        var nsResponse = await discovery
            .QueryAsync(_zoneApex, QueryType.NS, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (nsResponse.HasError)
        {
            throw new AcmeChallengeException("ns_discovery_failed", nsResponse.ErrorMessage ?? "NS query failed");
        }

        var nsNames = nsResponse.Answers.NsRecords()
            .Select(static r => r.NSDName.Value.TrimEnd('.'))
            .ToArray();
        if (nsNames.Length == 0)
        {
            throw new AcmeChallengeException("ns_discovery_failed", "empty NS set");
        }

        var endpoints = new List<IPEndPoint>();
        foreach (var nsName in nsNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var a = await discovery.QueryAsync(nsName, QueryType.A, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                foreach (var record in a.Answers.ARecords())
                {
                    endpoints.Add(new IPEndPoint(record.Address, 53));
                }

                var aaaa = await discovery.QueryAsync(nsName, QueryType.AAAA, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                foreach (var record in aaaa.Answers.AaaaRecords())
                {
                    endpoints.Add(new IPEndPoint(record.Address, 53));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AcmeLogMessages.AuthoritativeNsResolveFailed(_logger, ex, nsName);
            }
        }

        return endpoints;
    }
}
