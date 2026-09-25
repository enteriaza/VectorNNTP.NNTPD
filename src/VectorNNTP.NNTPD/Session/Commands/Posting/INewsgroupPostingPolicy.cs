namespace VectorNNTP.NNTPD.Session.Commands.Posting;

/// <summary>
/// Newsgroup existence, posting-authorization, and moderation boundary used by POST.
/// </summary>
/// <remarks>
/// VectorNNTP does not currently have a newsgroup catalogue. Implementations must not
/// invent group existence. <see cref="SyntaxOnlyNewsgroupPostingPolicy"/> is the
/// temporary production policy until a catalogue exists.
/// </remarks>
public interface INewsgroupPostingPolicy
{
    /// <summary>
    /// Evaluates posting authorization for already syntax-validated newsgroup names.
    /// </summary>
    /// <param name="groups">Distinct, syntactically valid newsgroup names.</param>
    /// <param name="approvedHeaderPresent">
    /// Whether the article contains a structurally valid <c>Approved:</c> field.
    /// This is not an unconditional moderation bypass.
    /// </param>
    /// <returns>Catalogue / authorization outcome. Does not re-validate group-name syntax.</returns>
    NewsgroupPostingEvaluation Evaluate(ReadOnlySpan<string> groups, bool approvedHeaderPresent);
}

/// <summary>Result of <see cref="INewsgroupPostingPolicy.Evaluate"/>.</summary>
/// <param name="Status">Whether a catalogue decision was available.</param>
/// <param name="Failure">Set when posting is rejected by policy.</param>
public readonly record struct NewsgroupPostingEvaluation(
    NewsgroupCatalogStatus Status,
    PostingFailure? Failure);

/// <summary>Availability of newsgroup existence/moderation state.</summary>
public enum NewsgroupCatalogStatus
{
    /// <summary>
    /// Group existence and moderation state are not available. This is not a confirmed
    /// existence match and must not be logged or treated as such.
    /// </summary>
    CatalogUnavailable = 0,

    /// <summary>Policy rejected posting to one or more groups.</summary>
    Rejected = 1,
}

/// <summary>
/// Temporary POST newsgroup policy: syntax-valid groups are accepted without asserting
/// that the groups exist or that moderation state is known.
/// </summary>
/// <remarks>
/// This is an explicit stand-in until a newsgroup catalogue exists. It does not
/// consult any in-memory group database and does not treat <c>Approved:</c> as
/// authorization to post to a moderated group.
/// </remarks>
public sealed class SyntaxOnlyNewsgroupPostingPolicy : INewsgroupPostingPolicy
{
    /// <summary>Shared instance.</summary>
    public static SyntaxOnlyNewsgroupPostingPolicy Instance { get; } = new();

    /// <inheritdoc />
    public NewsgroupPostingEvaluation Evaluate(ReadOnlySpan<string> groups, bool approvedHeaderPresent)
    {
        _ = groups;
        _ = approvedHeaderPresent;
        return new NewsgroupPostingEvaluation(NewsgroupCatalogStatus.CatalogUnavailable, Failure: null);
    }
}
