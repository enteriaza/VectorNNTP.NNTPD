namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>
    /// Publishes this process's DNS records after its listener has bound.
    /// </summary>
    public interface IListenerDnsPublication
    {
        /// <summary>
        /// Reconciles the configured FQDN. The caller invokes this only after a successful bind.
        /// </summary>
        /// <param name="cancellationToken">Cancels publication. Does not grant cleanup of another process's records.</param>
        /// <returns>A task that completes when publication has been verified or has failed closed.</returns>
        Task PublishAfterListenerBoundAsync(CancellationToken cancellationToken);
    }
}
