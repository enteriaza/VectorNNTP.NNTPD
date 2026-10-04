using VectorNNTP.Common.NntpDb;

namespace VectorNNTP.BackFiller.NntpDb
{
    /// <summary>
    /// Starts <see cref="NntpDbService"/> before later hosted services, including provider-account refresh.
    /// </summary>
    internal sealed class NntpDbServiceHostedAdapter : IHostedService
    {
        private readonly NntpDbService _service;

        /// <summary>Retains the shared database service.</summary>
        /// <param name="service">Service started and stopped with this adapter.</param>
        internal NntpDbServiceHostedAdapter(NntpDbService service)
        {
            ArgumentNullException.ThrowIfNull(service);
            _service = service;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) => _service.StartAsync(cancellationToken);

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => _service.StopAsync(cancellationToken);
    }
}
