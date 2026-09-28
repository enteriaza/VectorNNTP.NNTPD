namespace VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;

/// <summary>
/// One-way OverviewDB RabbitMQ handoff. Completing the call means the broker
/// confirmed the publication. There is no OverviewDB RPC, reply, or database
/// round-trip.
/// </summary>
public interface IOverviewDbHandoffPublisher
{
    /// <summary>
    /// Publishes a compact OverviewDB payload to <c>overviewdb.queue</c> and
    /// waits for a publisher confirmation.
    /// </summary>
    /// <param name="payload">Protobuf overview message. Must not include the article body.</param>
    /// <param name="cancellationToken">Token used to cancel the publish or confirm wait.</param>
    Task PublishConfirmedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}
