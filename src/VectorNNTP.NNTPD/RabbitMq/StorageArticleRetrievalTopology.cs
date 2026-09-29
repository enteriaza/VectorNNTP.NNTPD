namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Internal storage article-retrieval request topology.
/// </summary>
/// <remarks>
/// <para>
/// This is not a BackFiller backbone. Entity names are exactly <c>backfiller.storage</c>
/// after <see cref="RabbitMqTopologyNames"/> normalization. The name lives in the
/// <c>backfiller.*</c> namespace but is not generated from the provider list.
/// </para>
/// <para>
/// Broker semantics match the BackFiller article-retrieval endpoints: durable fanout,
/// durable non-exclusive non-auto-delete quorum queue (<c>x-queue-type=quorum</c>),
/// bound with the same name as the routing key. NNTPD does not consume this queue.
/// The sequential Backfill Scheduler only targets provider queues with active consumers;
/// storage is not selected unless a consumer appears.
/// </para>
/// </remarks>
internal static class StorageArticleRetrievalTopology
{
    /// <summary>Exchange, queue, and routing-key name for storage retrieval requests.</summary>
    internal const string EntityName = "backfiller.storage";

    /// <summary>
    /// JSON <c>backbone</c> for storage publications. This is not a BackFiller provider identifier.
    /// </summary>
    internal const string Backbone = "Storage";

    /// <summary>Single storage request endpoint.</summary>
    internal static RabbitMqArticleRetrievalEndpoint Definition { get; } =
        RabbitMqArticleRetrievalEndpoints.CreateFanoutQuorumBinding(EntityName);
}
