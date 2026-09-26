using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking;

namespace VectorNNTP.BackFiller.Hosting;

/// <summary>
/// Generic Host wrapper around the shared Cloudflare DNS reconciliation implementation.
/// </summary>
public sealed class CloudflareDnsReconciliationHostedService : IHostedService
{
    private readonly CloudflareDnsReconciliationService _inner;
    private readonly IBindAddressResolver _bindAddressResolver;
    private readonly IOptions<AcmeCloudflareOptions> _options;
    private readonly IBackFillerStartupJournal _journal;

    /// <summary>Initializes a new wrapper.</summary>
    public CloudflareDnsReconciliationHostedService(
        CloudflareDnsReconciliationService inner,
        IBindAddressResolver bindAddressResolver,
        IOptions<AcmeCloudflareOptions> options,
        IBackFillerStartupJournal journal)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(bindAddressResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(journal);
        _inner = inner;
        _bindAddressResolver = bindAddressResolver;
        _options = options;
        _journal = journal;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _bindAddressResolver.Resolve(_options.Value);
        _journal.Record(BackFillerStartupStages.BindResolution);
        await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
        _journal.Record(BackFillerStartupStages.CloudflareReconciled);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);
}
