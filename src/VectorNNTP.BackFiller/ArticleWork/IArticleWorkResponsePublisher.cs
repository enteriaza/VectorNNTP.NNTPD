namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Intent to publish a terminal Article Work RPC response.
/// </summary>
/// <remarks>
/// Identities are never invented: missing parse fields stay null.
/// <see cref="Uri"/> is the Phase 5 retention <c>cache://</c> value for Success only.
/// The publisher must not reconstruct that URI.
/// </remarks>
/// <param name="Outcome">Terminal protocol outcome.</param>
/// <param name="RequestId">Logical request identity when recovered.</param>
/// <param name="MessageId">Exact Message-ID when recovered.</param>
/// <param name="Backbone">JSON backbone when recovered.</param>
/// <param name="CorrelationId">AMQP correlation to echo on the response.</param>
/// <param name="ReplyTo">AMQP reply destination.</param>
/// <param name="Error">Failure reason for terminal non-success outcomes.</param>
/// <param name="Uri">Retention cache URI for Success. Must be absent otherwise.</param>
public sealed record ArticleWorkResponseIntent(
    ArticleWorkOutcome Outcome,
    Guid? RequestId,
    string? MessageId,
    string? Backbone,
    string? CorrelationId,
    string? ReplyTo,
    string? Error,
    string? Uri = null);

/// <summary>
/// Response-publish seam. Does not own the consumer channel or connection.
/// </summary>
public interface IArticleWorkResponsePublisher
{
    /// <summary>
    /// Gets whether a Success publish is a real RPC publication that may be followed by ACK.
    /// The recording seam returns <see langword="false"/> so tests can leave Success pending.
    /// The hosted publisher returns <see langword="true"/>.
    /// </summary>
    bool CompletesSuccessPublication { get; }

    /// <summary>
    /// Publishes a terminal response and waits until the broker confirms, or throws.
    /// Returning is not permission to ACK; the pipeline must re-check the original settlement context.
    /// </summary>
    /// <param name="intent">Response identities and outcome.</param>
    /// <param name="cancellationToken">Token used to cancel the attempt.</param>
    /// <returns>A task that completes when publication is confirmed or the seam has recorded the intent.</returns>
    Task PublishAsync(ArticleWorkResponseIntent intent, CancellationToken cancellationToken);
}

/// <summary>
/// In-process recorder used by tests that do not exercise broker publication.
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

    /// <inheritdoc />
    public bool CompletesSuccessPublication { get; set; }

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
