namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Broker topology for the one-way OverviewDB ingest handoff.
/// </summary>
/// <remarks>
/// Publishes through the default exchange with routing key
/// <see cref="QueueName"/>. No dedicated exchange, bind, reply queue, or RPC
/// topology is declared.
/// </remarks>
internal static class OverviewDbTopology
{
    /// <summary>
    /// Durable classic queue consumed independently by OverviewDB.
    /// </summary>
    /// <remarks>
    /// Declared without <c>x-queue-type</c> (durable classic). All current VectorNNTP
    /// application queues use classic while the broker is not yet quorum-ready.
    /// </remarks>
    internal const string QueueName = "overviewdb.queue";

    /// <summary>Default-exchange routing key; equal to <see cref="QueueName"/>.</summary>
    internal const string RoutingKey = QueueName;

    /// <summary>AMQP default exchange (empty name).</summary>
    internal const string DefaultExchange = "";

    /// <summary>Per-message AMQP expiration in milliseconds (<c>expiration</c> shortstr).</summary>
    /// <remarks>
    /// The broker interprets this as milliseconds. RabbitMQ discards expired
    /// messages when they reach the head of the queue; Ready counts can still
    /// include expired messages queued behind a non-expired head. This is not a
    /// queue-wide <c>x-message-ttl</c>.
    /// </remarks>
    internal const string ExpirationMilliseconds = "2000";

    /// <summary>
    /// AMQP mandatory flag. Unroutable publications are returned to the publisher
    /// instead of being confirmed and dropped.
    /// </summary>
    internal const bool Mandatory = true;
}
