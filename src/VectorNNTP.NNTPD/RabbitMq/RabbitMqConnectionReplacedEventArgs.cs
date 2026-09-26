namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>Published after <see cref="RabbitMqService"/> installs a connection generation.</summary>
/// <param name="ConnectionGeneration">Monotonic generation assigned to the newly current connection.</param>
/// <param name="IsReplacement">
/// <see langword="true"/> when a prior generation existed and callers must drop work tied to it.
/// </param>
public sealed record RabbitMqConnectionReplacedEventArgs(
    long ConnectionGeneration,
    bool IsReplacement);
