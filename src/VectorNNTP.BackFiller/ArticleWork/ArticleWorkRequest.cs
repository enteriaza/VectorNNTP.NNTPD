namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Validated v1 Article Work application payload.
/// </summary>
/// <param name="Version">Application protocol version. Always <c>1</c> for accepted work.</param>
/// <param name="RequestId">Logical lookup identity. Distinct from AMQP <c>CorrelationId</c> and delivery tag.</param>
/// <param name="MessageId">Exact accepted Message-ID, including angle brackets.</param>
/// <param name="Backbone">JSON backbone value as supplied (not case-folded).</param>
public sealed record ArticleWorkRequest(
    int Version,
    Guid RequestId,
    string MessageId,
    string Backbone);
