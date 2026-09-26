namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Event payload published after the connection service establishes a RabbitMQ connection generation.
/// </summary>
/// <param name="ConnectionGeneration">Monotonic application-managed generation number assigned to the newly active connection.</param>
/// <param name="IsReplacement"><see langword="true"/> when a prior generation existed and later consumers must drop stale channels.</param>
public sealed record RabbitMqConnectionReplacedEventArgs(
    long ConnectionGeneration,
    bool IsReplacement);
