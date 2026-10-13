using VectorNNTP.Common.Cloudflare;
using VectorNNTP.Common.Networking;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    internal sealed class NoOpCloudflareDnsReconciler : ICloudflareDnsReconciler
    {
        public Task ReconcileAsync(
            string zoneId,
            string fqdn,
            ResolvedBindAddresses desired,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public bool DeletedPreExistingDuringLastReconcile => false;

        public Task RemoveAllRecordsForFqdnAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListRecordIdsAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task DeleteRecordIdsAsync(
            string zoneId,
            string fqdn,
            IReadOnlyCollection<string> recordIds,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null) =>
            Task.CompletedTask;
    }
}
