using RabbitMQ.Client;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// One declare-only article-retrieval exchange, quorum queue, and binding.
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
/// <param name="QueueArguments">Queue arguments applied during declaration. Must include <c>x-queue-type=quorum</c>.</param>
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
/// BackFiller backbone endpoints and the internal <c>storage.requests</c> endpoint share
/// these broker semantics. They do not share a naming namespace.
/// </remarks>
internal static class RabbitMqArticleRetrievalEndpoints
{
    /// <summary>Broker argument that selects the RabbitMQ queue type.</summary>
    internal const string QueueTypeArgumentName = "x-queue-type";

    /// <summary>Required queue type for every article-retrieval queue.</summary>
    internal const string QuorumQueueType = "quorum";

    /// <summary>
    /// Builds a durable fanout exchange, durable quorum queue, and binding that share
    /// <paramref name="entityName"/> as the exchange, queue, and routing key.
    /// </summary>
    internal static RabbitMqArticleRetrievalEndpoint CreateFanoutQuorumBinding(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        var name = entityName.Trim();
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
/// Twelve BackFiller <c>grabbers.*</c> endpoints plus one internal <c>storage.requests</c>
/// endpoint. Storage is not a BackFiller provider.
/// </remarks>
internal static class ArticleRetrievalTopology
{
    /// <summary>Required endpoints in declaration order: 12 BackFiller, then storage.</summary>
    internal static IReadOnlyList<RabbitMqArticleRetrievalEndpoint> Required { get; } =
        [
            ..BackfillArticleRetrievalTopology.Definitions.Select(static definition => definition.ToEndpoint()),
            StorageArticleRetrievalTopology.Definition,
        ];
}
