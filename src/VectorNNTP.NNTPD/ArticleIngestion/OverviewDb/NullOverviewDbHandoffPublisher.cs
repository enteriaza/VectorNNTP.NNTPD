namespace VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;

/// <summary>
/// Test-only OverviewDB publisher that completes without contacting a broker.
/// Production DI registers <see cref="OverviewDbHandoffPublisher"/> instead.
/// </summary>
internal sealed class NullOverviewDbHandoffPublisher : IOverviewDbHandoffPublisher
{
    /// <summary>Shared succeeding instance used when tests construct the worker directly.</summary>
    public static NullOverviewDbHandoffPublisher Instance { get; } = new();

    private NullOverviewDbHandoffPublisher()
    {
    }

    /// <inheritdoc />
    public Task PublishConfirmedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
