using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>
/// NNTPD lifecycle wrapper around the shared Cloudflare DNS reconciliation implementation.
/// </summary>
public sealed class CloudflareDnsReconciliationApplicationService : IApplicationService
{
    private readonly CloudflareDnsReconciliationService _inner;

    /// <summary>Initializes a new wrapper.</summary>
    public CloudflareDnsReconciliationApplicationService(CloudflareDnsReconciliationService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public Task? Execution => _inner.Execution;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);
}
