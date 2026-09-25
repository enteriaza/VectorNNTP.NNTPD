namespace VectorNNTP.NNTPD.Moderation;

/// <summary>
/// Durable boundary that accepts a proto-article for moderator forwarding.
/// </summary>
/// <remarks>
/// POST policy calls this abstraction; it does not send email. SMTP or another
/// delivery implementation can be registered without changing posting authorization.
/// This repository has no mail infrastructure, so the production default reports
/// <see cref="ModerationSubmissionStatus.Unavailable"/>.
/// </remarks>
public interface IModerationSubmissionService
{
    /// <summary>
    /// Offers <paramref name="submission"/> for moderator delivery.
    /// </summary>
    /// <param name="submission">Proto-article and resolved moderator route.</param>
    /// <param name="cancellationToken">Cancels the attempt. Must not be ignored by implementations that wait.</param>
    /// <returns>Accepted, unavailable, or failed. Does not inject the article.</returns>
    ValueTask<ModerationSubmissionResult> SubmitAsync(
        ModerationSubmission submission,
        CancellationToken cancellationToken = default);
}
