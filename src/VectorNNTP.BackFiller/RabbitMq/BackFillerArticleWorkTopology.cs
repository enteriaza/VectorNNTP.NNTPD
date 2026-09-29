using RabbitMQ.Client;

namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Declares the durable quorum fanout ArticleWork topology for one BackFiller backbone.
/// </summary>
/// <remarks>
/// BackFiller owns per-backbone <c>backfiller.{backbone}</c> exchange, queue, and binding.
/// Declaration is idempotent and never deletes or mutates incompatible existing entities.
/// Topology is created when a backbone becomes usable (before Article Work consumers start)
/// and is retained when the backbone later becomes inactive.
/// </remarks>
public static class BackFillerArticleWorkTopology
{
    /// <summary>Broker argument that selects the RabbitMQ queue type.</summary>
    public const string QueueTypeArgumentName = "x-queue-type";

    /// <summary>Required queue type for durable BackFiller ArticleWork provider queues.</summary>
    public const string QuorumQueueType = "quorum";

    /// <summary>
    /// Declares the fanout exchange, durable quorum queue, and binding for
    /// <paramref name="backbone"/> using existing name/routing semantics.
    /// </summary>
    /// <param name="channel">Caller-owned channel on the current connection generation.</param>
    /// <param name="backbone">Provider backbone label (for example <c>Giganews</c>).</param>
    /// <param name="cancellationToken">Token used to cancel declaration.</param>
    public static async Task DeclareProviderEndpointAsync(
        IBackFillerRabbitMqChannel channel,
        string backbone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(backbone);

        var name = BackFillerRabbitMqTopology.ComposeProviderEntity(backbone);
        await channel.ExchangeDeclareAsync(
                name,
                ExchangeType.Fanout,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken)
            .ConfigureAwait(false);

        await channel.QueueDeclareAsync(
                name,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [QueueTypeArgumentName] = QuorumQueueType,
                },
                cancellationToken)
            .ConfigureAwait(false);

        await channel.QueueBindAsync(
                name,
                name,
                name,
                arguments: null,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
