using RabbitMQ.Client;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Immutable BackFiller-compatible topology for one article-retrieval provider.
/// </summary>
/// <param name="Provider">Canonical provider identifier. Casing is preserved.</param>
/// <param name="ExchangeName">Exchange that receives article-retrieval work for the provider.</param>
/// <param name="ExchangeType">RabbitMQ exchange type. BackFiller uses fanout.</param>
/// <param name="ExchangeDurable">Whether the exchange survives broker restart.</param>
/// <param name="ExchangeAutoDelete">Whether the exchange is auto-deleted when unused.</param>
/// <param name="QueueName">Queue bound to the provider exchange.</param>
/// <param name="QueueDurable">Whether the queue survives broker restart.</param>
/// <param name="QueueExclusive">Whether the queue is exclusive to a single connection.</param>
/// <param name="QueueAutoDelete">Whether the queue is auto-deleted when unused.</param>
/// <param name="RoutingKey">Routing key used when binding the queue to the exchange.</param>
/// <param name="QueueArguments">Queue arguments applied during declaration. Must include <c>x-queue-type=quorum</c>.</param>
internal sealed record BackfillArticleRetrievalTopologyDefinition(
    string Provider,
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
/// Fixed article-retrieval topology shared with VectorNNTP.BackFiller.
/// </summary>
/// <remarks>
/// <para>
/// Provider identifiers are application constants, not configuration. The twelve names
/// match BackFiller's backbone set. Entity names follow BackFiller's legacy rule
/// <c>grabbers.{provider.ToLowerInvariant()}</c> for the exchange, queue, and routing key.
/// </para>
/// <para>
/// Every queue is declared as a RabbitMQ quorum queue. NNTPD does not consume these
/// queues; it will later publish RPC article-retrieval requests onto the same topology.
/// </para>
/// </remarks>
internal static class BackfillArticleRetrievalTopology
{
    /// <summary>Broker argument that selects the RabbitMQ queue type.</summary>
    internal const string QueueTypeArgumentName = "x-queue-type";

    /// <summary>Required queue type for every article-retrieval queue.</summary>
    internal const string QuorumQueueType = "quorum";

    /// <summary>
    /// Canonical BackFiller provider identifiers. Casing is significant and is not normalized.
    /// </summary>
    internal static readonly string[] Providers =
    [
        "Abavia",
        "Altopia",
        "BaseIP",
        "Eweka",
        "Elbracht",
        "Giganews",
        "GTT",
        "Highwinds",
        "ItsHosted",
        "Novia",
        "UExpress",
        "UsenetNode1",
    ];

    /// <summary>Complete article-retrieval topology in provider-list order.</summary>
    internal static IReadOnlyList<BackfillArticleRetrievalTopologyDefinition> Definitions { get; } =
        BuildDefinitions(Providers);

    /// <summary>
    /// Builds topology definitions for the supplied provider identifiers.
    /// </summary>
    /// <param name="providers">Provider names whose exchanges and queues must exist.</param>
    /// <returns>One definition per non-blank unique provider, preserving first-seen order.</returns>
    internal static IReadOnlyList<BackfillArticleRetrievalTopologyDefinition> BuildDefinitions(
        IEnumerable<string> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var uniqueProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<BackfillArticleRetrievalTopologyDefinition>();

        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                continue;
            }

            var canonicalProvider = provider.Trim();
            if (!uniqueProviders.Add(canonicalProvider))
            {
                continue;
            }

            var entityName = BuildLegacyProviderEntityName(canonicalProvider);
            definitions.Add(new BackfillArticleRetrievalTopologyDefinition(
                Provider: canonicalProvider,
                ExchangeName: entityName,
                ExchangeType: ExchangeType.Fanout,
                ExchangeDurable: true,
                ExchangeAutoDelete: false,
                QueueName: entityName,
                QueueDurable: true,
                QueueExclusive: false,
                QueueAutoDelete: false,
                RoutingKey: entityName,
                QueueArguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [QueueTypeArgumentName] = QuorumQueueType,
                }));
        }

        return definitions;
    }

    /// <summary>
    /// Builds the BackFiller legacy exchange, queue, and routing-key name for a provider.
    /// </summary>
    /// <param name="provider">Canonical provider identifier.</param>
    /// <returns>The lower-cased legacy entity name in the form <c>grabbers.{provider}</c>.</returns>
    internal static string BuildLegacyProviderEntityName(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return $"grabbers.{provider.Trim().ToLowerInvariant()}";
    }
}
