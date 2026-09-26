namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Short-lived RabbitMQ channel used only to declare application topology.
/// </summary>
/// <remarks>
/// The channel must not publish, consume, delete, purge, or mutate existing
/// entities beyond RabbitMQ's normal idempotent declare/bind operations.
/// </remarks>
public interface IRabbitMqTopologyChannel : IAsyncDisposable
{
    /// <summary>Declares an exchange using RabbitMQ's idempotent declare semantics.</summary>
    /// <param name="exchange">Exchange name to declare.</param>
    /// <param name="type">Exchange type, such as <c>fanout</c>.</param>
    /// <param name="durable"><see langword="true"/> when the exchange survives broker restart.</param>
    /// <param name="autoDelete"><see langword="true"/> when the exchange is deleted when unused.</param>
    /// <param name="arguments">Optional exchange arguments; <see langword="null"/> when none apply.</param>
    /// <param name="cancellationToken">Token used to cancel the declaration.</param>
    Task ExchangeDeclareAsync(
        string exchange,
        string type,
        bool durable,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken);

    /// <summary>Declares a queue using RabbitMQ's idempotent declare semantics.</summary>
    /// <param name="queue">Queue name to declare.</param>
    /// <param name="durable"><see langword="true"/> when the queue survives broker restart.</param>
    /// <param name="exclusive"><see langword="true"/> when the queue is exclusive to one connection.</param>
    /// <param name="autoDelete"><see langword="true"/> when the queue is deleted when unused.</param>
    /// <param name="arguments">Optional queue arguments; quorum queues require <c>x-queue-type=quorum</c>.</param>
    /// <param name="cancellationToken">Token used to cancel the declaration.</param>
    Task QueueDeclareAsync(
        string queue,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken);

    /// <summary>Binds a queue to an exchange using RabbitMQ's idempotent bind semantics.</summary>
    /// <param name="queue">Queue name to bind.</param>
    /// <param name="exchange">Exchange providing the messages.</param>
    /// <param name="routingKey">Routing key used for the binding.</param>
    /// <param name="arguments">Optional binding arguments; <see langword="null"/> when none apply.</param>
    /// <param name="cancellationToken">Token used to cancel the bind.</param>
    Task QueueBindAsync(
        string queue,
        string exchange,
        string routingKey,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken);
}
