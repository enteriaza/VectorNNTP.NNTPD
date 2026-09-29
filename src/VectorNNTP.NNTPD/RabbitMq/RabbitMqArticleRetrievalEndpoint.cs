using RabbitMQ.Client;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// One declare-only article-retrieval exchange, durable quorum queue, and binding.
/// </summary>
/// <param name="ExchangeName">Exchange that receives article-retrieval requests.</param>
/// <param name="ExchangeType">RabbitMQ exchange type. Article-retrieval uses fanout.</param>
/// <param name="ExchangeDurable">Whether the exchange survives broker restart.</param>
/// <param name="ExchangeAutoDelete">Whether the exchange is auto-deleted when unused.</param>
/// <param name="QueueName">Queue bound to the exchange.</param>
/// <param name="QueueDurable">Whether the queue survives broker restart.</param>
/// <param name="QueueExclusive">Whether the queue is exclusive to a single connection.</param>
/// <param name="QueueAutoDelete">Whether the queue is auto-deleted when unused.</param>
/// <param name="RoutingKey">Routing key used when binding the queue to the exchange.</param>
/// <param name="QueueArguments">
/// Queue arguments applied during declaration. Durable article-retrieval work queues
/// require <c>x-queue-type=quorum</c>.
/// </param>
internal sealed record RabbitMqArticleRetrievalEndpoint(
    string ExchangeName,
    string ExchangeType,
    bool ExchangeDurable,
    bool ExchangeAutoDelete,
    string QueueName,
    bool QueueDurable,
    bool QueueExclusive,
    bool QueueAutoDelete,
    string RoutingKey,
    IReadOnlyDictionary<string, object?> QueueArguments);

/// <summary>
/// Shared factory for NNTPD article-retrieval broker entities.
/// </summary>
/// <remarks>
/// BackFiller backbone endpoints and the internal <c>backfiller.storage</c> endpoint share
/// these broker semantics. Storage is not a BackFiller provider. Exclusive auto-delete
/// ArticleWork RPC reply queues are declared separately and remain non-durable classic.
/// </remarks>
internal static class RabbitMqArticleRetrievalEndpoints
{
    /// <summary>Broker argument that selects the RabbitMQ queue type.</summary>
    internal const string QueueTypeArgumentName = "x-queue-type";

    /// <summary>Required queue type for every durable article-retrieval work queue.</summary>
    internal const string QuorumQueueType = "quorum";

    /// <summary>
    /// Builds a durable fanout exchange, durable quorum queue, and binding that share
    /// the normalized <paramref name="entityName"/> as the exchange, queue, and routing key.
    /// </summary>
    internal static RabbitMqArticleRetrievalEndpoint CreateFanoutQuorumBinding(string entityName)
    {
        var name = RabbitMqTopologyNames.Normalize(entityName);
        return new RabbitMqArticleRetrievalEndpoint(
            ExchangeName: name,
            ExchangeType: ExchangeType.Fanout,
            ExchangeDurable: true,
            ExchangeAutoDelete: false,
            QueueName: name,
            QueueDurable: true,
            QueueExclusive: false,
            QueueAutoDelete: false,
            RoutingKey: name,
            QueueArguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [QueueTypeArgumentName] = QuorumQueueType,
            });
    }
}

/// <summary>
/// Complete article-retrieval topology declared by <see cref="RabbitMqTopologyService"/>.
/// </summary>
/// <remarks>
/// NNTPD owns only the internal <c>backfiller.storage</c> endpoint. Per-backbone
/// <c>backfiller.*</c> provider topology is declared by VectorNNTP.BackFiller when a
/// backbone becomes usable.
/// </remarks>
internal static class ArticleRetrievalTopology
{
    /// <summary>Required NNTPD-owned article-retrieval endpoints (storage only).</summary>
    internal static IReadOnlyList<RabbitMqArticleRetrievalEndpoint> Required { get; } =
        [
            StorageArticleRetrievalTopology.Definition,
        ];
}
