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
/// durable non-exclusive non-auto-delete quorum queue, bound with the same name as
/// the routing key. NNTPD does not consume this queue.
/// </para>
/// </remarks>
internal static class StorageArticleRetrievalTopology
{
    /// <summary>Exchange, queue, and routing-key name for storage retrieval requests.</summary>
    internal const string EntityName = "backfiller.storage";

    /// <summary>Single storage request endpoint.</summary>
    internal static RabbitMqArticleRetrievalEndpoint Definition { get; } =
        RabbitMqArticleRetrievalEndpoints.CreateFanoutQuorumBinding(EntityName);
}
