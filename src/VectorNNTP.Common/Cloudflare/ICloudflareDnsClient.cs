namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>
    /// Cloudflare DNS Records API client scoped to a single zone.
    /// </summary>
    /// <remarks>
    /// Implementations must not log API keys or authenticated request headers.
    /// Unsuccessful API responses must surface as failures (never as empty success).
    /// </remarks>
    internal interface ICloudflareDnsClient
    {
        /// <summary>
        /// Lists all DNS records for the exact hostname and type, following pagination.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone identifier.</param>
        /// <param name="fqdn">Fully-qualified DNS name (no trailing dot required).</param>
        /// <param name="type">Record type (<c>A</c> or <c>AAAA</c>).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>All matching records for the exact name and type.</returns>
        Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
            string zoneId,
            string fqdn,
            string type,
            CancellationToken cancellationToken);

        /// <summary>
        /// Lists all DNS records for the exact hostname across every record type, following pagination.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone identifier.</param>
        /// <param name="fqdn">Fully-qualified DNS name (no trailing dot required).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// All records whose DNS name is exactly <paramref name="fqdn"/> (after normalization).
        /// Does not include parent, child/subdomain, or other hostnames.
        /// </returns>
        Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken);

        /// <summary>
        /// Creates a DNS record.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone identifier.</param>
        /// <param name="request">Record body. Managed A/AAAA callers set TTL and proxy explicitly.</param>
        /// <param name="cancellationToken">Cancellation token. Canceling a mutation leaves the remote outcome uncertain.</param>
        /// <returns>The record returned by Cloudflare.</returns>
        /// <exception cref="CloudflareDnsException">The API call failed or returned success without a record payload.</exception>
        Task<CloudflareDnsRecord> CreateRecordAsync(
            string zoneId,
            CloudflareDnsRecordWriteRequest request,
            CancellationToken cancellationToken);

        /// <summary>
        /// Updates an existing DNS record.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone identifier.</param>
        /// <param name="recordId">Cloudflare record identifier.</param>
        /// <param name="request">Replacement record body.</param>
        /// <param name="cancellationToken">Cancellation token. Canceling a mutation leaves the remote outcome uncertain.</param>
        /// <returns>The record returned by Cloudflare.</returns>
        /// <exception cref="CloudflareDnsException">The API call failed or returned success without a record payload.</exception>
        Task<CloudflareDnsRecord> UpdateRecordAsync(
            string zoneId,
            string recordId,
            CloudflareDnsRecordWriteRequest request,
            CancellationToken cancellationToken);

        /// <summary>
        /// Deletes an existing DNS record.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone identifier.</param>
        /// <param name="recordId">Cloudflare record identifier.</param>
        /// <param name="cancellationToken">Cancellation token. Canceling a mutation leaves the remote outcome uncertain.</param>
        /// <returns>A task that completes when Cloudflare reports success. A returned record id is not required.</returns>
        /// <exception cref="CloudflareDnsException">The API call failed.</exception>
        Task DeleteRecordAsync(
            string zoneId,
            string recordId,
            CancellationToken cancellationToken);
    }
}
