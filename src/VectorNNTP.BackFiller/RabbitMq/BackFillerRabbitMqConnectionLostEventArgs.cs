namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Details of a broker or peer connection close observed by a generation.
/// </summary>
public sealed class BackFillerRabbitMqConnectionLostEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BackFillerRabbitMqConnectionLostEventArgs"/> class.
    /// </summary>
    /// <param name="replyCode">AMQP reply code reported by the client.</param>
    /// <param name="replyText">AMQP reply text. Must not contain credentials.</param>
    /// <param name="initiator">Who initiated the close (broker, library, or application).</param>
    public BackFillerRabbitMqConnectionLostEventArgs(ushort replyCode, string replyText, string initiator)
    {
        ReplyCode = replyCode;
        ReplyText = replyText ?? string.Empty;
        Initiator = initiator ?? string.Empty;
    }

    /// <summary>Gets the AMQP reply code.</summary>
    public ushort ReplyCode { get; }

    /// <summary>Gets the AMQP reply text.</summary>
    public string ReplyText { get; }

    /// <summary>Gets the close initiator description.</summary>
    public string Initiator { get; }
}
