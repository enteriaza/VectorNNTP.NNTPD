namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>
/// Cloudflare DNS Records API client scoped to a single zone.
/// </summary>
/// <remarks>
/// Implementations must not log API keys or authenticated request headers.
/// Unsuccessful API responses must surface as failures (never as empty success).
/// </remarks>
public interface ICloudflareDnsClient
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
    Task<CloudflareDnsRecord> CreateRecordAsync(
        string zoneId,
        CloudflareDnsRecordWriteRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates an existing DNS record.
    /// </summary>
    Task<CloudflareDnsRecord> UpdateRecordAsync(
        string zoneId,
        string recordId,
        CloudflareDnsRecordWriteRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an existing DNS record.
    /// </summary>
    Task DeleteRecordAsync(
        string zoneId,
        string recordId,
        CancellationToken cancellationToken);
}
