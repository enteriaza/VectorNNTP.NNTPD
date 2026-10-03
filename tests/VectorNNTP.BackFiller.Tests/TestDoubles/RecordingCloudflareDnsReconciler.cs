using VectorNNTP.Common.Cloudflare;
using VectorNNTP.Common.Networking;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    internal sealed class RecordingCloudflareDnsReconciler : ICloudflareDnsReconciler
    {
        private readonly object _gate = new();

        public List<(string ZoneId, string Fqdn)> ReconcileCalls { get; } = [];

        public List<(string ZoneId, string Fqdn)> RemoveCalls { get; } = [];

        public Exception? ReconcileException { get; set; }

        public Task ReconcileAsync(
            string zoneId,
            string fqdn,
            ResolvedBindAddresses desired,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            ArgumentNullException.ThrowIfNull(desired);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                ReconcileCalls.Add((zoneId, fqdn));
            }

            if (ReconcileException is not null)
            {
                throw ReconcileException;
            }

            return Task.CompletedTask;
        }

        public Task RemoveAllRecordsForFqdnAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                RemoveCalls.Add((zoneId, fqdn));
            }

            return Task.CompletedTask;
        }
    }
}
