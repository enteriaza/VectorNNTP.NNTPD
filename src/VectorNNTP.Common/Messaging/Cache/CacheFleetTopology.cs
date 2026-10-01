namespace VectorNNTP.Common.Messaging.Cache;

/// <summary>
/// Shared NNTPD ↔ StorageServer cache-fleet RabbitMQ topology names and timing constants.
/// </summary>
/// <remarks>
/// Application-specific contracts shared by StorageServer and NNTPD. This is not a generic
/// messaging framework. Topology declaration and consume/publish ownership remain in each
/// application.
/// <para>
/// <c>cache.broadcast</c> — StorageServer → NNTPD fleet capacity advertisements.<br/>
/// <c>cache.requests</c> — NNTPD → StorageServer fleet article-presence query (fanout exchange
/// only; each StorageServer binds an ephemeral <c>cache.&lt;storage-fqdn&gt;</c> queue).
/// </para>
/// </remarks>
public static class CacheFleetTopology
{
    /// <summary>Fanout exchange that carries StorageServer capacity advertisements.</summary>
    public const string BroadcastExchangeName = "cache.broadcast";

    /// <summary>
    /// Fanout exchange that carries NNTPD "who has article X?" lookup requests to every
    /// StorageServer. There is no shared competing-consumer work queue with this name.
    /// </summary>
    public const string RequestsExchangeName = "cache.requests";

    /// <summary>Exchange type for broadcast and request fanout exchanges.</summary>
    public const string FanoutExchangeType = "fanout";

    /// <summary>Exchange type for <see cref="BroadcastExchangeName"/>.</summary>
    public const string BroadcastExchangeType = FanoutExchangeType;

    /// <summary>Prefix for ephemeral per-instance queues (<c>cache.&lt;fqdn&gt;</c>).</summary>
    public const string InstanceQueuePrefix = "cache.";

    /// <summary>Per-message AMQP expiration for advertisements (milliseconds, as a string).</summary>
    public const string AdvertisementExpirationMilliseconds = "3000";

    /// <summary>
    /// Per-message AMQP expiration for article-lookup requests and responses (milliseconds).
    /// Matches <see cref="LookupTimeout"/>.
    /// </summary>
    public const string LookupExpirationMilliseconds = "1000";

    /// <summary>StorageServer advertisement publish interval.</summary>
    public static readonly TimeSpan AdvertisementInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// NNTPD registry liveness window. Matches advertisement message TTL. A StorageServer that
    /// disappears without a Draining announcement becomes inactive when this window expires.
    /// </summary>
    public static readonly TimeSpan LivenessWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// End-to-end StorageServer article-presence lookup budget measured from request start.
    /// Aligns with ArticleWork request AMQP expiration (1s) for a short hot-path ask.
    /// </summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Builds the ephemeral per-instance queue name <c>cache.{fqdn}</c> after normalization.
    /// </summary>
    /// <param name="fqdn">
    /// Generated application FQDN (for example <c>nntpd01.usenet.ninja</c> or
    /// <c>cache01.usenet.ninja</c>).
    /// </param>
    /// <returns>Trimmed invariant-lowercase queue name.</returns>
    public static string BuildInstanceQueueName(string fqdn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        var normalizedFqdn = fqdn.Trim().ToLowerInvariant();
        return $"{InstanceQueuePrefix}{normalizedFqdn}";
    }

    /// <summary>Builds the ephemeral NNTPD <c>cache.broadcast</c> consumer queue name.</summary>
    public static string BuildNntpdBroadcastQueueName(string nntpdFqdn) =>
        BuildInstanceQueueName(nntpdFqdn);

    /// <summary>Builds the ephemeral StorageServer <c>cache.requests</c> consumer queue name.</summary>
    public static string BuildStorageServerRequestQueueName(string storageServerFqdn) =>
        BuildInstanceQueueName(storageServerFqdn);
}
