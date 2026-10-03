using VectorNNTP.Common.Core;

namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>
    /// Shared <see cref="IApplicationService"/> adapter around
    /// <see cref="CloudflareDnsReconciliationService"/>.
    /// </summary>
    /// <remarks>
    /// Applications register this type with their <c>ApplicationServiceManager</c>.
    /// It does not construct hostnames, resolve bind addresses, or call Cloudflare.
    /// Those remain on <see cref="CloudflareDnsReconciliationService"/>.
    /// </remarks>
    internal sealed class CloudflareDnsReconciliationApplicationService : IApplicationService
    {
        /// <summary>Reconciliation service that performs hostname, bind-address, and Cloudflare work.</summary>
        private readonly CloudflareDnsReconciliationService _inner;

        /// <summary>Initializes a new wrapper.</summary>
        /// <param name="inner">Reconciliation service registered beside this adapter.</param>
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
}
