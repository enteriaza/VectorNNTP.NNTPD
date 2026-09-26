using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Networking;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

internal sealed class NoOpCloudflareDnsReconciler : ICloudflareDnsReconciler
{
    public Task ReconcileAsync(
        string zoneId,
        string fqdn,
        ResolvedBindAddresses desired,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RemoveAllRecordsForFqdnAsync(
        string zoneId,
        string fqdn,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout = null) =>
        Task.CompletedTask;
}
