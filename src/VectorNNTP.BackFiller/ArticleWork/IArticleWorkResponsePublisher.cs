namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Intent to publish a terminal Article Work RPC response.
/// </summary>
/// <remarks>
/// Phase 3 records this intent only. Broker publish and publisher confirms are deferred.
/// Identities are never invented: missing parse fields stay null.
/// </remarks>
/// <param name="Outcome">Terminal protocol outcome.</param>
/// <param name="RequestId">Logical request identity when recovered.</param>
/// <param name="MessageId">Exact Message-ID when recovered.</param>
/// <param name="Backbone">JSON backbone when recovered.</param>
/// <param name="CorrelationId">AMQP correlation to echo on the response.</param>
/// <param name="ReplyTo">AMQP reply destination.</param>
/// <param name="Error">Failure reason for terminal non-success outcomes.</param>
public sealed record ArticleWorkResponseIntent(
    ArticleWorkOutcome Outcome,
    Guid? RequestId,
    string? MessageId,
    string? Backbone,
    string? CorrelationId,
    string? ReplyTo,
    string? Error);

/// <summary>
/// Phase 3 response-publish seam. Does not own the consumer channel or connection.
/// </summary>
public interface IArticleWorkResponsePublisher
{
    /// <summary>
    /// Attempts to publish a terminal response. Phase 3 implementations must not talk to the broker.
    /// </summary>
    /// <param name="intent">Response identities and outcome.</param>
    /// <param name="cancellationToken">Token used to cancel the attempt.</param>
    /// <returns>A task that completes when the seam has recorded or rejected the intent.</returns>
    Task PublishAsync(ArticleWorkResponseIntent intent, CancellationToken cancellationToken);
}

/// <summary>
/// In-process recorder used until real response publishing is implemented.
/// </summary>
public sealed class RecordingArticleWorkResponsePublisher : IArticleWorkResponsePublisher
{
    private readonly List<ArticleWorkResponseIntent> _published = [];

    /// <summary>Gets recorded publish intents in admission order.</summary>
    public IReadOnlyList<ArticleWorkResponseIntent> Published
    {
        get
        {
            lock (_published)
            {
                return [.. _published];
            }
        }
    }

    /// <summary>When set, the next publish throws this exception.</summary>
    public Exception? PublishException { get; set; }

    /// <inheritdoc />
    public Task PublishAsync(ArticleWorkResponseIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        if (PublishException is not null)
        {
            throw PublishException;
        }

        lock (_published)
        {
            _published.Add(intent);
        }

        return Task.CompletedTask;
    }
}
