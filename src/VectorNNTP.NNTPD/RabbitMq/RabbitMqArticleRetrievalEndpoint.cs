using RabbitMQ.Client;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// One declare-only article-retrieval exchange, classic durable queue, and binding.
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
/// Queue arguments applied during declaration. Classic article-retrieval queues omit
/// <c>x-queue-type</c> (broker default classic). Quorum may return later after broker upgrade.
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
/// these broker semantics. Storage is not a BackFiller provider.
/// </remarks>
internal static class RabbitMqArticleRetrievalEndpoints
{
    /// <summary>Broker argument that selects the RabbitMQ queue type when non-default.</summary>
    internal const string QueueTypeArgumentName = "x-queue-type";

    /// <summary>
    /// Quorum type name retained so tests can assert article-retrieval queues do not declare it.
    /// </summary>
    internal const string QuorumQueueType = "quorum";

    /// <summary>
    /// Builds a durable fanout exchange, durable classic queue, and binding that share
    /// the normalized <paramref name="entityName"/> as the exchange, queue, and routing key.
    /// </summary>
    internal static RabbitMqArticleRetrievalEndpoint CreateFanoutClassicBinding(string entityName)
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
            QueueArguments: new Dictionary<string, object?>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Obsolete name retained as a redirect so call sites migrate explicitly.
    /// </summary>
    [Obsolete("Use CreateFanoutClassicBinding; article-retrieval queues are classic until broker upgrade.")]
    internal static RabbitMqArticleRetrievalEndpoint CreateFanoutQuorumBinding(string entityName) =>
        CreateFanoutClassicBinding(entityName);
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
