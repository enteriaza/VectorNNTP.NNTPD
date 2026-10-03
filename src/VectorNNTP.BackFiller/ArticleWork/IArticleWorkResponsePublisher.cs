namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Intent to publish a terminal Article Work RPC response.
/// </summary>
/// <remarks>
/// Identities are never invented: missing parse fields stay null.
/// <see cref="Fqdn"/> and <see cref="VatpPort"/> are the Success dial endpoint.
/// The publisher must not encode them into a URI.
/// </remarks>
/// <param name="Outcome">Terminal protocol outcome.</param>
/// <param name="RequestId">Logical request identity when recovered; otherwise null.</param>
/// <param name="MessageId">Exact Message-ID when recovered.</param>
/// <param name="Backbone">JSON backbone when recovered.</param>
/// <param name="CorrelationId">AMQP correlation to echo on the response.</param>
/// <param name="ReplyTo">AMQP reply destination.</param>
/// <param name="Error">Failure reason for terminal non-success outcomes.</param>
/// <param name="Fqdn">BackFiller FQDN for Success. Must be absent otherwise.</param>
/// <param name="VatpPort">TLS VATP listen port for Success. Must be absent otherwise.</param>
/// <param name="ArticleIdHex">64-char lowercase BLAKE3 ArtId hex for Success. Must be absent otherwise.</param>
internal sealed record ArticleWorkResponseIntent(
    ArticleWorkOutcome Outcome,
    Guid? RequestId,
    string? MessageId,
    string? Backbone,
    string? CorrelationId,
    string? ReplyTo,
    string? Error,
    string? Fqdn = null,
    int? VatpPort = null,
    string? ArticleIdHex = null);

/// <summary>
/// Response-publish seam. Does not own the consumer channel or connection.
/// </summary>
internal interface IArticleWorkResponsePublisher
{
    /// <summary>
    /// Gets whether Success publishing is a real RPC publication that may be followed by ACK.
    /// The hosted publisher returns <see langword="true"/>.
    /// A test double may return <see langword="false"/> so Success can remain pending.
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
