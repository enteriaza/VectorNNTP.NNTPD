namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Raised after a connection generation is published as current.
/// </summary>
public sealed class BackFillerRabbitMqConnectionReplacedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BackFillerRabbitMqConnectionReplacedEventArgs"/> class.
    /// </summary>
    /// <param name="generation">Generation that is now current.</param>
    /// <param name="isReplacement">
    /// <see langword="true"/> when this is not the first successful connect of the process.
    /// </param>
    public BackFillerRabbitMqConnectionReplacedEventArgs(long generation, bool isReplacement)
    {
        Generation = generation;
        IsReplacement = isReplacement;
    }

    /// <summary>Gets the generation that is now current.</summary>
    public long Generation { get; }

    /// <summary>Gets a value indicating whether a previous generation was replaced.</summary>
    public bool IsReplacement { get; }
}
