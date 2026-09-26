namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>Broker close details for a single AMQP connection.</summary>
/// <param name="ReplyCode">AMQP reply code supplied by the close.</param>
/// <param name="ReplyText">AMQP reply text supplied by the close.</param>
/// <param name="Initiator">Who initiated the close (<c>Peer</c>, <c>Library</c>, or <c>Application</c>).</param>
public sealed record RabbitMqConnectionLostEventArgs(
    ushort ReplyCode,
    string ReplyText,
    string Initiator);
