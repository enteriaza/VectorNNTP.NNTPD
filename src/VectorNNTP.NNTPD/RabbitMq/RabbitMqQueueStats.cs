namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>Broker-reported queue statistics from a passive declare.</summary>
/// <param name="MessageCount">Ready message count.</param>
/// <param name="ConsumerCount">Active consumer count.</param>
public readonly record struct RabbitMqQueueStats(uint MessageCount, uint ConsumerCount);
